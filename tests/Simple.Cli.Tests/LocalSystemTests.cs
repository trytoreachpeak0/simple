using System.Text.Json;
using Microsoft.Data.SqlClient;
using Simple.Cli.Instances;

namespace Simple.Cli.Tests;

/// <summary>Checks of up and down that need no running process: state is written by hand.</summary>
public sealed class LocalSystemTests : IDisposable
{
    private readonly InstanceStore store = new(Path.Combine(Path.GetTempPath(), $"simple-cli-test-{Guid.NewGuid():N}"));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>"log" is the program log directory (ADR 0034), "output" the console output; status had the latter as "log" (#80).</summary>
    [Fact]
    public async Task StatusNamesEachProcesssProgramLogAndItsConsoleOutputApart()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var output = store.LogPath(instance, "mes-gateway");
        // This process's pid with another start time: recorded, but gone.
        var gone = new ProcessRecord("mes-gateway", "mes-gateway", 1, Environment.ProcessId, DateTimeOffset.UnixEpoch, "Simple.MesGateway.exe", [20002], output);
        store.Write(new InstanceState(instance, @"F:\simple\repo", CliRunner.SqlServer, DateTimeOffset.UtcNow, [20002], [gone]));
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null);

        var outcome = await system.StatusAsync(instance, Cancel);

