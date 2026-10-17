using Microsoft.Data.SqlClient;
using Simple.Cli.Instances;

namespace Simple.Cli.Tests;

/// <summary>Checks of up and down that need no running process: state is written by hand.</summary>
public sealed class LocalSystemTests : IDisposable
{
    private readonly InstanceStore store = new(Path.Combine(Path.GetTempPath(), $"simple-cli-test-{Guid.NewGuid():N}"));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownRefusesAnInstanceStartedFromAnotherClone()
    {
        // Two clones in folders of the same name get the same default instance.
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var state = new InstanceState(instance, @"C:\elsewhere\repo", CliRunner.SqlServer, DateTimeOffset.UtcNow, [], []);
        store.Write(state);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null);

        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

        Assert.Equal(ExitCodes.Unhealthy, outcome.ExitCode);
        Assert.Contains(@"C:\elsewhere\repo", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(state.Repository, store.Read(instance)!.Repository);
    }

    [Fact]
    public async Task ResetDropsOnTheSqlServerUpUsedNotTheOneConfiguredNow()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null);

        // Would fail to connect (exit code 9) if it were used.
        var outcome = await system.DownAsync(instance, @"Server=.\NOSUCHINSTANCE;Integrated Security=true;Connect Timeout=1", reset: true, Cancel);

        Assert.Equal(ExitCodes.Success, outcome.ExitCode);
        Assert.Null(store.ReadSqlServer(instance));
    }

    /// <summary>A SQL Server check that refuses whatever it is asked about.</summary>
    private static Task<string?> Refuse(string sqlServer, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("refused by the test");

    [Fact]
    public async Task UpChecksTheSqlServerBeforeBuildingOrRecordingAnything()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        // No clone here: reaching the build would end with exit code 3, not 2.
        var system = new LocalSystem(Path.Combine(store.Home, "no-clone"), store, TextWriter.Null, Refuse);

        var outcome = await system.UpAsync(instance, CliRunner.SqlServer, new UpOptions(0), TimeSpan.FromSeconds(5), Cancel);

        Assert.Equal(ExitCodes.Usage, outcome.ExitCode);
        Assert.Equal("refused by the test", outcome.Error);
        Assert.Null(store.Read(instance));
        Assert.Null(store.ReadSqlServer(instance));
        Assert.False(File.Exists(store.LogPath(instance, "build")));
    }

    [Fact]
    public async Task ResetChecksTheSqlServerBeforeDroppingTheDatabase()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var database = SqlServerTarget.DatabaseName(instance);
        await ExecuteOnMasterAsync($"CREATE DATABASE [{database}]");
        try
        {
            store.WriteSqlServer(instance, CliRunner.SqlServer);
            var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, Refuse);

            var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

            Assert.Equal(ExitCodes.Usage, outcome.ExitCode);
            Assert.Equal("refused by the test", outcome.Error);
            Assert.Equal(1, await ExecuteOnMasterAsync($"SELECT COUNT(*) FROM sys.databases WHERE name = N'{database}'"));
            Assert.Equal(CliRunner.SqlServer, store.ReadSqlServer(instance));
        }
        finally
        {
            await SqlServerTarget.DropDatabaseAsync(CliRunner.SqlServer, instance, CancellationToken.None);
        }
    }

    private static async Task<int> ExecuteOnMasterAsync(string sql)
    {
        var builder = new SqlConnectionStringBuilder(CliRunner.SqlServer) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Cancel) is int count ? count : 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(store.Home))
        {
            Directory.Delete(store.Home, recursive: true);
        }
    }
}
