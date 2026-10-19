using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// What runs on the remote machine leave behind when they are ended before
/// they finish: test databases, test keys, bundles, and agents of older
/// versions. The remote side clears them itself, and touches nothing else.
/// </summary>
/// <remarks>
/// The stores on this machine are shared with every other test running
/// here, so these tests clear names of their own (a random tag in the
/// patterns), never the real test patterns, in the store the tests' keys go
/// to (TestKeyStore: the machine's under simple remote).
/// </remarks>
public sealed class RemoteLeftoversTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string SqlServer =>
        Environment.GetEnvironmentVariable("SIMPLE_TEST_SQLSERVER") is { Length: > 0 } configured ? configured : SqlServerTarget.LocalDb;

    [Theory]
    [InlineData("Simple_test_0123456789ab", true)]
    [InlineData("Simple_test_0123456789abc", false)]
    [InlineData("Simple_test_0123456789AB", false)]
    [InlineData("Simple_test_0123456789ab\n", false)]
    [InlineData("Simple_63-chores", false)]
    [InlineData("Simple_test", false)]
    [InlineData("Simple_main", false)]
    public void TheDatabasesClearedAreOnlyThoseOfTestInstances(string name, bool cleared)
    {
        Assert.Equal(cleared, Regex.IsMatch(name, RemoteSweep.TestDatabases));
    }

    [Fact]
    public void TheNamesTheTestsMakeAreTheOnesCleared()
    {
        // As CliRunner.NewInstance, ServerInstance and GitSandbox name a test instance.
        var instance = $"test_{Guid.NewGuid():N}"[..17];

        Assert.Matches(RemoteSweep.TestDatabases, SqlServerTarget.DatabaseName(instance));
        Assert.Matches(RemoteSweep.TestKeys, Components.ServerKeyName(instance));
        Assert.Matches(RemoteSweep.TestKeys, Components.TerminalKeyName(instance, 1));
        Assert.Matches(RemoteSweep.TestKeys, Components.TerminalKeyName(instance, 12));
        // As the onboard terminal tests' FakeServer and test terminals name their keys.
        Assert.Matches(RemoteSweep.TestKeys, $"Simple-Test-{Guid.NewGuid():N}");
    }

    [Theory]
    [InlineData("Simple-Server-63-chores")]
    [InlineData("Simple-Server-main")]
    [InlineData("Simple-Server-test_0123456789ab-extra")]
    [InlineData("Simple-Terminal-92-terminal-pairing-1")]
    [InlineData("Simple-Terminal-test_0123456789ab-scenario-1")]
    [InlineData("Simple-Terminal-test_0123456789ab-")]
    [InlineData("Simple-OnboardTerminal")]
    [InlineData("Simple-Test-0123")]
    [InlineData("Simple-Other-0123456789abcdef0123456789abcdef")]
    [InlineData("simple-server-test_0123456789ab")]
    public void KeysOfWorktreeInstancesAndOthersAreNotCleared(string name)
    {
        Assert.DoesNotMatch(RemoteSweep.TestKeys, name);
    }

    [Fact]
    public void CiDeletesTheSameTestKeysAsTheRemoteSide()
    {
        // The step "No test keys left behind" in ci.yml has the pattern as a
        // literal: a key format added to one and not the other is either
        // deleted from under a runner or left behind unnoticed.
        var ci = File.ReadAllText(Path.Combine(CliEnvironment.FindRepository(AppContext.BaseDirectory), ".github", "workflows", "ci.yml"));

        var pattern = Assert.Single(Regex.Matches(ci, @"^\s*\$tests = '(?<pattern>[^']*)'\s*$", RegexOptions.Multiline));
        Assert.Equal(RemoteSweep.TestKeys, pattern.Groups["pattern"].Value);
    }

    [Fact]
    public async Task HeavyWorkFirstClearsLeftoverTestDatabasesAndKeysAndNothingElse()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var tag = Guid.NewGuid().ToString("N")[..8];
        string[] leftovers = ["a", "b"];
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { Sweep = Sweep(tag, SqlServer) };
        try
        {
            foreach (var name in leftovers.Append("c"))
            {
                await CreateDatabaseAsync(Database(tag, name));
                CreateKey(Key(tag, name));
            }

            var (exitCode, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");

            Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
            foreach (var name in leftovers)
            {
                Assert.False(await DatabaseExistsAsync(Database(tag, name)), $"{Database(tag, name)} is still there: {stderr}");
                Assert.False(ServerKey.Exists(Key(tag, name), TestKeyStore.Store), $"{Key(tag, name)} is still there: {stderr}");
                Assert.Contains($"dropped database {Database(tag, name)}", stderr, StringComparison.Ordinal);
                Assert.Contains($"deleted key {Key(tag, name)}", stderr, StringComparison.Ordinal);
            }

            Assert.True(await DatabaseExistsAsync(Database(tag, "c")));
            Assert.True(ServerKey.Exists(Key(tag, "c"), TestKeyStore.Store));
        }
        finally
        {
            foreach (var name in leftovers.Append("c"))
            {
                await SqlServerTarget.DropDatabaseAsync(SqlServer, Instance(tag, name), CancellationToken.None);
                ServerKey.Delete(Key(tag, name), TestKeyStore.Store);
            }
        }
    }

    [Fact]
    public async Task ALeftoverTestKeyInTheTpmIsClearedToo()
    {
        // The terminal tests may put a key in the TPM (TerminalKey); one a
        // killed test left there is cleared like one in the software store.
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { Sweep = Sweep(tag, sqlServer: null) };
        var name = Key(tag, "a");
        // Not named as the sweep's pattern says: in the TPM and in the software store alike, it stays.
        var other = Key(tag, "c");
        try
        {
            if (!TryCreateTpmKey(name, out var problem))
            {
                Assert.Skip($"No TPM this account can use here: {problem}");
            }

            Assert.True(TryCreateTpmKey(other, out problem), problem);
            CreateKey(other);

            var (exitCode, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");

            Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
            Assert.False(TpmKeyExists(name), $"{name} is still in the TPM: {stderr}");
            Assert.Contains($"deleted key {name}", stderr, StringComparison.Ordinal);
            Assert.True(TpmKeyExists(other), $"{other} was deleted from the TPM: {stderr}");
            Assert.True(ServerKey.Exists(other, TestKeyStore.Store), $"{other} was deleted from the software store: {stderr}");
        }
        finally
        {
            DeleteTpmKey(name);
            DeleteTpmKey(other);
            ServerKey.Delete(other, TestKeyStore.Store);
        }
    }

    [Fact]
    public void CiLooksForTestKeysInTheTpmToo()
    {
        var ci = File.ReadAllText(Path.Combine(CliEnvironment.FindRepository(AppContext.BaseDirectory), ".github", "workflows", "ci.yml"));
        var step = ci[ci.IndexOf("- name: No test keys left behind", StringComparison.Ordinal)..];

        Assert.Contains("Microsoft Software Key Storage Provider", step, StringComparison.Ordinal);
        Assert.Contains("Microsoft Platform Crypto Provider", step, StringComparison.Ordinal);
        // A TPM that cannot be listed is said, not a red step.
        Assert.Contains("::warning::The TPM's key store", step, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CiFailsOnATestKeyLeftInTheSoftwareStoreWhateverTheTpmHolds()
    {
        // The step "No test keys left behind" as ci.yml runs it, its pattern
        // narrowed to one key of this test's own, so that it touches no other
        // test's key. That key is left in the software store; the TPM (where
        // it can be listed) holds none: the step must fail all the same.
        if (Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 })
        {
            Assert.Skip("The step looks at the user's key store, which an SSH key logon cannot write.");
        }

        var name = $"Simple-Test-{Guid.NewGuid():N}";
        var ci = File.ReadAllText(Path.Combine(CliEnvironment.FindRepository(AppContext.BaseDirectory), ".github", "workflows", "ci.yml"));
        var script = Regex.Replace(CiStep(ci, "No test keys left behind"), @"^\$tests = '[^']*'$", $"$tests = '\\A{name}\\z'", RegexOptions.Multiline);
        Assert.Contains(name, script, StringComparison.Ordinal);
        var file = Path.Combine(Directory.CreateTempSubdirectory("simple-ci-step-").FullName, "step.ps1");
        await File.WriteAllTextAsync(file, script, Cancel);
        using (CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider }))
        {
        }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("pwsh", ["-NoProfile", "-File", file]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var step = System.Diagnostics.Process.Start(start)!;
            var output = await step.StandardOutput.ReadToEndAsync(Cancel) + await step.StandardError.ReadToEndAsync(Cancel);
            await step.WaitForExitAsync(Cancel);

            Assert.True(step.ExitCode != 0, $"The step passed with a test key left behind: {output}");
            Assert.Contains($"::error::Test keys left behind in Microsoft Software Key Storage Provider (now deleted): {name}", output, StringComparison.Ordinal);
            Assert.False(ServerKey.Exists(name, ServerKeyStore.User), output);
        }
        finally
        {
            ServerKey.Delete(name, ServerKeyStore.User);
        }
    }

    /// <summary>The script of a step of ci.yml (its <c>run: |</c> block), unindented.</summary>
    private static string CiStep(string ci, string step)
    {
        var lines = ci[ci.IndexOf($"- name: {step}", StringComparison.Ordinal)..].Split('\n');
        var body = new List<string>();
        foreach (var line in lines.SkipWhile(l => !l.Trim().StartsWith("run: |", StringComparison.Ordinal)).Skip(1))
        {
            var text = line.TrimEnd('\r');
            if (text.Trim().Length == 0)
            {
                body.Add(string.Empty);
            }
            else if (text.StartsWith("          ", StringComparison.Ordinal))
            {
                body.Add(text[10..]);
            }
            else
            {
                break;
            }
        }

        return string.Join('\n', body);
    }

    [Theory]
    [InlineData(0)]
    // A slot beyond those this client counts: a client of another version
    // may count more.
    [InlineData(5)]
    public async Task NothingIsClearedWhileOtherHeavyWorkRuns(int held)
    {
        // A test run holds a slot for as long as it runs, and its database
        // and keys are in use until it ends.
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { Slots = 2, Sweep = Sweep(tag, sqlServer: null) };
        try
        {
            CreateKey(Key(tag, "a"));
            using (HoldSlot(sandbox, held))
            {
                var (busy, busyError) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");
                Assert.True(busy == 0, $"exit code {busy}: {busyError}");
                Assert.True(ServerKey.Exists(Key(tag, "a"), TestKeyStore.Store), "Cleared while another run held a slot.");
            }

            // Light commands take no slot and clear nothing either.
            var (light, lightError) = await RunAsync(sandbox, machine, "status");
            Assert.True(light == 0, $"exit code {light}: {lightError}");
            Assert.True(ServerKey.Exists(Key(tag, "a"), TestKeyStore.Store), "Cleared by a command that takes no slot.");

            var (exitCode, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");
            Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
            Assert.False(ServerKey.Exists(Key(tag, "a"), TestKeyStore.Store), $"Not cleared once the slots were free: {stderr}");
        }
        finally
        {
            ServerKey.Delete(Key(tag, "a"), TestKeyStore.Store);
        }
    }

    [Fact]
    public async Task AnInstanceSetUpThereKeepsItsDatabaseAndKeyThoughNamedLikeATest()
    {
        // The sandbox's instance is named test_ and 12 hex digits, as a test
        // instance is; its system, started by simple remote up, holds no slot.
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ownDatabase = SqlServerTarget.DatabaseName(sandbox.Instance);
        var ownKey = Components.ServerKeyName(sandbox.Instance);
        var ownTerminalKey = Components.TerminalKeyName(sandbox.Instance, 1);
        var machine = new DirectoryMachine(sandbox.RemoteRoot)
        {
            Sweep = new(
                SqlServer,
                TestKeyStore.Store,
                $@"\ASimple_({sandbox.Instance}|test_{tag}_a)\z",
                $@"\ASimple-(Server-{sandbox.Instance}|Terminal-{sandbox.Instance}-[0-9]+|Test-{tag}a{{24}})\z"),
        };
        try
        {
            await CreateDatabaseAsync(ownDatabase);
            await CreateDatabaseAsync(Database(tag, "a"));
            CreateKey(ownKey);
            CreateKey(ownTerminalKey);
            CreateKey(Key(tag, "a"));

            var (exitCode, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");

            Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
            Assert.False(await DatabaseExistsAsync(Database(tag, "a")), $"The sweep did not run: {stderr}");
            Assert.False(ServerKey.Exists(Key(tag, "a"), TestKeyStore.Store), $"The sweep did not run: {stderr}");
            Assert.True(await DatabaseExistsAsync(ownDatabase), $"The instance's database was dropped: {stderr}");
            Assert.True(ServerKey.Exists(ownKey, TestKeyStore.Store), $"The instance's key was deleted: {stderr}");
            Assert.True(ServerKey.Exists(ownTerminalKey, TestKeyStore.Store), $"The instance's terminal key was deleted: {stderr}");
        }
        finally
        {
            await SqlServerTarget.DropDatabaseAsync(SqlServer, sandbox.Instance, CancellationToken.None);
            await SqlServerTarget.DropDatabaseAsync(SqlServer, Instance(tag, "a"), CancellationToken.None);
            ServerKey.Delete(ownKey, TestKeyStore.Store);
            ServerKey.Delete(ownTerminalKey, TestKeyStore.Store);
            ServerKey.Delete(Key(tag, "a"), TestKeyStore.Store);
        }
    }

    [Fact]
    public async Task AgentsNoRunStartedForAWeekAndOldBundlesAreRemoved()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");
        var bin = Path.Combine(sandbox.RemoteRoot, "bin");
        var incoming = Path.Combine(sandbox.RemoteRoot, "incoming");
        var current = Path.Combine(bin, RemoteRun.AgentFile);
        var old = Path.Combine(bin, "agent-0123456789ab.ps1");
        var recent = Path.Combine(bin, "agent-ba9876543210.ps1");
        var other = Path.Combine(bin, "notes.txt");
        var oldBundle = Path.Combine(incoming, "gone-0123456789ab-01234567.bundle");
        var newBundle = Path.Combine(incoming, "sending-0123456789ab-01234567.bundle");
        var weekAgo = DateTime.UtcNow - RemoteRun.KeepAgents - TimeSpan.FromHours(1);
        foreach (var file in new[] { old, recent, other, oldBundle, newBundle })
        {
            File.WriteAllText(file, "x");
        }

        foreach (var file in new[] { current, old, other, oldBundle })
        {
            File.SetLastWriteTimeUtc(file, weekAgo);
        }

        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow - RemoteRun.KeepAgents + TimeSpan.FromHours(1));

        var (exitCode, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");

        Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
        Assert.False(File.Exists(old), "An agent no run started for a week is still there.");
        Assert.False(File.Exists(oldBundle), "A bundle a week old is still there.");
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(newBundle));
        // The agent that ran is marked as used, and was not sent again.
        Assert.True(File.Exists(current));
        Assert.True(File.GetLastWriteTimeUtc(current) > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(1, machine.Sent.Count(f => f == RemoteRun.AgentFile));
    }

    private static RemoteSweep Sweep(string tag, string? sqlServer) =>
        new(sqlServer, TestKeyStore.Store, $@"\ASimple_test_{tag}_[ab]\z", $@"\ASimple-Test-{tag}(a{{24}}|b{{24}})\z");

    private static string Instance(string tag, string name) => $"test_{tag}_{name}";

    private static string Database(string tag, string name) => SqlServerTarget.DatabaseName(Instance(tag, name));

    /// <summary>In the tests' own format, Simple-Test- and 32 hex digits, so CI deletes one this test left behind.</summary>
    private static string Key(string tag, string name) => $"Simple-Test-{tag}{new string(name[0], 24)}";

    private static void CreateKey(string name)
    {
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = TestKeyStore.Store == ServerKeyStore.Machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
        });
    }

    private static readonly CngProvider Tpm = new("Microsoft Platform Crypto Provider");

    private static CngKeyOpenOptions TpmOpen => TestKeyStore.Store == ServerKeyStore.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

    /// <summary>A key in the TPM, as TerminalKey makes one there; false where no TPM can be used.</summary>
    private static bool TryCreateTpmKey(string name, out string? problem)
    {
        try
        {
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = Tpm,
                KeyCreationOptions = TestKeyStore.Store == ServerKeyStore.Machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            });
            problem = null;
            return true;
        }
        catch (CryptographicException e)
        {
            problem = e.Message;
            return false;
        }
    }

    private static bool TpmKeyExists(string name)
    {
        try
        {
            return CngKey.Exists(name, Tpm, TpmOpen);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void DeleteTpmKey(string name)
    {
        if (TpmKeyExists(name))
        {
            using var key = CngKey.Open(name, Tpm, TpmOpen);
            key.Delete();
        }
    }

    private static async Task CreateDatabaseAsync(string name)
    {
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC (@sql);";
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        return (int)(await command.ExecuteScalarAsync(Cancel))! == 1;
    }

    private static string Master() => new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = "master", Pooling = false }.ConnectionString;

    private static FileStream HoldSlot(GitSandbox sandbox, int slot)
    {
        var path = Path.Combine(sandbox.RemoteRoot, "slots", $"{slot}.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(path + ".holder", "the test");
        return stream;
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(GitSandbox sandbox, IRemoteMachine machine, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var environment = new CliEnvironment(sandbox.Clone, Path.Combine(sandbox.Directory, "home"), SqlServerTarget.LocalDb, stdout, stderr)
        {
            RemoteMachine = machine,
        };
        var exitCode = await SimpleCommand.InvokeAsync(["remote", .. args], environment, Cancel);
        return (exitCode, stderr.ToString());
    }
}
