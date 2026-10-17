using System.CommandLine;
using System.Text.Json;

namespace Simple.Cli.Tests;

public sealed class CommandLineTests
{
    private static readonly CliEnvironment Environment = new(
        Repository: @"C:\clones\29-simple-cli",
        Home: Path.Combine(Path.GetTempPath(), "simple-cli-tests-never-written"),
        SqlServer: SqlServerTarget.LocalDb,
        Out: TextWriter.Null,
        Error: TextWriter.Null);

    private static ParseResult Parse(params string[] args) => SimpleCommand.Create(Environment).Parse(args);

    [Fact]
    public void UpTakesTerminalsInstanceTimeoutAndJson()
    {
        var parse = Parse("up", "--terminals", "3", "--instance", "wt_25", "--timeout", "30", "--json");

        Assert.Empty(parse.Errors);
        Assert.Equal("up", parse.CommandResult.Command.Name);
        Assert.Equal(3, parse.GetValue<int>("--terminals"));
        Assert.Equal(30, parse.GetValue<int>("--timeout"));
        Assert.True(parse.GetValue<bool>("--json"));
        Assert.Equal("wt_25", SimpleCommand.InstanceOf(parse, Environment.Repository));
    }

    [Fact]
    public void ALocalUpStaysInTheCallersJobAndUsesTheUsersKeyStore()
    {
        // Only simple remote up asks otherwise (RemotePlan); a local up, the
        // tests' and CI's, must leave a job that cleans up able to catch its
        // components.
        Assert.Equal(new UpOptions(2, BreakAway: false, KeyStore: ServerKeyStore.User), SimpleCommand.UpOptionsOf(Parse("up", "--terminals", "2")));
        Assert.Equal(new UpOptions(0, BreakAway: true, KeyStore: ServerKeyStore.Machine), SimpleCommand.UpOptionsOf(Parse("up", "--break-away", "--server-key-store", "Machine")));
    }

    [Fact]
    public void UpDefaultsToNoTerminalsAndTwoMinutes()
    {
        var parse = Parse("up");

        Assert.Empty(parse.Errors);
        Assert.Equal(0, parse.GetValue<int>("--terminals"));
        Assert.Equal(120, parse.GetValue<int>("--timeout"));
        Assert.False(parse.GetValue<bool>("--json"));
    }

    [Theory]
    [InlineData(@"C:\clones\29-simple-cli", "29-simple-cli")]
    [InlineData(@"F:\simple\repo\", "repo")]
    [InlineData(@"C:\work\my clone (2)", "my-clone-2")]
    public void TheInstanceDefaultsToTheCloneFolderName(string repository, string expected)
    {
        Assert.Equal(expected, SimpleCommand.InstanceOf(Parse("status"), repository));
    }

    [Theory]
    [InlineData("down", "--reset")]
    [InlineData("status")]
    [InlineData("events", "--since", "5m")]
    [InlineData("events", "--since", "2026-10-17T09:00:00+08:00")]
    [InlineData("logs", "server", "--tail", "20")]
    public void EveryCommandParses(params string[] args)
    {
        Assert.Empty(Parse(args).Errors);
    }

    [Theory]
    [InlineData("up", "--terminals", "-1")]
    [InlineData("up", "--timeout", "0")]
    [InlineData("up", "--instance", "two words")]
    [InlineData("up", "--instance", "../escape")]
    [InlineData("status", "--instance", "x123456789x123456789x123456789x123456789x123456789x")]
    [InlineData("events", "--since", "yesterday")]
    [InlineData("logs")]
    [InlineData("logs", "server", "--tail", "0")]
    [InlineData("restart")]
    [InlineData]
    public void InvalidArgumentsAreErrors(params string[] args)
    {
        Assert.NotEmpty(Parse(args).Errors);
    }

    [Fact]
    public void SinceTakesADurationBackFromNow()
    {
        var since = Parse("events", "--since", "5m").GetValue<DateTimeOffset?>("--since");

        Assert.NotNull(since);
        Assert.InRange(since.Value, DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1));
    }

    [Fact]
    public async Task InvalidArgumentsExitWithTwoAndOneJsonObject()
    {
        using var stdout = new StringWriter();
        var environment = Environment with { Out = stdout };

        var exitCode = await SimpleCommand.InvokeAsync(["up", "--terminals", "-1", "--json"], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        using var output = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(ExitCodes.Usage, output.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("--terminals", output.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoteSqlServerIsRefusedBeforeAnythingStarts()
    {
        using var stdout = new StringWriter();
        var environment = Environment with { Out = stdout, SqlServer = "Server=factory01;Integrated Security=true" };

        var exitCode = await SimpleCommand.InvokeAsync(["up", "--json"], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("factory01", stdout.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Environment.Home));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("status")]
    [InlineData("events")]
    [InlineData("logs")]
    [InlineData("remote")]
    public async Task EachCommandsHelpListsItsExitCodes(string command)
    {
        using var stdout = new StringWriter();

        await SimpleCommand.InvokeAsync([command, "--help"], Environment with { Out = stdout }, TestContext.Current.CancellationToken);

        Assert.Contains(SimpleCommand.ExitCodesOf(command), stdout.ToString(), StringComparison.Ordinal);
        Assert.Subset(SimpleCommand.CommandExitCodes[command].ToHashSet(), new HashSet<int> { ExitCodes.Unexpected, ExitCodes.Usage });
    }

    [Fact]
    public async Task HelpListsEveryExitCode()
    {
        using var stdout = new StringWriter();

        var exitCode = await SimpleCommand.InvokeAsync(["--help"], Environment with { Out = stdout }, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
        foreach (var code in typeof(ExitCodes).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(int)))
        {
            Assert.Contains($"  {code.GetValue(null)}  ", stdout.ToString(), StringComparison.Ordinal);
        }
    }
}
