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
        var evidence = result.GetProperty("evidence").GetString()!;
        try
        {
            var report = $"{name} ended with {run.ExitCode}:{Environment.NewLine}{run.Output}{Environment.NewLine}{run.Error}";
            Assert.True(run.ExitCode == scenario.ExpectedVerdict.ExitCode(), report);
            Assert.True(result.GetProperty("asExpected").GetBoolean(), report);
            Assert.Equal(scenario.ExpectedVerdict.Name(), result.GetProperty("verdict").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("driveFailure").ValueKind);
            Assert.True(result.GetProperty("teardown").GetProperty("stopped").GetBoolean(), report);

            // The evidence, outside the clone: named by time, scenario and run id, every file there.
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
        catch
        {
            // CliRunner deletes its home, and the evidence with it: keep a copy
            // where the run's results are collected.
            Keep(evidence);
            throw;
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task ARunWhoseEvidenceCannotBeWrittenStillStopsItsSystem()
    {
        // A full disk: not one evidence file can be written, the timeline included.
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();
        var found = ScenarioFile.Find(Repository, "deliberately-wrong")!.Value;
        var scenario = ScenarioFile.Load(Repository, found.Name, found.Path).Scenario!;
        var runner = new ScenarioRunner(Repository, new Instances.InstanceStore(cli.Home), TextWriter.Null)
        {
            CreateEvidence = (root, name, _) => new Evidence(Path.Combine(root, "never-created", name), "unwritable", new FailingWriter()),
        };

        var outcome = await runner.RunAsync(
            scenario, instance, CliRunner.SqlServer, TestKeyStore.Store, Path.Combine(cli.Home, "evidence"), keepUp: false, TestContext.Current.CancellationToken);

        var result = Assert.IsType<ScenarioResult>(outcome.Result);
        Assert.True(result.Teardown.Stopped, result.Teardown.Error);
        Assert.Equal(Verdict.DoesNotHold.ExitCode(), outcome.ExitCode);
        Assert.False(result.EvidenceComplete);
        Assert.Contains(result.EvidenceMissing, m => m.StartsWith(Evidence.Timeline, StringComparison.Ordinal));
        var status = await cli.RunAsync("status", "--instance", instance);
        Assert.Equal(ExitCodes.NotRunning, status.ExitCode);
    }

    /// <summary>A timeline on a full disk.</summary>
    private sealed class FailingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("There is not enough space on the disk.");

        public override void WriteLine(string? value) => throw new IOException("There is not enough space on the disk.");
    }

    [Fact(Timeout = 300_000)]
    public async Task AnInterruptedRunStillStopsItsSystemAndWritesItsResult()
    {
        // Ctrl+C, as the command line sees it: the run's cancellation, once
        // the system is up and the run is watching it.
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();
        var directory = Directory.CreateDirectory(Path.Combine(cli.Home, "interrupted")).FullName;
        var file = Path.Combine(directory, "long-watch.yaml");
        await File.WriteAllTextAsync(
            file,
            "title: t\nsite: sample\nexpectations:\n  - always: { status: riot.state, equals: connected }\n    for: 2m\n",
            TestContext.Current.CancellationToken);
        var evidenceRoot = Path.Combine(directory, "evidence");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var environment = new CliEnvironment(cli.Repository, cli.Home, CliRunner.SqlServer, stdout, stderr);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = SimpleCommand.InvokeAsync(
            ["scenario", "run", file, "--instance", instance, "--evidence", evidenceRoot, "--json", SimpleCommand.ServerKeyStoreOption, TestKeyStore.Store.ToString()],
            environment,
            interrupt.Token);
        while (!run.IsCompleted && !Watching(evidenceRoot))
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        await interrupt.CancelAsync();
        var exitCode = await run;

        var output = JsonDocument.Parse(stdout.ToString()).RootElement;
        var result = output.GetProperty("result");
        Assert.True(exitCode == ExitCodes.CannotTell, $"{exitCode}: {output}{Environment.NewLine}{stderr}");
        Assert.Equal("cannot-tell", result.GetProperty("verdict").GetString());
        Assert.Equal("the run was interrupted", result.GetProperty("driveFailure").GetString());
        Assert.True(result.GetProperty("teardown").GetProperty("stopped").GetBoolean());
        Assert.True(File.Exists(Path.Combine(result.GetProperty("evidence").GetString()!, Evidence.Result)));
        var status = await cli.RunAsync("status", "--instance", instance);
        Assert.Equal(ExitCodes.NotRunning, status.ExitCode);

        static bool Watching(string root) =>
            Directory.Exists(root)
            && Directory.EnumerateFiles(root, Evidence.Timeline, SearchOption.AllDirectories)
                .Any(path => ReadShared(path).Contains("\"kind\":\"window-opened\"", StringComparison.Ordinal));
    }

    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Copies a failed run's evidence to SIMPLE_SCENARIO_EVIDENCE (CI sets it
    /// under its test results), to the run directory simple remote brings
    /// back, or under artifacts/ in the clone.
    /// </summary>
    private static void Keep(string evidence) =>
        TestEvidence.Keep(evidence, Path.GetFileName(evidence), "SIMPLE_SCENARIO_EVIDENCE", "scenario-evidence");
}
