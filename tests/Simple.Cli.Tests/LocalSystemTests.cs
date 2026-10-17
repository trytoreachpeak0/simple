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
