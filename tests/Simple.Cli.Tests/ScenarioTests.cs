using System.Text.Json;
using Simple.Cli.Scenarios;

namespace Simple.Cli.Tests;

/// <summary>
/// Every scenario under scenarios/ as one test case, run the way CI, people
/// and AI sessions run it: simple scenario run, as its own set of processes.
/// A scenario passes when its verdict is the one it expects: holds for most;
/// the ones written to prove the runner's other verdicts expect those, and
/// fail if they hold.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class ScenarioTests
{
    private static readonly string Repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);

    public static TheoryData<string> Scenarios() => [.. ScenarioFile.All(Repository).Select(s => s.Name)];

    [Theory(Timeout = 300_000)]
    [MemberData(nameof(Scenarios))]
    public async Task ScenarioEndsWithTheVerdictItExpects(string name)
    {
        var found = ScenarioFile.Find(Repository, name)!.Value;
        var (scenario, problems) = ScenarioFile.Load(Repository, found.Name, found.Path);
        Assert.True(scenario is not null, string.Join(Environment.NewLine, problems));
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();

        var run = await cli.RunJsonAsync("scenario", "run", name, "--instance", instance);

        var result = run.Output.GetProperty("result");
        var report = $"{name} ended with {run.ExitCode}:{Environment.NewLine}{run.Output}{Environment.NewLine}{run.Error}";
        Assert.True(run.ExitCode == scenario.ExpectedVerdict.ExitCode(), report);
        Assert.True(result.GetProperty("asExpected").GetBoolean(), report);
        Assert.Equal(scenario.ExpectedVerdict.Name(), result.GetProperty("verdict").GetString());

        // The evidence, outside the clone: named by time, scenario and run id, every file there.
        var evidence = result.GetProperty("evidence").GetString()!;
        Assert.StartsWith(Path.Combine(cli.Home, "scenario-runs"), evidence, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"-{name}-{result.GetProperty("runId").GetString()}", evidence, StringComparison.Ordinal);
        Assert.True(result.GetProperty("evidenceComplete").GetBoolean(), report);
        foreach (var file in new[] { Evidence.Timeline, Evidence.EventRecord, Evidence.FinalStatus, Evidence.Result, Evidence.Manifest, Evidence.ScenarioCopy })
        {
            Assert.True(File.Exists(Path.Combine(evidence, file)), $"{file} is missing from {evidence}");
        }

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(evidence, Evidence.Logs, "server")));
        Assert.True(File.Exists(Path.Combine(evidence, Evidence.Logs, "server.log")));
        var events = File.ReadAllLines(Path.Combine(evidence, Evidence.EventRecord));
        Assert.Contains(events, line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() == "server.started");
        var timeline = File.ReadAllLines(Path.Combine(evidence, Evidence.Timeline)).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()).ToList();
        Assert.Equal("run-started", timeline[0]);
        Assert.Contains("up", timeline);
        Assert.Equal("finished", timeline[^1]);
        var manifest = File.ReadAllLines(Path.Combine(evidence, Evidence.Manifest));
        Assert.Contains(manifest, line => line.EndsWith($"  {Evidence.Result}", StringComparison.Ordinal));
        Assert.Contains(manifest, line => line.EndsWith($"  {Evidence.Timeline}", StringComparison.Ordinal));

        // The run stops what it started.
        var status = await cli.RunAsync("status", "--instance", instance);
        Assert.Equal(ExitCodes.NotRunning, status.ExitCode);
    }
}
