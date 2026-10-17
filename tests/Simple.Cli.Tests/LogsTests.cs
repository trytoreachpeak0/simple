using Simple.Cli.Instances;

namespace Simple.Cli.Tests;

/// <summary>
/// simple logs: a process's program log (ADR 0034) by default, what it wrote
/// to its console with --output. Files are written by hand here.
/// </summary>
public sealed class LogsTests : IDisposable
{
    private const string Instance = "logs-test";

    private readonly InstanceStore store = new(Path.Combine(Path.GetTempPath(), $"simple-cli-test-{Guid.NewGuid():N}"));

    private LocalSystem System => new(@"F:\simple\repo", store, TextWriter.Null);

    public LogsTests()
    {
        var server = store.ProgramLogDirectory(Instance, "server");
        Directory.CreateDirectory(server);
        // Rolled once: Server.log is the older file, Server_001.log the one being written.
        File.WriteAllLines(Path.Combine(server, "Server.log"), ["first 1", "first 2"]);
        File.WriteAllLines(Path.Combine(server, "Server_001.log"), ["second 1", "second 2"]);
        File.WriteAllLines(store.LogPath(Instance, "server"), ["console 1"]);
        File.WriteAllLines(store.LogPath(Instance, "build"), ["> dotnet build"]);
    }

    [Fact]
    public void AProcessLogIsItsProgramLogOldestFileFirst()
    {
        var outcome = System.Logs(Instance, "server", tail: null, output: false);

        Assert.Equal(ExitCodes.Success, outcome.ExitCode);
        Assert.Equal(["first 1", "first 2", "second 1", "second 2"], outcome.Lines);
    }

    [Fact]
    public void TailCountsAcrossTheFiles()
    {
        var outcome = System.Logs(Instance, "server", tail: 3, output: false);

        Assert.Equal(["first 2", "second 1", "second 2"], outcome.Lines);
    }

    [Fact]
    public void OutputIsWhatTheProcessWroteToItsConsole()
    {
        Assert.Equal(["console 1"], System.Logs(Instance, "server", tail: null, output: true).Lines);
        Assert.Equal(["> dotnet build"], System.Logs(Instance, "build", tail: null, output: true).Lines);
    }

    [Fact]
    public void ANameWithoutAProgramLogIsNotRunningAndTheKnownOnesAreListed()
    {
        var outcome = System.Logs(Instance, "build", tail: null, output: false);

        Assert.Equal(ExitCodes.NotRunning, outcome.ExitCode);
        Assert.Equal(
            "Instance logs-test has no program log 'build'. Program logs: server. Console output (--output): build, server.",
            outcome.Error);
    }

    [Fact]
    public void EveryComponentIsToldItsOwnProgramLogDirectory()
    {
        var ports = new Dictionary<string, IReadOnlyList<int>> { ["mes-gateway"] = [20002] };
        foreach (var component in Components.All)
        {
            var directory = store.ProgramLogDirectory(Instance, component.Name);
            var context = new ComponentContext(Instance, SqlServerTarget.LocalDb, 1, [20000, 20001], ports, "d", directory);

            Assert.Contains($"--Simple:Logs:Directory={directory}", component.Arguments(context));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(store.Home))
        {
            Directory.Delete(store.Home, recursive: true);
        }
    }
}
