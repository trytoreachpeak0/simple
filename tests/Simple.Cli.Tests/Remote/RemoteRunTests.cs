using System.Text;
using System.Text.Json;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// simple remote end to end, with a directory on this machine as the remote
/// machine (<see cref="DirectoryMachine"/>): snapshot, bundle, the agent,
/// checkout, locks, slots, exit codes and results all run for real; only
/// ssh and scp are replaced.
/// </summary>
public sealed class RemoteRunTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunsExactlyTheWorkingTreeAndReturnsTheExitCode()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { Environment = new Dictionary<string, string> { ["SIMPLE_SQLSERVER"] = "from the machine" } };
        sandbox.Write("README.txt", "changed, not committed\n");
        sandbox.Write("tests/NewTests.txt", "new, never added\n");
        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        var (exitCode, stdout, stderr) = await RunAsync(
            sandbox,
            machine,
            "exec",
            "--",
            "pwsh",
            "-NoProfile",
            "-Command",
            "Get-Content README.txt, tests/NewTests.txt; $env:SIMPLE_SQLSERVER; $env:DOTNET_CLI_UI_LANGUAGE; exit 8");

        Assert.Equal(8, exitCode);
        Assert.Equal(["changed, not committed", "new, never added", "from the machine", "en"], Lines(stdout));
        Assert.Contains($"commit {snapshot.Commit[..12]} (HEAD {snapshot.Head[..12]} + 2 uncommitted files)", stderr, StringComparison.Ordinal);
        Assert.Contains("exec exited with 8", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChineseOutputComesBackAsItWasWritten()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);

        // 已连接服务端, written as code points so no file encoding is involved.
        var (exitCode, stdout, stderr) = await RunAsync(
            sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "-join ([char[]](0x5DF2, 0x8FDE, 0x63A5, 0x670D, 0x52A1, 0x7AEF))");

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["已连接服务端"], Lines(stdout));
    }

    [Fact]
    public async Task OutputWithoutAFinalNewlineKeepsTheExitCode()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);

        var (exitCode, _, stderr) = await RunAsync(
            sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "[Console]::Error.Write('partial'); exit 8");

        Assert.True(exitCode == 8, stderr);
        Assert.Contains(Lines(stderr), line => line == "partial");
        Assert.DoesNotContain("##simple-remote", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendsOnlyWhatTheRemoteCacheLacks()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);

        // Clean and pushed: nothing to bundle; the remote side fetches the origin.
        await RunOkAsync(sandbox, machine);
        Assert.Empty(Bundles(machine));

        // An unpushed commit and an uncommitted file: one bundle, built on origin/main.
        sandbox.Write("committed.txt", "committed, not pushed\n");
        var unpushed = await sandbox.CommitAsync("not pushed", Cancel);
        sandbox.Write("new.txt", "new\n");
        await RunOkAsync(sandbox, machine);
        Assert.Single(Bundles(machine));
        await Git.RunAsync(sandbox.RemoteRoot, ["--git-dir", Path.Combine(sandbox.RemoteRoot, "repo.git"), "cat-file", "-e", unpushed], Cancel);

        // The same tree again: already there, nothing sent.
        await RunOkAsync(sandbox, machine);
        Assert.Single(Bundles(machine));
    }

    [Fact]
    public async Task SendsTheFullHistoryWhenTheCacheCannotGetWhatTheBundleBuildsOn()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        // origin/main is known here, but the remote side cannot fetch it.
        await sandbox.GitAsync(Cancel, "remote", "set-url", "origin", Path.Combine(sandbox.Directory, "gone.git"));
        sandbox.Write("new.txt", "new\n");

        var (exitCode, stdout, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "Get-Content new.txt");

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["new"], Lines(stdout));
        Assert.Equal(2, Bundles(machine).Count);
        Assert.Contains("sending the full history", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultsAndOutputComeBack()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);

        var (exitCode, _, stderr) = await RunAsync(
            sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "Set-Content (Join-Path $env:SIMPLE_REMOTE_RUN 'result.txt') done; 'printed'");

        Assert.True(exitCode == 0, stderr);
        var run = Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.Clone, "artifacts", "remote", sandbox.Instance)));
        Assert.Equal("done", File.ReadAllText(Path.Combine(run, "result.txt")).Trim());
        Assert.Contains("printed", File.ReadAllText(Path.Combine(run, "output.log")), StringComparison.Ordinal);
        Assert.EndsWith((await Snapshot.TakeAsync(sandbox.Clone, Cancel)).Commit[..8], run, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRemoteWorktreeIsExactlyTheSnapshotButKeepsIgnoredFiles()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        await RunOkAsync(sandbox, machine);
        var remote = sandbox.RemoteWorktree();
        File.WriteAllText(Path.Combine(remote, "stray.txt"), "left by an earlier run");
        File.WriteAllText(Path.Combine(remote, "README.txt"), "changed by a build");
        Directory.CreateDirectory(Path.Combine(remote, "bin"));
        File.WriteAllText(Path.Combine(remote, "bin", "kept.dll"), "build output");
        sandbox.Write("src/kept.txt", "changed here\n");

        await RunOkAsync(sandbox, machine);

        Assert.False(File.Exists(Path.Combine(remote, "stray.txt")));
        // Line endings follow that machine's git settings, as in a CI checkout.
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(remote, "README.txt")).ReplaceLineEndings("\n"));
        Assert.Equal("changed here\n", File.ReadAllText(Path.Combine(remote, "src", "kept.txt")).ReplaceLineEndings("\n"));
        Assert.True(File.Exists(Path.Combine(remote, "bin", "kept.dll")));
    }

    [Fact]
    public async Task TwoClonesRunAtOnceEachOnItsOwnTree()
    {
        await using var first = await GitSandbox.CreateAsync(Cancel);
        await using var second = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(first.RemoteRoot);
        first.Write("which.txt", "first\n");
        second.Write("which.txt", "second\n");

        var runs = await Task.WhenAll(
            RunAsync(first, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "Start-Sleep 1; Get-Content which.txt"),
            RunAsync(second, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "Start-Sleep 1; Get-Content which.txt"));

        Assert.True(runs[0].ExitCode == 0, runs[0].Stderr);
        Assert.True(runs[1].ExitCode == 0, runs[1].Stderr);
        Assert.Equal(["first"], Lines(runs[0].Stdout));
        Assert.Equal(["second"], Lines(runs[1].Stdout));
    }

    [Fact]
    public async Task AnAgentThatDoesNotMatchItsNameIsReplacedBeforeItRuns()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        await RunOkAsync(sandbox, machine);
        var agent = Path.Combine(sandbox.RemoteRoot, "bin", RemoteRun.AgentFile);
        File.AppendAllText(agent, "Write-Output 'tampered'; exit 3");

        var (exitCode, stdout, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "'ran'");

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["ran"], Lines(stdout));
        Assert.Contains("not running it", stderr, StringComparison.Ordinal);
        Assert.Equal(2, machine.Sent.Count(f => f == RemoteRun.AgentFile));
    }

    [Fact]
    public async Task ARunWaitsForTheInstanceAndSaysForWhom()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        var stderr = new SyncWriter();
        using (var held = HoldLock(sandbox, "locks", sandbox.Instance))
        {
            var run = RunAsync(sandbox, machine, stderr, "exec", "--", "pwsh", "-NoProfile", "-Command", "'ran'");
            while (!stderr.ToString().Contains("waiting for instance", StringComparison.Ordinal))
            {
                Assert.False(run.IsCompleted, stderr.ToString());
                await Task.Delay(100, Cancel);
            }

            held.Dispose();
            var (exitCode, stdout, _) = await run;
            Assert.Equal(0, exitCode);
            Assert.Equal(["ran"], Lines(stdout));
        }

        Assert.Contains("held by the test", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("locks", "instance")]
    [InlineData("slots", "a slot")]
    public async Task ABusyRemoteEndsWithTwentyTwo(string directory, string what)
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { Slots = 1 };
        using var held = HoldLock(sandbox, directory, directory == "locks" ? sandbox.Instance : "0");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var outcome = await new RemoteRun(sandbox.Clone, sandbox.Instance, machine, stdout, stderr) { Wait = TimeSpan.FromSeconds(1) }
            .RunAsync("exec", ["pwsh", "-NoProfile", "-Command", "'must not run'"], json: false, Cancel);

        Assert.True(outcome.ExitCode == ExitCodes.RemoteBusy, $"{outcome.ExitCode}: {outcome.Error}{System.Environment.NewLine}{stderr}");
        Assert.Contains(what, outcome.Error, StringComparison.Ordinal);
        Assert.Contains("held by the test", outcome.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("must not run", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALargeNewFileIsRefusedBeforeAnythingIsSent()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        sandbox.Write("evidence/capture.bin", new string('x', (int)RemoteRun.LargeFileLimit + 1));

        var (exitCode, _, stderr) = await RunAsync(sandbox, machine, "test");

        Assert.Equal(ExitCodes.RemoteRefused, exitCode);
        Assert.Contains("evidence/capture.bin", stderr, StringComparison.Ordinal);
        Assert.Empty(machine.Sent);
    }

    [Fact]
    public async Task CommandsOnTheRunningSystemNeedTheInstanceThere()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);

        var (exitCode, _, stderr) = await RunAsync(sandbox, machine, "status");

        Assert.Equal(ExitCodes.NotRunning, exitCode);
        Assert.Contains("never been set up", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimpleCommandsRunTheRemoteSimpleAndKeepItsJson()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        sandbox.Write("new.txt", "new\n");
        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        var up = await RunAsync(sandbox, machine, "up", "--terminals", "2");
        var (exitCode, stdout, stderr) = await RunAsync(sandbox, machine, "status", "--json");

        Assert.Equal(["fake simple up --break-away --server-key-store Machine --terminals 2"], Lines(up.Stdout));
        Assert.True(exitCode == 0, stderr);
        using var output = JsonDocument.Parse(stdout);
        var result = output.RootElement.GetProperty("result");
        Assert.Equal(0, output.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("test-remote", result.GetProperty("machine").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        Assert.Equal(["status", "--json"], result.GetProperty("remote").GetProperty("result").GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(snapshot.Commit, (await Git.RunAsync(sandbox.RemoteWorktree(), ["rev-parse", "HEAD"], Cancel)).Trim());
    }

    [Fact]
    public async Task JsonReportsTheSnapshotAndTheRemoteExitCode()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        sandbox.Write("new.txt", "new\n");
        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        var (exitCode, stdout, stderr) = await RunAsync(sandbox, machine, "exec", "--json", "--", "pwsh", "-NoProfile", "-Command", "'to stderr'; exit 3");

        Assert.Equal(3, exitCode);
        Assert.Contains("to stderr", stderr, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(stdout);
        var result = output.RootElement.GetProperty("result");
        Assert.Equal(3, output.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
        Assert.Equal(snapshot.Commit, result.GetProperty("commit").GetString());
        Assert.Equal(snapshot.Head, result.GetProperty("head").GetString());
        Assert.Equal(1, result.GetProperty("uncommitted").GetInt32());
        Assert.StartsWith(Path.Combine(sandbox.Clone, "artifacts", "remote", sandbox.Instance), result.GetProperty("results").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetRemovesTheInstanceThereAndItsRefHere()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        sandbox.Write("new.txt", "new\n");
        await RunOkAsync(sandbox, machine);
        Assert.True(Directory.Exists(sandbox.RemoteWorktree()));

        var (exitCode, stdout, stderr) = await RunAsync(sandbox, machine, "forget");

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["fake simple down --reset"], Lines(stdout));
        // Removing the directories must not show up as serialized progress.
        Assert.DoesNotContain("CLIXML", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(sandbox.RemoteWorktree()));
        Assert.False(Directory.Exists(Path.Combine(sandbox.RemoteRoot, "runs", sandbox.Instance)));
        Assert.NotEqual(0, (await Git.TryAsync(sandbox.Clone, ["rev-parse", "--verify", "--quiet", Snapshot.Ref(sandbox.Instance)], Cancel)).ExitCode);
        Assert.NotEqual(0, (await Git.TryAsync(
            sandbox.RemoteRoot, ["--git-dir", Path.Combine(sandbox.RemoteRoot, "repo.git"), "rev-parse", "--verify", "--quiet", Snapshot.Ref(sandbox.Instance)], Cancel)).ExitCode);
    }

    private static async Task RunOkAsync(GitSandbox sandbox, IRemoteMachine machine)
    {
        var (exitCode, _, stderr) = await RunAsync(sandbox, machine, "exec", "--", "pwsh", "-NoProfile", "-Command", "exit 0");
        Assert.True(exitCode == 0, $"exit code {exitCode}: {stderr}");
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(GitSandbox sandbox, IRemoteMachine machine, params string[] args) =>
        RunAsync(sandbox, machine, new SyncWriter(), args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        GitSandbox sandbox, IRemoteMachine machine, SyncWriter stderr, params string[] args)
    {
        var stdout = new SyncWriter();
        var environment = new CliEnvironment(sandbox.Clone, Path.Combine(sandbox.Directory, "home"), SqlServerTarget.LocalDb, stdout, stderr)
        {
            RemoteMachine = machine,
        };
        var exitCode = await SimpleCommand.InvokeAsync(["remote", .. args], environment, Cancel);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Holds a lock of the remote side as another run would, with its holder note.</summary>
    private static FileStream HoldLock(GitSandbox sandbox, string directory, string name)
    {
        var path = Path.Combine(sandbox.RemoteRoot, directory, $"{name}.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(path + ".holder", "the test");
        return stream;
    }

    private static List<string> Bundles(DirectoryMachine machine) => machine.Sent.Where(f => f.EndsWith(".bundle", StringComparison.Ordinal)).ToList();

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A StringWriter that can be read while another thread writes to it.</summary>
    private sealed class SyncWriter : StringWriter
    {
        private readonly Lock gate = new();

        public override void Write(char value)
        {
            lock (gate)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (gate)
            {
                base.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (gate)
            {
                base.WriteLine(value);
            }
        }

        public override string ToString()
        {
            lock (gate)
            {
                return base.ToString();
            }
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
