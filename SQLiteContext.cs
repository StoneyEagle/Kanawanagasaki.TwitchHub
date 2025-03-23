namespace Kanawanagasaki.TwitchHub;

using Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class SQLiteContext : DbContext
{
    public required DbSet<TwitchAuthModel> TwitchAuth { get; set; }
    public required DbSet<TwitchCustomRewardModel> TwitchCustomRewards { get; set; }
    public required DbSet<TextCommandModel> TextCommands { get; set; }
    public required DbSet<JsAfkCodeModel> JsAfkCodes { get; set; }
    public required DbSet<ViewerVoice> ViewerVoices { get; set; }
    public required DbSet<SettingModel> Settings { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        SqliteConnectionStringBuilder connectionStringBuilder = new() { DataSource = "data.db" };
        SqliteConnection connection = new(connectionStringBuilder.ToString());

        optionsBuilder.UseSqlite(connection);
    }
}