        var line = Assert.Single(outcome.Lines, l => l.TrimStart().StartsWith("mes-gateway ", StringComparison.Ordinal));
        Assert.EndsWith($"log {store.ProgramLogDirectory(instance, "mes-gateway")}  output {output}", line, StringComparison.Ordinal);
    }

    /// <summary>The same for up when a process does not get ready: its last console lines are shown, and both are named apart (#80).</summary>
    [Fact]
    public void AProcessThatDoesNotGetReadyIsReportedWithItsConsoleOutputAndItsProgramLogNamedApart()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var output = store.LogPath(instance, "mes-gateway");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllLines(output, ["starting", "refused: no source"]);
        var process = new ProcessRecord("mes-gateway", "mes-gateway", 1, 1234, DateTimeOffset.UnixEpoch, "Simple.MesGateway.exe", [20002], output);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null);

        var outcome = system.NotReady(instance, process, "exited with 3", [], string.Empty);

        var programLog = store.ProgramLogDirectory(instance, "mes-gateway");
        Assert.Equal(ExitCodes.NotReady, outcome.ExitCode);
        Assert.Equal(
            $"mes-gateway exited with 3. Its console output: {output} (simple logs mes-gateway --output); its program log: {programLog} (simple logs mes-gateway).",
            outcome.Error);
        Assert.Equal(["Last lines of its console output:", "starting", "refused: no source"], outcome.Lines);
        var result = JsonSerializer.SerializeToElement(outcome.Result, Outcome.JsonOptions);
        Assert.Equal(programLog, result.GetProperty("programLog").GetString());
        Assert.Equal(output, result.GetProperty("output").GetString());
        Assert.False(result.TryGetProperty("log", out _));
    }

    [Fact]
    public void AProcessThatExitedBeforeItWasReadyIsReportedWithItsExitCode()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var output = store.LogPath(instance, "io-simulator-2");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, string.Empty);
        var process = new ProcessRecord("io-simulator-2", "io-simulator", 2, 1234, DateTimeOffset.UnixEpoch, "Simple.IoSimulator.Headless.exe", [20010, 20011], output);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null);

        var outcome = system.NotReady(instance, process, LocalSystem.ExitedBeforeReady(uint.MaxValue), [], string.Empty, uint.MaxValue);

        Assert.StartsWith("io-simulator-2 exited before it was ready, with exit code -1 (0xFFFFFFFF). Its console output:", outcome.Error, StringComparison.Ordinal);
        var result = JsonSerializer.SerializeToElement(outcome.Result, Outcome.JsonOptions);
        Assert.Equal(-1, result.GetProperty("processExitCode").GetInt32());
    }

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

    [Theory]
    [InlineData(ServerKeyStore.User)]
    [InlineData(ServerKeyStore.Machine)]
    public async Task ResetDeletesTheServerKeyOnlyFromTheStoreUpRecorded(ServerKeyStore recorded)
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        store.WriteServerKeyStore(instance, recorded);
        var asked = new List<(string Name, ServerKeyStore Store)>();
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (name, keyStore) =>
        {
            asked.Add((name, keyStore));
            return true;
        });

        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

        Assert.Equal(ExitCodes.Success, outcome.ExitCode);
        Assert.Equal([(Components.ServerKeyName(instance), recorded)], asked);
        Assert.Null(store.ReadServerKeyStore(instance));
    }

    [Fact]
    public async Task WithoutARecordResetLooksInBothStores()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        var asked = new List<ServerKeyStore>();
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (_, keyStore) =>
        {
            asked.Add(keyStore);
            return keyStore == ServerKeyStore.Machine;
        });

        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

        Assert.Equal(ExitCodes.Success, outcome.ExitCode);
        Assert.Equal([ServerKeyStore.User, ServerKeyStore.Machine], asked);
        Assert.Contains(outcome.Lines, line => line.Contains("from the Machine key store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKeyThatCannotBeDeletedFailsResetAndKeepsTheRecord()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        store.WriteServerKeyStore(instance, ServerKeyStore.Machine);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (_, _) =>
            throw new System.Security.Cryptography.CryptographicException("Access denied."));

        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

        Assert.Equal(ExitCodes.DatabaseFailed, outcome.ExitCode);
        Assert.Contains("Only an administrator", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(ServerKeyStore.Machine, store.ReadServerKeyStore(instance));
    }

    /// <summary>
    /// simple down --reset, the clean-up of a worktree, also resets the
    /// instance its scenario runs used: their database and server key
    /// outlived the worktree before (#88).
    /// </summary>
    [Fact]
    public async Task DownResetAlsoDropsTheDatabaseOfTheInstanceTheClonesScenarioRunsUse()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var scenario = Scenarios.ScenarioRunner.InstanceFor(instance);
        await ExecuteOnMasterAsync($"CREATE DATABASE [{SqlServerTarget.DatabaseName(scenario)}]");
        try
        {
            store.WriteSqlServer(scenario, CliRunner.SqlServer);
            using var stdout = new StringWriter();
            var environment = new CliEnvironment(@"F:\simple\repo", store.Home, CliRunner.SqlServer, stdout, TextWriter.Null);

            var exitCode = await SimpleCommand.InvokeAsync(["down", "--reset", "--instance", instance, "--json"], environment, Cancel);

            Assert.True(exitCode == ExitCodes.Success, stdout.ToString());
            Assert.Equal(0, await ExecuteOnMasterAsync($"SELECT COUNT(*) FROM sys.databases WHERE name = N'{SqlServerTarget.DatabaseName(scenario)}'"));
            Assert.Null(store.ReadSqlServer(scenario));
            using var output = JsonDocument.Parse(stdout.ToString());
            var result = output.RootElement.GetProperty("result");
            Assert.Equal(instance, result.GetProperty("instance").GetString());
            Assert.Equal(scenario, result.GetProperty("scenarioInstance").GetProperty("instance").GetString());
            Assert.True(result.GetProperty("scenarioInstance").GetProperty("databaseDropped").GetBoolean());
        }
        finally
        {
            await SqlServerTarget.DropDatabaseAsync(CliRunner.SqlServer, scenario, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResetDeletesTheServerKeysOfTheInstanceAndOfItsScenarioInstance()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var scenario = Scenarios.ScenarioRunner.InstanceFor(instance);
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        store.WriteSqlServer(scenario, CliRunner.SqlServer);
        store.WriteServerKeyStore(instance, ServerKeyStore.User);
        store.WriteServerKeyStore(scenario, ServerKeyStore.Machine);
        var asked = new List<(string Name, ServerKeyStore Store)>();
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (name, keyStore) =>
        {
            asked.Add((name, keyStore));
            return true;
        });

        var outcome = await system.ResetAsync(instance, CliRunner.SqlServer, Cancel);

        Assert.True(outcome.ExitCode == ExitCodes.Success, outcome.Error);
        Assert.Equal([(Components.ServerKeyName(scenario), ServerKeyStore.Machine), (Components.ServerKeyName(instance), ServerKeyStore.User)], asked);
        Assert.Contains($"Deleted the server key {Components.ServerKeyName(scenario)} from the Machine key store.", outcome.Lines);
        Assert.Contains($"Deleted the server key {Components.ServerKeyName(instance)} from the User key store.", outcome.Lines);
    }

    [Fact]
    public async Task ResetLeavesNoDirectoryForAScenarioInstanceThatHadNone()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (_, _) => false);

        var outcome = await system.ResetAsync(instance, CliRunner.SqlServer, Cancel);

        Assert.True(outcome.ExitCode == ExitCodes.Success, outcome.Error);
        Assert.False(Directory.Exists(store.InstanceDirectory(Scenarios.ScenarioRunner.InstanceFor(instance))));
    }

    [Fact]
    public async Task ResetOfAScenarioInstanceResetsOnlyIt()
    {
        var scenario = Scenarios.ScenarioRunner.InstanceFor($"test_{Guid.NewGuid():N}"[..17]);
        store.WriteSqlServer(scenario, CliRunner.SqlServer);
        var asked = new List<string>();
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (name, _) =>
        {
            asked.Add(name);
            return false;
        });

        var outcome = await system.ResetAsync(scenario, CliRunner.SqlServer, Cancel);

        Assert.True(outcome.ExitCode == ExitCodes.Success, outcome.Error);
        Assert.All(asked, name => Assert.Equal(Components.ServerKeyName(scenario), name));
    }

    [Fact]
    public async Task ResetLeavesTheInstanceAloneWhileAScenarioRunHoldsItsScenarioInstance()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var scenario = Scenarios.ScenarioRunner.InstanceFor(instance);
        store.WriteSqlServer(instance, CliRunner.SqlServer);
        store.WriteSqlServer(scenario, CliRunner.SqlServer);
        using var run = await store.LockScenarioAsync(scenario, TimeSpan.Zero, Cancel);
        var asked = new List<string>();
        var system = new LocalSystem(@"F:\simple\repo", store, TextWriter.Null, deleteServerKey: (name, _) =>
        {
            asked.Add(name);
            return true;
        })
        {
            ScenarioWait = TimeSpan.FromMilliseconds(300),
        };

        var outcome = await system.ResetAsync(instance, CliRunner.SqlServer, Cancel);

        Assert.Equal(ExitCodes.Busy, outcome.ExitCode);
        Assert.Contains($"scenario run is using instance {scenario}", outcome.Error, StringComparison.Ordinal);
        Assert.Contains($"Instance {instance} is kept", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal(CliRunner.SqlServer, store.ReadSqlServer(instance));
        Assert.Equal(CliRunner.SqlServer, store.ReadSqlServer(scenario));
    }

    [Fact]
    public async Task UpRefusesAnotherKeyStoreThanTheInstanceRecorded()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        store.WriteServerKeyStore(instance, ServerKeyStore.Machine);
        // No clone here: reaching the build would end with exit code 3, not 2.
        var system = new LocalSystem(Path.Combine(store.Home, "no-clone"), store, TextWriter.Null, (_, _) => Task.FromResult<string?>(null));

        var outcome = await system.UpAsync(instance, CliRunner.SqlServer, new UpOptions(0), TimeSpan.FromSeconds(5), Cancel);

        Assert.Equal(ExitCodes.Usage, outcome.ExitCode);
        Assert.Contains("down --reset", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(ServerKeyStore.Machine, store.ReadServerKeyStore(instance));
    }

    [Fact]
    public async Task DownWaitsForAFileInTheRunDirectoryThatIsReleasedSoon()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var repository = Path.Combine(store.Home, "clone");
        store.Write(new InstanceState(instance, repository, CliRunner.SqlServer, DateTimeOffset.UtcNow, [], []));
        using var held = HeldImage.In(InstanceStore.BinDirectory(repository, instance, "server"));
        var system = new LocalSystem(repository, store, TextWriter.Null);

        // Released after a second, as by a process still exiting or a scanner (#60).
        var release = Task.Delay(TimeSpan.FromSeconds(1), Cancel).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: false, Cancel);
        await release;

        Assert.True(outcome.ExitCode == ExitCodes.Success, $"down exited with {outcome.ExitCode}: {outcome.Error}");
        Assert.False(Directory.Exists(InstanceStore.RunDirectory(repository, instance)));
        Assert.Null(store.Read(instance));
    }

    [Fact]
    public async Task AFileStillHeldFailsDownNamesItsHolderAndKeepsTheState()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var repository = Path.Combine(store.Home, "clone");
        var heldStore = new InstanceStore(store.Home) { DeleteWait = TimeSpan.FromSeconds(1) };
        heldStore.Write(new InstanceState(instance, repository, CliRunner.SqlServer, DateTimeOffset.UtcNow, [], []));
        using var held = HeldImage.In(InstanceStore.BinDirectory(repository, instance, "server"));
        var system = new LocalSystem(repository, heldStore, TextWriter.Null);

        // Fails before the database is dropped, so the hint repeats --reset.
        var outcome = await system.DownAsync(instance, CliRunner.SqlServer, reset: true, Cancel);

        Assert.Equal(ExitCodes.Unexpected, outcome.ExitCode);
        Assert.Contains(held.Path, outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"pid {Environment.ProcessId}", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("Last error: Access to the path", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("run simple down --reset again", outcome.Error, StringComparison.Ordinal);
        Assert.NotNull(heldStore.Read(instance));

        // Once it is released, down again finishes the job, with the usual wait.
        held.Dispose();
        var again = await new LocalSystem(repository, store, TextWriter.Null).DownAsync(instance, CliRunner.SqlServer, reset: false, Cancel);
        Assert.True(again.ExitCode == ExitCodes.Success, $"down exited with {again.ExitCode}: {again.Error}");
        Assert.False(Directory.Exists(InstanceStore.RunDirectory(repository, instance)));
        Assert.Null(store.Read(instance));
    }

    /// <summary>
    /// A DLL in a directory, loaded into this process the way a running
    /// component holds its own: deleting it fails with access denied until
    /// it is released, as down met on CI (#60).
    /// </summary>
    private sealed class HeldImage : IDisposable
    {
        private nint handle;

        private HeldImage(string path, nint handle)
        {
            Path = path;
            this.handle = handle;
        }

        public string Path { get; }

        public static HeldImage In(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "A.deps.json"), "{}");
            var path = System.IO.Path.Combine(directory, "Held.dll");
            File.Copy(System.IO.Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.IO.Compression.Native.dll"), path);
            return new HeldImage(path, System.Runtime.InteropServices.NativeLibrary.Load(path));
        }

        public void Dispose()
        {
            var loaded = Interlocked.Exchange(ref handle, 0);
            if (loaded != 0)
            {
                System.Runtime.InteropServices.NativeLibrary.Free(loaded);
            }
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
