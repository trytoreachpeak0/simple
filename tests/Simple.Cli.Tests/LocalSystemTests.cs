using Simple.Cli.Instances;

namespace Simple.Cli.Tests;

/// <summary>down's checks that need no running process: a state file is written by hand.</summary>
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

    public void Dispose()
    {
        if (Directory.Exists(store.Home))
        {
            Directory.Delete(store.Home, recursive: true);
        }
    }
}
