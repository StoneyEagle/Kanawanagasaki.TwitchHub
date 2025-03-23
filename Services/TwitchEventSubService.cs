namespace Kanawanagasaki.TwitchHub.Services;

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Models;

public class TwitchEventSubService(ILogger<TwitchEventSubService> _logger, TwitchAuthService _twitchAuth, TwitchApiService _twitchApi) : BackgroundService
{
    public delegate void WsNotification(Guid authUuid, JsonElement json);
    public event WsNotification? OnWsNotification;

    private readonly ConcurrentDictionary<string, EventSubState> _userIdToWsClient = new();
    private CancellationTokenSource _loopTaskCTS = new();
    private readonly object _loopTaskCTSLock = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Dictionary<EventSubState, Task> stateToTask = new();

        while (!ct.IsCancellationRequested)
        {
            if (_userIdToWsClient.IsEmpty)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            try
            {
                foreach (EventSubState? state in _userIdToWsClient.Values)
                {
                    if (state.WsClient is null || (state.WsClient.State != WebSocketState.Open && state.WsClient.State != WebSocketState.Connecting))
                        await state.ReconnectWs(ct);
                    if (!stateToTask.ContainsKey(state) || stateToTask[state].IsCompleted)
                        stateToTask[state] = ProcessEventSub(state, ct);
                }

                await Task.WhenAny([.. stateToTask.Values, Task.Run(_loopTaskCTS.Token.WaitHandle.WaitOne)]);
            }
            catch (Exception e)
            {
                _logger.LogError(e, null);
            }
            finally
            {
                lock (_loopTaskCTSLock)
                {
                    _loopTaskCTS.Dispose();
                    _loopTaskCTS = new();
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private async Task ProcessEventSub(EventSubState state, CancellationToken ct)
    {
        ClientWebSocket? ws = state.WsClient;
        if (ws is null)
            return;

        byte[] buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Connecting)
                await Task.Delay(100);
            _logger.LogInformation("[{AuthUsername}] Connected to twitch event sub", state.AuthUsername);
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult res = await ws.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (res.CloseStatus.HasValue)
                {
                    break;
                }

                if (res.MessageType == WebSocketMessageType.Binary)
                    continue;
                else if (res.MessageType == WebSocketMessageType.Text)
                {
                    StringBuilder message = new();
                    message.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));

                    while (!res.EndOfMessage)
                    {
                        res = await ws.ReceiveAsync(new(buffer), ct);
                        message.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    }

                    try
                    {
                        JsonElement json = JsonSerializer.Deserialize<JsonElement>(message.ToString());
                        if (json.TryGetProperty("metadata", out JsonElement metadataProp)
                            && metadataProp.TryGetProperty("message_type", out JsonElement messageTypeProp)
                            && messageTypeProp.ValueKind == JsonValueKind.String)
                        {
                            string? messageType = messageTypeProp.GetString();
                            switch (messageType)
                            {
                                case "session_welcome":
                                    {
                                        if (!json.TryGetProperty("payload", out JsonElement payloadProp))
                                            return;
                                        if (!payloadProp.TryGetProperty("session", out JsonElement sessionProp))
                                            return;
                                        if (!sessionProp.TryGetProperty("id", out JsonElement idProp))
                                            return;
                                        if (idProp.ValueKind != JsonValueKind.String)
                                            return;

                                        TwitchAuthModel? auth = await _twitchAuth.GetRestoredByUuid(state.AuthUuid);
                                        if (auth is null)
                                            break;

                                        string? sessionId = idProp.GetString();
                                        if (sessionId is null)
                                            break;

                                        _logger.LogInformation("[{AuthUsername}] Welcome from twitch, subscribing to {Subscriptions}", state.AuthUsername, string.Join(", ", state.Subscriptions.Select(x => x.Type)));

                                        foreach (EventSubSubscription? sub in state.Subscriptions)
                                        {
                                            if (await _twitchApi.EventSubSubscribe(auth.AccessToken, sub.Type, sub.Version, sub.Condition, sessionId))
                                                _logger.LogInformation("[{AuthUsername}] Subscribed to {SubType}", state.AuthUsername, sub.Type);
                                            else
                                                _logger.LogError("[{AuthUsername}] Failed to subscribe to {SubType}", state.AuthUsername, sub.Type);
                                        }

                                        break;
                                    }
                                case "session_keepalive":
                                    // :)
                                    break;
                                case "notification":
                                    OnWsNotification?.Invoke(state.AuthUuid, json);
                                    break;
                                case "session_reconnect":
                                    {
                                        if (!json.TryGetProperty("payload", out JsonElement payloadProp))
                                            return;
                                        if (!payloadProp.TryGetProperty("session", out JsonElement sessionProp))
                                            return;
                                        if (!sessionProp.TryGetProperty("reconnect_url", out JsonElement reconnectUrlProp))
                                            return;
                                        if (reconnectUrlProp.ValueKind != JsonValueKind.String)
                                            return;

                                        string? reconnectUrl = reconnectUrlProp.GetString();
                                        await state.ReconnectWs(ct);
                                        break;
                                    }
                                case "revocation":
                                    {
                                        if (!json.TryGetProperty("payload", out JsonElement payloadProp))
                                            break;
                                        if (!payloadProp.TryGetProperty("subscription", out JsonElement subscriptionProp))
                                            break;
                                        if (!subscriptionProp.TryGetProperty("status", out JsonElement statusProp))
                                            break;
                                        if (statusProp.ValueKind != JsonValueKind.String)
                                            break;
                                        if (!subscriptionProp.TryGetProperty("type", out JsonElement typeProp))
                                            break;
                                        if (typeProp.ValueKind != JsonValueKind.String)
                                            break;

                                        string? status = statusProp.GetString();
                                        string? type = typeProp.GetString();

                                        _logger.LogWarning("[{AuthUsername}] Subscription revocation of type {Type}, status: {Status}", state.AuthUsername, type, status);
                                        break;
                                    }
                                default:
                                    _logger.LogWarning("[{AuthUsername}] Unknown message type: {MessageType}", state.AuthUsername, messageType);
                                    break;
                            }
                        }
                        else
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "InvalidMessageFormat", ct);
                            _logger.LogError("[{AuthUsername}], Invalid message format: {Message}", state.AuthUsername, message.ToString());
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, null);
                    }
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, null);
        }
        finally
        {
            _logger.LogInformation("[{AuthUsername}] Disconnected from twitch event sub", state.AuthUsername);
        }
    }

    public async Task Subscribe(TwitchAuthModel auth, EventSubSubscription[] subs, CancellationToken ct)
    {
        EventSubState? state;
        if (_userIdToWsClient.TryGetValue(auth.UserId, out state))
        {
            if (state.WsClient?.State == WebSocketState.Connecting || state.WsClient?.State == WebSocketState.Open)
                await state.WsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);

            state.WsClient?.Dispose();
            state.AuthUuid = auth.Uuid;
            state.AuthUsername = auth.Username;
            state.Subscriptions = state.Subscriptions.Concat(subs).DistinctBy(x => x.Type).ToArray();
        }
        else
        {
            state = new(_logger)
            {
                AuthUuid = auth.Uuid,
                AuthUsername = auth.Username,
                Subscriptions = subs
            };
            _userIdToWsClient.AddOrUpdate(auth.UserId, state, (_, _) => state);
        }

        await state.ReconnectWs(ct);

        lock (_loopTaskCTSLock)
        {
            _loopTaskCTS.Cancel();
        }
    }

    public async Task Unsubscribe(TwitchAuthModel auth, HashSet<string> subTypes, CancellationToken ct)
    {

        if (!_userIdToWsClient.TryGetValue(auth.UserId, out EventSubState? state))
            return;

        state.Subscriptions = state.Subscriptions.Where(x => subTypes.Contains(x.Type)).ToArray();
        if (state.Subscriptions.Any())
            await state.ReconnectWs(ct);
        else
        {
            if (state.WsClient?.State == WebSocketState.Connecting || state.WsClient?.State == WebSocketState.Open)
                await state.WsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);

            state.WsClient?.Dispose();

            _userIdToWsClient.TryRemove(auth.UserId, out _);
        }

        lock (_loopTaskCTSLock)
        {
            _loopTaskCTS.Cancel();
        }

    }

    private class EventSubState(ILogger<TwitchEventSubService> _logger) : IDisposable
    {
        public required Guid AuthUuid { get; set; }
        public required string AuthUsername { get; set; }
        public required EventSubSubscription[] Subscriptions { get; set; }
        public ClientWebSocket? WsClient { get; set; }

        public Task ReconnectWs(CancellationToken ct)
            => ReconnectWs(null, ct);

        public async Task ReconnectWs(string? url, CancellationToken ct)
        {
            url ??= "wss://eventsub.wss.twitch.tv/ws";

            if (WsClient is not null)
            {
                if (WsClient.State == WebSocketState.Open || WsClient.State == WebSocketState.Connecting)
                    await WsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                WsClient.Dispose();
            }
            WsClient = new();
            await WsClient.ConnectAsync(new(url), ct);
            _logger.LogInformation("[{AuthUsername}] Connecting to twitch event sub ({url})...", AuthUsername, url);
        }

        public void Dispose()
        {
            _logger.LogInformation("[{AuthUsername}] Disposed", AuthUsername);
            if (WsClient is not null)
            {
                WsClient.Dispose();
                WsClient = null;
            }
        }
    }

    public class EventSubSubscription
    {
        public required string Type { get; init; }
        public required string Version { get; init; }
        public required object Condition { get; init; }
    }
}
