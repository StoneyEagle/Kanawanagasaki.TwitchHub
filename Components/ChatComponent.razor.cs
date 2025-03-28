namespace Kanawanagasaki.TwitchHub.Components;

using Data;
using Models;
using Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

public partial class ChatComponent : ComponentBase, IDisposable
{
    [Inject]
    public required TwitchAuthService TwAuth { get; set; }
    [Inject]
    public required TwitchApiService TwApi { get; set; }
    [Inject]
    public required TwitchChatMessagesService TwChat { get; set; }
    [Inject]
    public required IJSRuntime Js { get; set; }
    [Inject]
    public required ILogger<ChatComponent> Logger { get; set; }
    [Inject]
    public required HelperService Helper { get; set; }
    [Inject]
    public required SQLiteContext Db { get; set; }

    [Parameter]
    public string? Channel { get; set; }
    private string _channelCache = "";
    private TwitchGetUsersResponse? _channelObj;

    [Parameter]
    public TwitchAuthModel? AuthModel { get; set; }

    public Dictionary<string, string> Badges = [];

    private readonly List<ProcessedChatMessage> _messages = [];
    private readonly List<ChatMessageComponent> _components = [];

    protected override void OnInitialized()
    {
        TwChat.OnMessage += OnMessage;
        TwChat.OnMessageDelete += OnMessageDelete;
        TwChat.OnUserSuspend += OnUserSuspend;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(Channel)) return;
        if (AuthModel is null) return;
        if (!AuthModel.IsValid) return;

        _channelObj = await TwApi.GetUserByLogin(AuthModel.AccessToken, Channel);
        StateHasChanged();

        if (Channel == _channelCache) return;

        Logger.LogDebug("Connecting to {Channel} with {AuthModel.Username} account", Channel, AuthModel.Username);
        TwChat.Connect(AuthModel, Channel);

        TwitchDataResponse<TwitchGetChatBadgeResponse>? globalBadges = await TwApi.GetGlobalBadges(AuthModel.AccessToken);
        Badges.Clear();
        if (globalBadges is not null)
        {
            foreach (TwitchGetChatBadgeResponse? badge in globalBadges.data)
            {
                TwitchGetChatBadgeVersionResponse version = badge.versions.OrderByDescending(v => int.TryParse(v.id, out int id) ? id : 0).First();
                Badges[badge.set_id] = version.image_url_1x;
            }
        }

        if (_channelObj is not null)
        {
            TwitchDataResponse<TwitchGetChatBadgeResponse>? channelBadges = await TwApi.GetChannelBadges(AuthModel.AccessToken, _channelObj.id);
            foreach (TwitchGetChatBadgeResponse? badge in channelBadges?.data ?? [])
            {
                TwitchGetChatBadgeVersionResponse version = badge.versions.OrderByDescending(v => int.TryParse(v.id, out int id) ? id : 0).First();
                Badges[badge.set_id] = version.image_url_1x;
            }
        }

        _channelCache = Channel;
    }

    private void OnMessage(ProcessedChatMessage message)
    {
        InvokeAsync(async () =>
        {
            if (!message.Fragments.HasFlag(ProcessedChatMessage.RenderFragments.Message))
                return;
            if (AuthModel is null)
                return;

            if (!string.IsNullOrWhiteSpace(message.Original.ColorHex))
            {
                (double h, double s, double l) hsl = Helper.RgbToHsl(Helper.HexToRgb(message.Original.ColorHex));
                hsl.l += (1 - hsl.l) / 4;
                message.SetColor($"hsl({hsl.h}, {(int)(hsl.s * 100)}%, {(int)(hsl.l * 100)}%)");
            }

            TwitchGetUsersResponse? user = await TwApi.GetUser(AuthModel.AccessToken, message.Original.UserId);
            if (user is null)
            {
                await TwAuth.Restore(AuthModel);
                user = await TwApi.GetUser(AuthModel.AccessToken, message.Original.UserId);
            }

            message.SetUser(user);

            if (message.Fragments.HasFlag(ProcessedChatMessage.RenderFragments.Code))
                foreach (object? customContent in message.CustomContent)
                    if (customContent is CodeContent { IsFormatted: false } codeContent)
                        await codeContent.Format(Js);

            _messages.Add(message);
            if (_messages.Count > 20) _messages.RemoveAt(0);
            StateHasChanged();

            await Task.Delay(TimeSpan.FromMinutes(1));
            ChatMessageComponent? component = _components.FirstOrDefault(c => c.Message == message);
            if (component is not null)
                await component.AnimateAway();
            _messages.Remove(message);
            StateHasChanged();
        });
    }

    private void OnMessageDelete(string messageId)
    {
        InvokeAsync(() =>
        {
            int index = _messages.FindIndex(x => x.Original.Id == messageId);
            if (index > 0)
            {
                _messages.RemoveAt(index);
                StateHasChanged();
            }
        });
    }

    private void OnUserSuspend(string username)
    {
        InvokeAsync(() =>
        {
            int index = -1;
            do
            {
                index = _messages.FindIndex(x => x.Original.Username == username);
                if (0 <= index)
                    _messages.RemoveAt(index);
            } while (0 <= index);

            StateHasChanged();
        });
    }

    public void RegisterComponent(ChatMessageComponent component)
    {
        lock (_components)
        {
            if (!_components.Contains(component))
                _components.Add(component);
        }
    }

    public void UnregisterComponent(ChatMessageComponent component)
    {
        lock (_components)
        {
            if (_components.Contains(component))
                _components.Remove(component);
        }
    }

    public void Dispose()
    {
        if (AuthModel is not null && Channel is not null)
            TwChat.Disconnect(AuthModel, Channel);
        TwChat.OnMessage -= OnMessage;
        TwChat.OnMessageDelete -= OnMessageDelete;
        TwChat.OnUserSuspend -= OnUserSuspend;
    }
}
