using Simple.Cli.Scenarios;

namespace Simple.Cli.Tests;

/// <summary>
/// Reading scenario files: every scenario under scenarios/ is valid, and a
/// file that is not says what is wrong and on which line, whether the format
/// (scenario.schema.json), a rule it cannot express or the site is broken.
/// </summary>
public sealed class ScenarioFileTests : IDisposable
{
    private static readonly string Repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);

    private readonly string directory = Directory.CreateTempSubdirectory("simple-scenario-file-").FullName;

    public static TheoryData<string> Scenarios() => [.. ScenarioFile.All(Repository).Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryScenarioInTheRepositoryIsValid(string name)
    {
        var found = ScenarioFile.Find(Repository, name)!.Value;

        var (scenario, problems) = ScenarioFile.Load(Repository, found.Name, found.Path);

        Assert.Empty(problems);
        Assert.Equal(name, scenario!.Name);
        Assert.NotEmpty(scenario.Expectations);
    }

    [Fact]
    public void TheFirstScenariosAreThere() =>
        Assert.Superset(
            new HashSet<string> { "infrastructure-up", "terminal-disconnect", "mes-source-fails-and-recovers", "io-module-unreadable", "deliberately-wrong", "cannot-tell-on-purpose" },
            ScenarioFile.All(Repository).Select(s => s.Name).ToHashSet());

    [Fact]
    public void AValidFileReadsAsWritten()
    {
        var scenario = Valid("""
            title: t
            site: sample
            expected-verdict: does-not-hold
            initial:
              terminals: 2
              fake-riot:
                speedup: 2.5
                vehicles:
                  - { device-key: BROKERX-0000000000000000000000000000fa02, battery: 40 }
              fake-mes:
                rows:
                  - { task-type: SAMPLE_TO_GATE, sublot: "007.10", area: S2-1 }
              slots:
                - { terminal: 2, cargo: [3, 1, 3] }
            events:
              - id: first
                at: 1.5s
                scan: { terminal: 2, text: LOT1 }
              - when: { event: onboard.input-received, data: { text: LOT1 } }
                within: 2m
                stop-process: { process: terminal-2 }
            expectations:
              - eventually: { status: "onboardTerminals[vehicleId=vehicle-2].connected", equals: true }
                within: 500ms
                only-if: { status: onboardTerminals.count, at-least: 1 }
              - id: quiet
                never: { event: onboard.refused, source: server }
                for: 10s
                after: first
            """);

        Assert.Equal(Verdict.DoesNotHold, scenario.ExpectedVerdict);
        Assert.Equal(2, scenario.Initial.Terminals);
        Assert.Equal(2.5, scenario.Initial.FakeRiotSpeedup);
        Assert.Equal(40, scenario.Initial.FakeRiotVehicles.Single().Battery);
        Assert.Equal("007.10", (string)scenario.Initial.FakeMesRows!.Single()["sublot"]!);
        Assert.Equal([1, 3], scenario.Initial.Cargo[2]);
        Assert.Equal(TimeSpan.FromSeconds(1.5), scenario.Events[0].At);
        Assert.Equal("scan", scenario.Events[0].Action.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), scenario.Events[1].Within);
        Assert.Equal("event 2", scenario.Events[1].Label);
        Assert.IsType<EventCondition>(scenario.Events[1].When);
        var first = scenario.Expectations[0];
        Assert.Equal(ExpectationKind.Eventually, first.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(500), first.Window);
        Assert.IsType<StatusCondition>(first.OnlyIf);
        Assert.Equal(23, first.Line);
        Assert.Equal(ExpectationKind.Never, scenario.Expectations[1].Kind);
        Assert.Equal("first", scenario.Expectations[1].After);
    }

    [Theory]
    // The format itself.
    [InlineData("title: t\nsite: sample\nexpectations: []\n", 3, "'expectations' needs at least 1 item")]
    [InlineData("title: t\nsite: sample\ninitial:\n  terminals: two\nexpectations: [{ never: { event: x }, for: 1s }]\n", 4, "'terminals' must be a whole number")]
    [InlineData("title: t\nsite: sample\ninitial:\n  termnals: 1\nexpectations: [{ never: { event: x }, for: 1s }]\n", 4, "'termnals' is not a key the format knows here")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - never: { event: x }\n    for: 10 seconds\n", 5, "'for' must be a duration such as 500ms, 5s or 2m")]
    [InlineData("site: sample\nexpectations: [{ never: { event: x }, for: 1s }]\n", 1, "'title' is missing")]
    [InlineData("title: t\nsite: sample\nexpected-verdict: passes\nexpectations: [{ never: { event: x }, for: 1s }]\n", 3, "must be one of holds, does-not-hold, cannot-tell, not-applicable")]
    [InlineData("title: t\nsite: sample\nsite: d8\nexpectations: [{ never: { event: x }, for: 1s }]\n", 3, "Duplicate key site")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - never: [unclosed\n", 5, "")]
    // Rules the format cannot express.
    [InlineData("title: t\nsite: sample\nevents:\n  - at: 1s\n    fail-mes-reads: { count: 1 }\n    malform-mes-reads: { count: 1 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 4, "exactly one action, not fail-mes-reads and malform-mes-reads")]
    [InlineData("title: t\nsite: sample\nevents:\n  - at: 1s\n    when: { event: x }\n    fail-mes-reads: { count: 1 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 4, "exactly one of at (a time) and when (a condition)")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - eventually: { status: riot.state, equals: a, not-equals: b }\n    within: 1s\n", 4, "exactly one of equals, not-equals, at-least, at-most")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - eventually: { status: \"riot..state\", equals: a }\n    within: 1s\n", 4, "is neither a field")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - eventually: { event: x }\n    for: 1s\n", 5, "eventually takes within, not for")]
    [InlineData("title: t\nsite: sample\nexpectations:\n  - never: { event: x }\n    for: 1s\n    after: nothing\n", 6, "no event is called 'nothing'")]
    [InlineData("title: t\nsite: sample\nevents:\n  - at: 1s\n    scan: { terminal: 2, text: x }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 5, "there is no terminal 2")]
    [InlineData("title: t\nsite: sample\ninitial: { terminals: 1 }\nevents:\n  - at: 1s\n    stop-process: { process: terminal-1 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 6, "the system has no process 'terminal-1'")]
    // The site.
    [InlineData("title: t\nsite: nowhere\nexpectations: [{ never: { event: x }, for: 1s }]\n", 2, "site 'nowhere' has no")]
    [InlineData("title: t\nsite: sample\ninitial: { terminals: 4 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 3, "site 'sample' has 3 vehicles, not 4")]
    [InlineData("title: t\nsite: sample\nevents:\n  - at: 1s\n    add-mes-row: { task-type: SAMPLE_TO_OVEN, sublot: s, area: D8-9 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 5, "site 'sample' has no area 'D8-9'")]
    [InlineData("title: t\nsite: sample\nevents:\n  - at: 1s\n    set-riot-vehicle: { device-key: BROKERX-x, battery: 5 }\nexpectations: [{ never: { event: x }, for: 1s }]\n", 5, "site 'sample' has no vehicle with RIoT device key 'BROKERX-x'")]
    public void AFileThatIsNotValidSaysWhatAndOnWhichLine(string yaml, int line, string message)
    {
        var path = Write(yaml);

        var (scenario, problems) = ScenarioFile.Load(Repository, "bad", path);

        Assert.Null(scenario);
        Assert.Contains(problems, p => p.Line == line && p.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void ScenariosAreFoundByNameOrByPath()
    {
        var path = Write("title: t\nsite: sample\nexpectations: [{ never: { event: x }, for: 1s }]\n");

        Assert.Equal("infrastructure-up", ScenarioFile.Find(Repository, "infrastructure-up")!.Value.Name);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), ScenarioFile.Find(Repository, path)!.Value.Name);
        Assert.Null(ScenarioFile.Find(Repository, "no-such-scenario"));
    }

    [Fact]
    public async Task ValidateNamesTheLineAndEndsWith2()
    {
        var path = Write("title: t\nsite: sample\nexpectations:\n  - never: { event: x }\n    for: soon\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var environment = new CliEnvironment(Repository, directory, SqlServerTarget.LocalDb, stdout, stderr);

        var invalid = await SimpleCommand.InvokeAsync(["scenario", "validate", path], environment, TestContext.Current.CancellationToken);
        var valid = await SimpleCommand.InvokeAsync(["scenario", "validate"], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, invalid);
        Assert.Contains($"{path}: line 5, column 10: 'for' must be a duration", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, valid);
        Assert.Contains("infrastructure-up: valid", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRefusesAFileThatIsNotValidBeforeStartingAnything()
    {
        var path = Write("title: t\nsite: sample\nexpectations:\n  - never: { event: x }\n");
        using var stderr = new StringWriter();
        var environment = new CliEnvironment(Repository, directory, SqlServerTarget.LocalDb, TextWriter.Null, stderr);

        var exitCode = await SimpleCommand.InvokeAsync(["scenario", "run", path], environment, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("never needs for", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "instances")));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private Scenario Valid(string yaml)
    {
        var (scenario, problems) = ScenarioFile.Load(Repository, "valid", Write(yaml));
        Assert.Empty(problems);
        return scenario!;
    }

    private string Write(string yaml)
    {
        var path = Path.Combine(directory, $"s{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
