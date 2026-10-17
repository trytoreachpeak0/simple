using System.Text.Json;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>How simple remote reads its arguments and what it asks the remote machine to run.</summary>
public sealed class RemoteCommandTests
{
    private static readonly CliEnvironment Environment = new(
        Repository: @"C:\clones\42-simple-remote",
        Home: Path.Combine(Path.GetTempPath(), "simple-cli-tests-never-written"),
        SqlServer: SqlServerTarget.LocalDb,
        Out: TextWriter.Null,
        Error: TextWriter.Null);

    [Theory]
    [InlineData(new[] { "remote", "test", "--filter-class", "*CliTests", "--minimum-expected-tests", "3" }, new[] { "--filter-class", "*CliTests", "--minimum-expected-tests", "3" })]
    [InlineData(new[] { "remote", "test", "--", "--filter-method", "*Remote*" }, new[] { "--filter-method", "*Remote*" })]
    [InlineData(new[] { "remote", "exec", "--", "dotnet", "--info" }, new[] { "dotnet", "--info" })]
    [InlineData(new[] { "remote", "logs", "server", "--tail", "20" }, new[] { "server", "--tail", "20" })]
    [InlineData(new[] { "remote", "--instance", "other", "up", "--json", "--terminals", "2" }, new[] { "--terminals", "2" })]
    public void ArgumentsAfterTheCommandGoToTheRemoteCommandInOrder(string[] args, string[] passed)
    {
        var parse = SimpleCommand.Create(Environment).Parse(args);

        Assert.Empty(parse.Errors);
        Assert.Equal(passed, parse.UnmatchedTokens);
    }

    [Fact]
    public void JsonAndInstanceStaySimpleRemotesOwn()
    {
        var parse = SimpleCommand.Create(Environment).Parse(["remote", "--instance", "other", "up", "--json", "--terminals", "2"]);

        Assert.True(parse.GetValue<bool>(SimpleCommand.JsonOption));
        Assert.Equal("other", SimpleCommand.InstanceOf(parse, Environment.Repository));
    }

    [Fact]
    public void TestResultsGoToTheRunDirectoryUnlessTheCallerChoseWhere()
    {
        var plan = RemotePlan.For("test", ["--filter-class", "*X"], "C:/simple-remote/runs/i/r", json: false);
        var chosen = RemotePlan.For("test", ["--results-directory", "D:/elsewhere"], "C:/simple-remote/runs/i/r", json: false);

        Assert.Equal("dotnet", plan.FileName);
        Assert.Equal(
            ["test", "--solution", "simple.slnx", "--results-directory", "C:/simple-remote/runs/i/r/TestResults", "--report-xunit-trx", "--filter-class", "*X"],
            plan.Arguments);
        Assert.True(plan is { Checkout: true, Slot: true, KeepsResults: true, RunsSimple: false });
        Assert.Equal(["test", "--solution", "simple.slnx", "--results-directory", "D:/elsewhere"], chosen.Arguments);
    }

    /// <summary>
    /// Restore there is locked as on CI (Directory.Build.props), whatever the
    /// machine's own environment: a stale lock file passed there and failed CI
    /// with NU1004 (#76, #80).
    /// </summary>
    [Fact]
    public void CommandsThereRestoreAsOnCi()
    {
        var environment = RemoteRun.RunEnvironment(new DirectoryMachine("unused"));

        Assert.Equal("true", environment["CI"]);
    }

