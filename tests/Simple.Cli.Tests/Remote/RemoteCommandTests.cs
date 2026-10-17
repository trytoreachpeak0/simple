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
        var context = new ComponentContext("i", SqlServerTarget.LocalDb, 1, [20000, 20001], new Dictionary<string, IReadOnlyList<int>> { ["mes-gateway"] = [20002] }, "d", store);

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
    public async Task ExecWithoutACommandIsAnError()
    {
        using var stderr = new StringWriter();

        var exitCode = await SimpleCommand.InvokeAsync(
            ["remote", "exec"], Environment with { Error = stderr, RemoteAlias = "simple01" }, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("needs a command", stderr.ToString(), StringComparison.Ordinal);
    }
}
