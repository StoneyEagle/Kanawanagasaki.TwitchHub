using Kanawanagasaki.TwitchHub.Models;

namespace Kanawanagasaki.TwitchHub.Pages;

using Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TwitchLib.PubSub;

public partial class FollowerAlert : ComponentBase, IDisposable
{
    [Inject]
    public required TwitchAuthService TwAuth { get; set; }
    [Inject]
    public required TwitchApiService TwApi { get; set; }
    [Inject]
    public required ILogger<FollowerAlert> Logger { get; set; }
    [Inject]
    public required IJSRuntime Js { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string? Channel { get; set; }
    public string? _cacheChannel;

    private TwitchPubSub? _pubSub;

    private List<TwitchGetUsersResponse> _followersQueue = [new("", "", "", "", "", "", "", "", "", "", "")];

    private bool _isRunning = true;
    private bool _isSimulationAllowed;

    private ElementReference _ref;
    private bool _isSimulating;
    private TwitchGetUsersResponse? _droppedUser;
    private DateTime _simulationStart;
    private int _width;
    private int _height;
    private double _x = 0;
    private double _y = 0;

    private List<(double x, double y)> _points = [];

    protected override async Task OnInitializedAsync()
    {
        while (_isRunning)
        {
            if (_followersQueue.Count > 0 && _isSimulationAllowed)
            {
                DoSimulation(_followersQueue[0]);
                lock (_followersQueue)
                    _followersQueue.RemoveAt(0);
            }

            await Task.Delay(1000);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Channel == _cacheChannel)
            return;

        if (_pubSub is not null)
        {
            _pubSub.Disconnect();
            _pubSub = null;
        }

        _cacheChannel = Channel;

        if (string.IsNullOrWhiteSpace(Channel))
            return;

        TwitchAuthModel? model = await TwAuth.GetRestored(Channel);

        if (model is null || !model.IsValid)
        {
            Logger.LogWarning($"PubSub for {Channel} not connected due to missing authentication model");
            return;
        }

        _pubSub = new();

        _pubSub.OnPubSubServiceConnected += (obj, ev) =>
        {
            Logger.LogInformation($"[{model.Username}] Pubsub connected");
            _pubSub.SendTopics(model.AccessToken);
        };
        _pubSub.OnFollow += (obj, ev) =>
        {
            Logger.LogInformation($"[{model.Username}] {ev.DisplayName} just followed you");

            Task.Run(async () =>
            {
                TwitchGetUsersResponse? user = await TwApi.GetUser(model.AccessToken, ev.UserId);
                if (user is not null)
                    lock (_followersQueue)
                        _followersQueue.Add(user);
            });
        };
        // _pubSub.OnLog += (obj, ev) =>
        // {
        //     Logger.LogDebug($"[{model.Username}] " + ev.Data);
        // };

        _pubSub.ListenToFollows(model.UserId);

        _pubSub.Connect();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _width = await Js.InvokeAsync<int>("getWidth", _ref);
            _height = await Js.InvokeAsync<int>("getHeight", _ref);
            _isSimulationAllowed = true;
        }
    }

    private void DoSimulation(TwitchGetUsersResponse user)
    {
        _points.Clear();

        _isSimulating = true;
        _simulationStart = DateTime.UtcNow;
        _droppedUser = user;
        StateHasChanged();

        double x1 = 5;
        double vertexY1 = 43.75; // 56.25

        double b1 = (2 * x1 + Math.Sqrt(4 * vertexY1)) / -2;

        double x2 = 50;
        double y2 = 75;

        double b2 = -x2;
        Console.WriteLine((2 * b2 + Math.Sqrt(4 * y2)) / -2);
        Console.WriteLine((2 * b2 - Math.Sqrt(4 * y2)) / -2);

        _isSimulating = false;
        StateHasChanged();
    }

    public void Dispose()
    {
        _isRunning = false;
    }
}