    [Theory]
    [InlineData("up", true, true)]
    [InlineData("down", false, false)]
    [InlineData("status", false, false)]
    [InlineData("events", false, false)]
    [InlineData("logs", false, false)]
    [InlineData("forget", false, false)]
    public void SimpleCommandsRunTheRemoteSimpleWithJsonPassedOn(string command, bool checkout, bool slot)
    {
        var plan = RemotePlan.For(command, [], "unused", json: true);

        Assert.Equal("pwsh", plan.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", "simple.ps1"], plan.Arguments.Take(4));
        Assert.Equal("--json", plan.Arguments[^1]);
        Assert.Equal((checkout, slot, true, command == "forget"), (plan.Checkout, plan.Slot, plan.RunsSimple, plan.Forget));
    }

    [Fact]
    public void OnlyRemoteUpStartsTheComponentsOutsideTheSessionsJob()
    {
        var local = SimpleCommand.Create(Environment).Parse(["up"]);
        var remote = RemotePlan.For("up", ["--terminals", "2"], "unused", json: false);

        Assert.False(local.GetValue<bool>(SimpleCommand.BreakAwayOption));
        Assert.Equal(["up", SimpleCommand.BreakAwayOption, SimpleCommand.ServerKeyStoreOption, "Machine", "--terminals", "2"], remote.Arguments.Skip(4));
    }

    [Theory]
    [InlineData(new[] { "up" }, ServerKeyStore.User)]
    [InlineData(new[] { "up", "--server-key-store", "Machine" }, ServerKeyStore.Machine)]
    [InlineData(new[] { "up", "--server-key-store", "user" }, ServerKeyStore.User)]
    public void TheServerKeyStoreIsUserUnlessMachineIsAskedFor(string[] args, ServerKeyStore expected)
    {
        var parse = SimpleCommand.Create(Environment).Parse(args);

        Assert.Empty(parse.Errors);
        Assert.True(ServerKey.TryParse(parse.GetValue<string>(SimpleCommand.ServerKeyStoreOption), out var store));
        Assert.Equal(expected, store);
    }

    [Theory]
    [InlineData("Both")]
    [InlineData("1")]
    [InlineData("")]
    public void AnyOtherServerKeyStoreIsAnError(string value)
    {
        Assert.NotEmpty(SimpleCommand.Create(Environment).Parse(["up", "--server-key-store", value]).Errors);
    }

    [Theory]
    [InlineData(ServerKeyStore.User)]
    [InlineData(ServerKeyStore.Machine)]
    public void TheServerIsToldWhichKeyStoreToUse(ServerKeyStore store)
    {
        var context = new ComponentContext("i", SqlServerTarget.LocalDb, 1, [20000, 20001], new Dictionary<string, IReadOnlyList<int>> { ["mes-gateway"] = [20002], ["fake-riot"] = [20003] }, "d", "l", store);

        Assert.Contains($"--Simple:OnboardTerminals:ServerKeyStore={store}", Components.Server.Arguments(context));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("down")]
    [InlineData("status")]
    [InlineData("events")]
    [InlineData("logs")]
    [InlineData("forget")]
    public void NoOtherRemoteCommandAsksToBreakAwayOrForAKeyStore(string command)
    {
        var arguments = RemotePlan.For(command, [], "unused", json: false).Arguments;

        Assert.DoesNotContain(SimpleCommand.BreakAwayOption, arguments);
        Assert.DoesNotContain(SimpleCommand.ServerKeyStoreOption, arguments);
    }

    [Fact]
    public void WithoutTheSwitchAComponentStaysInTheCallersJob()
    {
        Assert.False(Processes.ChildProcess.CreationFlags(breakAway: false).HasFlag(Windows.Win32.System.Threading.PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB));
        Assert.True(Processes.ChildProcess.CreationFlags(breakAway: true).HasFlag(Windows.Win32.System.Threading.PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB));
    }

    [Theory]
    [InlineData(null, "SIMPLE_REMOTE is not set")]
    [InlineData("-oProxyCommand=calc", "must be the alias")]
    [InlineData("user@host", "must be the alias")]
    public async Task WithoutAUsableAliasNothingRunsAndTheExitCodeIsTwo(string? alias, string error)
    {
        using var stdout = new StringWriter();

        var exitCode = await SimpleCommand.InvokeAsync(
            ["remote", "status", "--json"], Environment with { Out = stdout, RemoteAlias = alias }, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        using var output = JsonDocument.Parse(stdout.ToString());
        Assert.Contains(error, output.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneProjectReplacesTheSolution()
    {
        var plan = RemotePlan.For("test", ["--project", "tests/Simple.Cli.Tests", "--filter-class", "*X"], "C:/simple-remote/runs/i/r", json: false);

        Assert.Equal(
            ["test", "--results-directory", "C:/simple-remote/runs/i/r/TestResults", "--report-xunit-trx", "--project", "tests/Simple.Cli.Tests", "--filter-class", "*X"],
            plan.Arguments);
    }

    [Fact]
    public void AProjectMustBeInsideTheClone()
    {
        var repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);

        Assert.Equal("tests/Simple.Cli.Tests", RemoteCommand.ProjectPath(repository, "tests/Simple.Cli.Tests"));
        Assert.Equal("tests/Simple.Cli.Tests", RemoteCommand.ProjectPath(repository, @"tests\Simple.Cli.Tests\"));
        Assert.Equal("tests/Simple.Cli.Tests/Simple.Cli.Tests.csproj", RemoteCommand.ProjectPath(repository, "tests/Simple.Cli.Tests/Simple.Cli.Tests.csproj"));
        Assert.Null(RemoteCommand.ProjectPath(repository, "../elsewhere"));
        Assert.Null(RemoteCommand.ProjectPath(repository, "tests/../../elsewhere"));
        Assert.Null(RemoteCommand.ProjectPath(repository, Path.Combine(repository, "tests", "Simple.Cli.Tests")));
        Assert.Null(RemoteCommand.ProjectPath(repository, "tests/NoSuch.Tests"));
        Assert.Null(RemoteCommand.ProjectPath(repository, "README.md"));
        Assert.Null(RemoteCommand.ProjectPath(repository, "."));
        // A directory without a project file: dotnet test would look for one higher up.
        Assert.Null(RemoteCommand.ProjectPath(repository, "tests"));
    }

    [Theory]
    [InlineData("--project", "../elsewhere")]
    [InlineData("--project=../elsewhere")]
    [InlineData("--PROJECT:../elsewhere")]
    [InlineData("--solution", "../elsewhere/x.slnx")]
    [InlineData("--solution=../elsewhere/x.slnx")]
    public async Task ATargetPassedThroughIsRefused(params string[] passed)
    {
        using var stdout = new StringWriter();
        var environment = Environment with { Repository = CliEnvironment.FindRepository(AppContext.BaseDirectory), Out = stdout, RemoteAlias = "remote-test" };

        var exitCode = await SimpleCommand.InvokeAsync(["remote", "test", "--json", "--", .. passed], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        using var output = JsonDocument.Parse(stdout.ToString());
        Assert.Contains($"'{passed[0]}' is not passed through", output.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectOutsideTheCloneIsRefusedAsJson()
    {
        using var stdout = new StringWriter();
        var environment = Environment with { Repository = CliEnvironment.FindRepository(AppContext.BaseDirectory), Out = stdout, RemoteAlias = "remote-test" };

        var exitCode = await SimpleCommand.InvokeAsync(["remote", "test", "--project", "../elsewhere", "--json"], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        using var output = JsonDocument.Parse(stdout.ToString());
        Assert.Contains("--project '../elsewhere'", output.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThroughAPipeTheCommandLineWritesUtf8()
    {
        // Sessions read simple's output through pipes as UTF-8. A Chinese
        // Windows console's code page (936) would garble every Chinese
        // character: screen texts, test names. The error echoes the argument.
        // A console of its own, in the machine's code page, as the shell a
        // session runs simple from has.
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "simple.dll"), "status", "--instance", "已连接 服务端", "--json" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("'已连接 服务端'", JsonDocument.Parse(stdout).RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedFailureStillEndsInOneJsonObject()
    {
        using var stdout = new StringWriter();
        // A real directory, so the failure is the machine's and not a missing clone's.
        var environment = Environment with { Repository = Path.GetTempPath(), Out = stdout, RemoteMachine = new FailingMachine() };

        var exitCode = await SimpleCommand.InvokeAsync(["remote", "status", "--json"], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Unexpected, exitCode);
        using var output = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(ExitCodes.Unexpected, output.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("the machine broke", output.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://someone:secret@github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData(@"C:\clones\origin.git", @"C:\clones\origin.git")]
    [InlineData("", "")]
    public void TheOriginIsHandedOnWithoutCredentials(string url, string handedOn)
    {
        Assert.Equal(handedOn, RemoteRun.WithoutCredentials(url));
    }

    /// <summary>A machine whose every call fails with something simple remote does not expect.</summary>
    private sealed class FailingMachine : IRemoteMachine
    {
        public string Name => "failing";

        public string Root => "C:/nowhere";

        public int Slots => 1;

        public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>();

        public RemoteSweep? Sweep => null;

        public Task<int> RunAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the machine broke");

        public Task SendAsync(string file, string directory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the machine broke");

        public Task ReceiveAsync(string directory, string localParent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the machine broke");
    }

    [Fact]
    public async Task ExecWithoutACommandIsAnError()
    {
        using var stderr = new StringWriter();

        var exitCode = await SimpleCommand.InvokeAsync(
            ["remote", "exec"], Environment with { Error = stderr, RemoteAlias = "remote-test" }, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("needs a command", stderr.ToString(), StringComparison.Ordinal);
    }
}
