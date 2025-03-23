namespace Kanawanagasaki.TwitchHub.Pages;

using Services;
using Microsoft.AspNetCore.Components;

public partial class Index : ComponentBase
{
    [Inject]
    public required TwitchAuthService TwAuth { get; set; }
    [Inject]
    public required SQLiteContext Db { get; set; }
}
