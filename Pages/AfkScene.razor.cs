namespace Kanawanagasaki.TwitchHub.Pages;

using Data;
using Data.JsHostObjects;
using Models;
using Services;
using Microsoft.AspNetCore.Components;
using Microsoft.ClearScript;
using Microsoft.JSInterop;
using TwitchLib.Client;

public partial class AfkScene : ComponentBase, IAsyncDisposable
{
    [Inject]
    public required JsEnginesService JsEngines { get; set; }
    [Inject]
    public required IJSRuntime Js { get; set; }
    [Inject]
    public required TwitchChatService Chat { get; set; }
    [Inject]
    public required SQLiteContext Db { get; set; }
    [Inject]
    public required TwitchAuthService TwAuth { get; set; }
    [Inject]
    public required ILogger<AfkScene> Logger { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string Content { get; set; } = "";

    [Parameter]
    [SupplyParameterFromQuery]
    public string? Channel { get; set; }
    private string? _cachedChannel;

    [Parameter]
    [SupplyParameterFromQuery]
    public string? Bot { get; set; }
    private string? _cachedBot;

    private AfkSceneData? _afkScene;
    private string _afkSceneJsName = "_$" + Guid.NewGuid().ToString().Replace("-", "");
    private string _symbolsJsName = "_$" + Guid.NewGuid().ToString().Replace("-", "");

    private IJSObjectReference? _loopObj;
    private DateTime _startDateTime;

    private JsEngine? _engine;
    private TwitchClient? _chatClient;
    private TwitchAuthModel? _twAuth;

    private int _framesToSkip;
    private int _framesToSkipCount;

    private bool _isInitialized;

    private int _jsFaultsCount;

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(Channel)) return;
        if (string.IsNullOrWhiteSpace(Bot)) Bot = Channel;

        if (Channel != _cachedChannel)
        {
            _engine = JsEngines.GetEngine(Channel);
            _engine.StreamApi.afk.OnCodeChange += () =>
            {
                InvokeAsync(async () => await Init());
            };

            _cachedChannel = Channel;
        }

        if (Bot != _cachedBot)
        {
            if (_twAuth is not null)
                Chat.Unlisten(_twAuth, this);

            _twAuth = await TwAuth.GetRestored(Bot);
            if (_twAuth is not null && _twAuth.IsValid)
                _chatClient = Chat.GetClient(_twAuth, this, Channel);
            else Logger.LogWarning($"Failed to connect to {Channel} as {Bot}");

            _cachedBot = Bot;
        }

        await Init();
    }

    private async Task Init(bool flag = true)
    {
        _isInitialized = false;

        _startDateTime = DateTime.UtcNow;

        _afkScene = new();
        if (!string.IsNullOrWhiteSpace(Content))
            _afkScene.SetContent(Content);

        try
        {
            if (_engine is null)
                return;

            _engine.RegisterHostObjects(_afkSceneJsName, _afkScene);
            _engine.RegisterHostObjects(_symbolsJsName, _afkScene.symbols);

            await _engine.Execute($"({_engine.StreamApi.afk.initCode})({_afkSceneJsName})", false);
            _engine.FlushLogs();

            _isInitialized = true;
        }
        catch (Exception e)
        {
            await ProcessException(e, flag);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _loopObj = await Js.InvokeAsync<IJSObjectReference>("createLoopObj", DotNetObjectReference.Create(this));
            await _loopObj.InvokeVoidAsync("start");
        }
    }

    [JSInvokable("onTick")]
    public async Task OnTick()
    {
        if (!_isInitialized) return;

        if (0 < _framesToSkipCount)
        {
            _framesToSkipCount--;
            return;
        }

        if (_engine is null)
            return;

        string? tickCode = _engine.StreamApi.afk.tickCode;
        string? symbolTickCode = _engine.StreamApi.afk.symbolTickCode;

        TimeSpan diff = DateTime.UtcNow - _startDateTime;
        int tickCounter = (int)(diff.TotalMilliseconds / 10);

        string code = $@"
            ({tickCode})({_afkSceneJsName}, {tickCounter});
            for(let i = 0; i < {_symbolsJsName}.length; i++)
                ({symbolTickCode})({_symbolsJsName}[i], i, {_symbolsJsName}.length, {tickCounter});
        ";

        try
        {
            await _engine.Execute(code, false);
            _framesToSkip = 0;

            string? logs = _engine.FlushLogs();
            if (!string.IsNullOrWhiteSpace(logs))
            {
                _jsFaultsCount++;

                if (3 < _jsFaultsCount)
                {
                    if (tickCode == _engine.StreamApi.afk.tickCode && symbolTickCode == _engine.StreamApi.afk.symbolTickCode)
                        _engine.StreamApi.afk.resetToDefault();
                    else
                    {
                        _engine.StreamApi.afk.SetOnTick(tickCode);
                        _engine.StreamApi.afk.SetOnSymbolTick(symbolTickCode);
                    }
                    _chatClient?.SendMessage(Channel, logs);
                }
            }
            else
                _jsFaultsCount = 0;
        }
        catch (Exception e)
        {
            await ProcessException(e);
        }
        StateHasChanged();
    }

    private async Task ProcessException(Exception e, bool flag = true)
    {
        _jsFaultsCount++;
        if (e.Message != "ReferenceError: _afkScreenJsName is not defined" && e.Message != "The V8 runtime cannot perform the requested operation because a script exception is pending")
        {
            if (e is ScriptEngineException)
                _chatClient?.SendMessage(Channel, e.Message);

            if (3 < _jsFaultsCount)
                _engine?.StreamApi.afk.resetToDefault();
            if (flag)
                await Init(false);
            _startDateTime = DateTime.UtcNow;
        }
        else
        {
            _framesToSkip++;
            _framesToSkipCount = _framesToSkip;
            Console.WriteLine($"Skipping {_framesToSkip} frames");
        }

        Console.WriteLine(e);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_loopObj is not null)
                await _loopObj.InvokeVoidAsync("stop");
        }
        catch { }
    }
}
