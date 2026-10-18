using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simple.Cli.Instances;

namespace Simple.Cli.Scenarios;

/// <summary>
/// Runs one scenario as its own local system, the way simple up starts one:
/// resets the instance, starts it with the scenario's initial state, drives
/// its events through the fakes' control APIs, judges its expectations on
/// the server's API and event record, and writes the evidence.
/// </summary>
public sealed class ScenarioRunner(string repository, InstanceStore store, TextWriter progress)
{
    /// <summary>How often the run looks at the system.</summary>
    public static readonly TimeSpan LookInterval = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan UpTimeout = TimeSpan.FromSeconds(120);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The instance a scenario runs as: the clone's own instance name with -scenario, so it never meets the clone's simple up.</summary>
    public static string InstanceFor(string cloneInstance)
    {
        const string suffix = "-scenario";
        return (cloneInstance.Length + suffix.Length > 50 ? cloneInstance[..(50 - suffix.Length)] : cloneInstance) + suffix;
    }

    /// <summary>Where the evidence of every run goes by default: outside the clone, under the command line's home.</summary>
    public static string DefaultEvidenceRoot(InstanceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Path.Combine(store.Home, "scenario-runs");
    }

    /// <summary>
    /// How long stopping the system may take once the run is over, however it
    /// ended: down waits up to 30 s for each process before it kills it.
    /// </summary>
    public static readonly TimeSpan TeardownLimit = TimeSpan.FromMinutes(5);

    /// <summary>Makes a run's evidence (<see cref="Evidence.Create"/>); tests give evidence that cannot be written.</summary>
    internal Func<string, string, DateTimeOffset, Evidence> CreateEvidence { get; init; } = Evidence.Create;

    public async Task<Outcome> RunAsync(Scenario scenario, string instance, string sqlServer, ServerKeyStore keyStore, string evidenceRoot, bool keepUp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        // Two runs on one instance would reset each other's system; the
        // second waits, as up and down do for the instance.
        using var held = await store.LockScenarioAsync(instance, LocalSystem.LockWait, cancellationToken);
        if (held is null)
        {
            return Outcome.Fail(ExitCodes.Busy, $"Another scenario run has been using instance {instance} for {LocalSystem.LockWait.TotalMinutes} minutes.");
        }

        var startedAt = DateTimeOffset.Now;
        using var evidence = CreateEvidence(evidenceRoot, scenario.Name, startedAt);
        evidence.CopyFile(scenario.Path, Evidence.ScenarioCopy);
        evidence.Write("run-started", new { Scenario = scenario.Name, scenario.Title, File = scenario.Path, Instance = instance, evidence.RunId, Repository = repository });
        progress.WriteLine($"Scenario {scenario.Name}: {scenario.Title}");
        progress.WriteLine($"Evidence: {evidence.Directory}");

        var system = new LocalSystem(repository, store, progress);
        var run = new Run(scenario, evidence);
        TeardownResult? teardown = null;
        try
        {
            try
            {
                var setupFailure = await SetUpAsync(system, scenario, instance, sqlServer, keyStore, evidence, cancellationToken);
                if (setupFailure is null)
                {
                    await DriveAsync(system, run, instance, cancellationToken);
                    await CollectAsync(system, run, instance, evidence, cancellationToken);
                }
                else
                {
                    run.CannotDrive(setupFailure);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.CannotDrive("the run was interrupted");
                evidence.Write("interrupted");
            }
            catch (Exception e)
            {
                // Whatever went wrong, the system is still stopped and the result
                // still written: a run that broke off cannot tell.
                run.CannotDrive($"the run broke off: {e.GetType().Name}: {e.Message}");
                evidence.Write("run-error", new { Error = e.ToString() });
            }
        }
        finally
        {
            if (run.Observer is { } observer)
            {
                try
                {
                    await observer.DisposeAsync();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    // Logging out is a courtesy; a login ends by itself.
                }
            }

            // Whatever happened above, the system is stopped.
            teardown = keepUp ? new TeardownResult(Stopped: false, Kept: true, null, null) : await TearDownAsync(system, instance, sqlServer);
            evidence.Write("down", teardown);
        }

        foreach (var tracker in run.Trackers.Where(t => t.Verdict is null))
        {
            tracker.Abandon($"never judged: {run.DriveFailure}", DateTimeOffset.UtcNow);
        }

        evidence.CopyDirectory(store.LogDirectory(instance), Evidence.Logs);

        var verdict = Verdicts.Combine(run.Trackers.Select(t => t.Verdict ?? Verdict.CannotTell), run.Driven);
        var result = new ScenarioResult(
            scenario.Name,
            scenario.Title,
            scenario.Path,
            evidence.RunId,
            instance,
            startedAt,
            DateTimeOffset.Now,
            verdict.Name(),
            scenario.ExpectedVerdict.Name(),
            verdict == scenario.ExpectedVerdict,
            run.DriveFailure,
            [.. run.Trackers.Select(t => new ExpectationResult(
                t.Expectation.Label,
                t.Expectation.Title,
                t.Expectation.Kind.ToString().ToLowerInvariant(),
                t.Expectation.Condition.Describe(),
                (t.Verdict ?? Verdict.CannotTell).Name(),
                t.Reason ?? "never judged",
                t.OpenedAt,
                t.DecidedAt,
                t.Expectation.Line))],
            [.. run.Steps.Select(s => new EventResult(s.Event.Label, s.Event.Action.Kind, s.State, s.Detail, s.StartedAt, s.DoneAt, s.Event.Line))],
            teardown,
            evidence.Directory,
            keepUp ? instance : null,
            evidence.Missing.Count == 0,
            [.. evidence.Missing]);
        evidence.Write("finished", new { Verdict = result.Verdict, result.ExpectedVerdict, result.AsExpected, Teardown = teardown });
        evidence.WriteJson(Evidence.Result, result);
        // Recorded in result.json too late to list itself; the manifest shows
        // every file that is there. Like any evidence, it never changes the verdict.
        try
        {
            evidence.Seal();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            progress.WriteLine($"{Evidence.Manifest} could not be written: {e.Message}");
        }

        var lines = new List<string> { $"Scenario {scenario.Name}: {scenario.Title}" };
        lines.AddRange(result.Expectations.Select(e => $"  {e.Verdict,-15} {e.Name}: {e.Kind} {e.Condition} — {e.Reason}"));
        lines.AddRange(result.Events.Where(e => e.State != "done").Select(e => $"  event {e.Name} ({e.Action}) {e.State}: {e.Detail}"));
        if (result.DriveFailure is { } failure)
        {
            lines.Add($"Not driven as written: {failure}");
        }

        lines.Add($"Verdict: {result.Verdict} (expected {result.ExpectedVerdict}{(result.AsExpected ? ", as expected" : ", NOT as expected")})");
        lines.Add(teardown switch
        {
            { Kept: true } => $"Still running as instance {instance}: simple status --instance {instance}; simple down --instance {instance} when done.",
            { Stopped: true, Error: null } => $"System stopped (instance {instance}; its database stays until the next run resets it).",
            { Stopped: true } => $"System stopped, but: {teardown.Error}",
            _ => $"THE SYSTEM WAS NOT STOPPED: {teardown.Error} Stop it with simple down --instance {instance}.",
        });
        lines.Add($"Evidence: {evidence.Directory}");

        // A system left running is the run's own failure, whatever the
        // verdict: the next run, and the machine, pay for it.
        if (!teardown.Stopped && !teardown.Kept)
        {
            return new Outcome(ExitCodes.Unexpected, $"The scenario's verdict is {result.Verdict}, but its system could not be stopped: {teardown.Error}", result, lines);
        }

        return new Outcome(verdict.ExitCode(), verdict == Verdict.Holds ? null : $"The scenario {(verdict == Verdict.DoesNotHold ? "does not hold" : verdict == Verdict.CannotTell ? "cannot be told" : "is not applicable")}.", result, lines);
    }

    /// <summary>
    /// Stops the system with a time limit of its own: the run's cancellation
    /// (Ctrl+C) is what may have brought it here, and it must stop all the same.
    /// </summary>
    private static async Task<TeardownResult> TearDownAsync(LocalSystem system, string instance, string sqlServer)
    {
        using var limit = new CancellationTokenSource(TeardownLimit);
        Outcome down;
        try
        {
            down = await system.DownAsync(instance, sqlServer, reset: false, limit.Token);
        }
        catch (Exception e)
        {
            return new TeardownResult(!Running(), Kept: false, null, $"stopping the system failed: {e.GetType().Name}: {e.Message}");
        }

        // Whether it stopped is what the processes say: down also ends with 1
        // when they are gone but files they ran from are still held.
        var stopped = !Running();
        return down.ExitCode switch
        {
            ExitCodes.Success => new TeardownResult(stopped, Kept: false, down.ExitCode, stopped ? null : "simple down ended with 0 but a process is still running"),
            // Stopped all the same, by killing what did not stop in time.
            ExitCodes.StopForced => new TeardownResult(stopped, Kept: false, down.ExitCode, down.Error),
            _ => new TeardownResult(stopped, Kept: false, down.ExitCode, $"simple down ended with {down.ExitCode}: {down.Error}"),
        };

        // A state file that cannot be read cannot vouch for anything: taken as still running.
        bool Running()
        {
            try
            {
                return system.IsAnyRunning(instance);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                return true;
            }
        }
    }

    /// <summary>Resets the instance and brings it up in the scenario's initial state; the reason when it could not.</summary>
    private async Task<string?> SetUpAsync(LocalSystem system, Scenario scenario, string instance, string sqlServer, ServerKeyStore keyStore, Evidence evidence, CancellationToken cancellationToken)
    {
        var reset = await system.DownAsync(instance, sqlServer, reset: true, cancellationToken);
        evidence.Write("reset", new { reset.ExitCode, reset.Error });
        if (reset.ExitCode != ExitCodes.Success)
        {
            return $"resetting instance {instance} failed (exit code {reset.ExitCode}): {reset.Error}";
        }

        // Its program logs are kept from one up to the next: start them anew,
        // so the evidence holds this run's alone.
        try
        {
            if (Directory.Exists(store.LogDirectory(instance)))
            {
                Directory.Delete(store.LogDirectory(instance), recursive: true);
            }
        }
        catch (IOException e)
        {
            evidence.Write("old-logs-kept", new { e.Message });
        }

        string? seed = null;
        if (scenario.Initial.FakeMesRows is { } rows)
        {
            seed = Path.Combine(evidence.Directory, "fake-mes-rows.json");
            await File.WriteAllTextAsync(seed, new JsonArray([.. rows.Select(r => (JsonNode)Camel(r))]).ToJsonString(Outcome.JsonOptions), cancellationToken);
        }

        var options = new UpOptions(
            scenario.Initial.Terminals,
            KeyStore: keyStore,
            FakeRiotSpeedup: scenario.Initial.FakeRiotSpeedup,
            FakeMesSeedFile: seed,
            Cargo: scenario.Initial.Cargo,
            Site: scenario.Site);
        var up = await system.UpAsync(instance, sqlServer, options, UpTimeout, cancellationToken);
        evidence.Write("up", new { up.ExitCode, up.Error, up.Result });
        if (up.ExitCode != ExitCodes.Success)
        {
            return $"the system did not come up (simple up exit code {up.ExitCode}): {up.Error}";
        }

        var urls = system.Urls(instance);
        foreach (var vehicle in scenario.Initial.FakeRiotVehicles)
        {
            var (ok, detail) = await SetRiotVehicleAsync(urls, vehicle, cancellationToken);
            evidence.Write("initial", new { Action = "set-riot-vehicle", vehicle.DeviceKey, Ok = ok, Detail = detail });
            if (!ok)
            {
                return $"the fake RIoT's vehicle {vehicle.DeviceKey} could not be set: {detail}";
            }
        }

        return null;
    }

    /// <summary>Looks at the system every <see cref="LookInterval"/>, does each event in turn and judges every open expectation, until all are done.</summary>
    private async Task DriveAsync(LocalSystem system, Run run, string instance, CancellationToken cancellationToken)
    {
        var urls = system.Urls(instance);
        var observer = new Observer(Http, urls[Components.Server.Name], store.ReadAdminPassword(instance), run.Evidence);
        run.Observer = observer;
        // One look before the scenario starts: the first one logs in and reads
        // the whole event record so far, and that time must not count as a
        // stretch of the first windows that no look saw.
        await observer.LookAsync(cancellationToken);
        var start = DateTimeOffset.UtcNow;
        run.Evidence.Write("started", new { Terminals = run.Scenario.Initial.Terminals });
        foreach (var tracker in run.Trackers.Where(t => t.Expectation.After is null))
        {
            tracker.Open(start);
            run.Evidence.Write("window-opened", new { Expectation = tracker.Expectation.Label });
        }

        var next = 0;
        // A when counts from when the event before it started (the start of
        // the scenario for the first): what that event brought about may be
        // recorded before it returned.
        var waitingSince = start;
        while (true)
        {
            var lookStarted = DateTimeOffset.UtcNow;
            var snapshot = await observer.LookAsync(cancellationToken);
            run.LastSnapshot = snapshot;

            if (next < run.Steps.Count)
            {
                var step = run.Steps[next];
                var due = false;
                if (step.Event.At is { } at)
                {
                    due = snapshot.At >= start + at;
                }
                else
                {
                    var seen = step.Event.When!.Observe(snapshot, waitingSince, snapshot.At);
                    due = seen.Truth == Truth.True;
                    if (!due && snapshot.At > waitingSince + step.Event.Within)
                    {
                        Fail(run, next, "timed-out", $"{step.Event.When.Describe()} did not hold within {step.Event.Within.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; last look: {seen.Detail}");
                        next = run.Steps.Count;
                    }
                }

                if (due)
                {
                    progress.WriteLine($"Event {step.Event.Label}: {step.Event.Action.Kind}");
                    // Windows that wait for it open as it starts: what it
                    // brings about can be recorded before it returns (a
                    // stopped terminal is seen gone while it is stopping).
                    var startedAt = DateTimeOffset.UtcNow;
                    var (ok, detail) = await ExecuteAsync(system, instance, urls, run.Scenario.Initial.Terminals, step.Event.Action, cancellationToken);
                    if (ok)
                    {
                        step.State = "done";
                        step.Detail = detail;
                        step.StartedAt = startedAt;
                        step.DoneAt = DateTimeOffset.UtcNow;
                        run.Evidence.Write("event-done", new { Name = step.Event.Label, Action = step.Event.Action.Kind, Arguments = step.Event.Action.Arguments, Detail = detail, StartedAt = startedAt });
                        foreach (var tracker in run.Trackers.Where(t => t.Expectation.After == step.Event.Id && step.Event.Id is not null))
                        {
                            tracker.Open(startedAt);
                            run.Evidence.Write("window-opened", new { Expectation = tracker.Expectation.Label, After = step.Event.Label });
                        }

                        next++;
                        waitingSince = startedAt;
                    }
                    else
                    {
                        Fail(run, next, "failed", detail);
                        next = run.Steps.Count;
                    }
                }
            }

            foreach (var tracker in run.Trackers)
            {
                if (tracker.Look(snapshot))
                {
                    run.Evidence.Write("expectation", new
                    {
                        Name = tracker.Expectation.Label,
                        Verdict = tracker.Verdict!.Value.Name(),
                        tracker.Reason,
                        Condition = tracker.Expectation.Condition.Describe(),
                    });
                    progress.WriteLine($"  {tracker.Verdict.Value.Name()}: {tracker.Expectation.Label}");
                }
            }

            if (next >= run.Steps.Count && run.Trackers.All(t => t.Verdict is not null))
            {
                return;
            }

            var wait = LookInterval - (DateTimeOffset.UtcNow - lookStarted);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }
        }
    }

    /// <summary>An event failed or its condition never came: it and every later one are not done, and the expectations that wait for them cannot be told.</summary>
    private static void Fail(Run run, int index, string state, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        run.CannotDrive($"event {run.Steps[index].Event.Label} {state}: {detail}");
        for (var i = index; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i];
            step.State = i == index ? state : "not-done";
            step.Detail = i == index ? detail : $"event {run.Steps[index].Event.Label} {state} before it";
            run.Evidence.Write("event-" + step.State, new { Name = step.Event.Label, Action = step.Event.Action.Kind, Detail = step.Detail });
            foreach (var tracker in run.Trackers.Where(t => t.Expectation.After == step.Event.Id && step.Event.Id is not null))
            {
                tracker.Abandon($"its window opens after event {step.Event.Label}, which was not done: {step.Detail}", now);
                run.Evidence.Write("expectation", new { Name = tracker.Expectation.Label, Verdict = Verdict.CannotTell.Name(), tracker.Reason });
            }
        }
    }

    /// <summary>The event record and the final status into the evidence, before the system stops.</summary>
    private static async Task CollectAsync(LocalSystem system, Run run, string instance, Evidence evidence, CancellationToken cancellationToken)
    {
        if (run.Observer is { } observer)
        {
            // One more look, so the export holds what the last look missed,
            // and the event expectations are checked against it.
            var last = await observer.LookAsync(cancellationToken);
            foreach (var tracker in run.Trackers.Where(t => t.Recheck(last)))
            {
                evidence.Write("expectation", new { Name = tracker.Expectation.Label, Verdict = tracker.Verdict!.Value.Name(), tracker.Reason, Rechecked = true });
            }

            evidence.WriteLines(Evidence.EventRecord, observer.Events.Select(e => e.Raw.GetRawText()));
            if (last.EventsError is not null)
            {
                evidence.Missing.Add($"{Evidence.EventRecord}: the event record could not be read at the end ({last.EventsError}); it holds what earlier looks saw");
            }

            if (last.Status is { } status)
            {
                evidence.WriteJson(Evidence.FinalStatus, status);
            }
            else
            {
                evidence.Missing.Add($"{Evidence.FinalStatus}: {last.StatusError}");
            }
        }

        var report = await system.StatusAsync(instance, cancellationToken);
        evidence.Write("final-status", new { report.ExitCode, report.Error });
    }

    private static async Task<(bool Ok, string Detail)> ExecuteAsync(
        LocalSystem system, string instance, IReadOnlyDictionary<string, Uri> urls, int terminals, ScenarioAction action, CancellationToken cancellationToken)
    {
        var a = action.Arguments;
        try
        {
            return action.Kind switch
            {
                "add-mes-row" => await SendAsync(HttpMethod.Post, Url(urls, "mes-gateway", "/fake-mes/rows"), Camel(a), cancellationToken),
                "remove-mes-row" => await SendAsync(
                    HttpMethod.Delete,
                    Url(urls, "mes-gateway", $"/fake-mes/rows?taskType={Uri.EscapeDataString((string)a["task-type"]!)}&sublot={Uri.EscapeDataString((string)a["sublot"]!)}"),
                    null,
                    cancellationToken),
                "fail-mes-reads" => await SendAsync(HttpMethod.Post, Url(urls, "mes-gateway", "/fake-mes/failures"), Camel(a), cancellationToken),
                "malform-mes-reads" => await SendAsync(HttpMethod.Post, Url(urls, "mes-gateway", "/fake-mes/malformed"), Camel(a), cancellationToken),
                "set-riot-vehicle" => await SetRiotVehicleAsync(urls, Vehicle(a), cancellationToken),
                "fail-riot-creates" => await SendAsync(HttpMethod.Post, Url(urls, "fake-riot", "/fake-riot/faults/create-00002"), Camel(a), cancellationToken),
                "scan" => await SendAsync(
                    HttpMethod.Post,
                    Url(urls, Process("terminal", a, terminals), "/test/keyboard"),
                    new JsonObject { ["text"] = (string)a["text"]! + "\r" },
                    cancellationToken),
                "press" => await SendAsync(HttpMethod.Post, Url(urls, Process("terminal", a, terminals), "/test/press"), Camel(a, "terminal"), cancellationToken),
                "fault-io-module" => await SendAsync(HttpMethod.Put, Url(urls, Process("io-simulator", a, terminals), "/api/v1/modbus-fault"), Camel(a, "terminal"), cancellationToken),
                "auto-respond" => await SendAsync(HttpMethod.Post, Url(urls, Process("io-simulator", a, terminals), "/api/v1/auto-responder"), Camel(a, "terminal"), cancellationToken),
                "stop-process" => await system.StopProcessAsync(instance, (string)a["process"]!, cancellationToken) is { } stopped
                    ? (true, $"{(string)a["process"]!}: {stopped}")
                    : (false, $"instance {instance} has no process {(string)a["process"]!}"),
                _ => (false, $"no action {action.Kind}"),
            };
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (false, e.Message);
        }
    }

    private static Task<(bool Ok, string Detail)> SetRiotVehicleAsync(IReadOnlyDictionary<string, Uri> urls, RiotVehicleChange vehicle, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Put,
            Url(urls, "fake-riot", $"/fake-riot/vehicles/{Uri.EscapeDataString(vehicle.DeviceKey)}"),
            new JsonObject
            {
                ["battery"] = vehicle.Battery,
                ["brakeReleased"] = vehicle.BrakeReleased,
                ["mode"] = vehicle.Mode,
                ["manualCharger"] = vehicle.ManualCharger,
                ["emergencyButtonPressed"] = vehicle.EmergencyButtonPressed,
            },
            cancellationToken);

    private static RiotVehicleChange Vehicle(JsonObject a) => new(
        (string)a["device-key"]!,
        (int?)a["battery"],
        (bool?)a["brake-released"],
        (string?)a["mode"],
        (bool?)a["manual-charger"],
        (bool?)a["emergency-button-pressed"]);

    private static async Task<(bool Ok, string Detail)> SendAsync(HttpMethod method, Uri uri, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = $"{method} {uri.PathAndQuery} answered {(int)response.StatusCode}" + (text.Length == 0 ? string.Empty : $": {(text.Length > 500 ? text[..500] + "…" : text)}");
        return (response.IsSuccessStatusCode, detail);
    }

    private static Uri Url(IReadOnlyDictionary<string, Uri> urls, string process, string pathAndQuery) =>
        urls.TryGetValue(process, out var root)
            ? new Uri(root, pathAndQuery)
            : throw new HttpRequestException($"the system has no process {process}");

    /// <summary>The process of a terminal's own component: terminal-2, io-simulator-2; or terminal alone when there is one.</summary>
    private static string Process(string component, JsonObject arguments, int terminals) =>
        Components.ProcessNames(Components.All.Single(c => c.Name == component), terminals).ElementAt((int)arguments["terminal"]! - 1);

    /// <summary>The arguments as the control APIs take them: task-type becomes taskType; the keys in <paramref name="except"/> are left out.</summary>
    private static JsonObject Camel(JsonObject arguments, params string[] except)
    {
        var result = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            if (except.Contains(key))
            {
                continue;
            }

            var parts = key.Split('-');
            var name = parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
            result[name] = value?.DeepClone();
        }

        return result;
    }

    /// <summary>What a run keeps while it drives: its events' states and its expectations' trackers.</summary>
    private sealed class Run(Scenario scenario, Evidence evidence)
    {
        public Scenario Scenario { get; } = scenario;

        public Evidence Evidence { get; } = evidence;

        public List<StepState> Steps { get; } = [.. scenario.Events.Select(e => new StepState(e))];

        public List<ExpectationTracker> Trackers { get; } = [.. scenario.Expectations.Select(e => new ExpectationTracker(e))];

        public Observer? Observer { get; set; }

        public Snapshot? LastSnapshot { get; set; }

        public bool Driven => DriveFailure is null;

        /// <summary>Why the scenario could not be driven as written; the first reason only.</summary>
        public string? DriveFailure { get; private set; }

        public void CannotDrive(string reason) => DriveFailure ??= reason;
    }

    private sealed class StepState(ScenarioEvent e)
    {
        public ScenarioEvent Event { get; } = e;

        /// <summary>pending, done, failed, timed-out or not-done.</summary>
        public string State { get; set; } = "pending";

        public string? Detail { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? DoneAt { get; set; }
    }
}

/// <summary>result.json, and the result of simple scenario run --json.</summary>
/// <param name="Instance">The instance still running, with --keep-up; otherwise null.</param>
/// <param name="EvidenceComplete">Every evidence file was collected; <see cref="EvidenceMissing"/> lists what was not. Never changes the verdict.</param>
public sealed record ScenarioResult(
    string Scenario,
    string Title,
    string File,
    string RunId,
    string RunInstance,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Verdict,
    string ExpectedVerdict,
    bool AsExpected,
    string? DriveFailure,
    IReadOnlyList<ExpectationResult> Expectations,
    IReadOnlyList<EventResult> Events,
    TeardownResult Teardown,
    string Evidence,
    string? Instance,
    bool EvidenceComplete,
    IReadOnlyList<string> EvidenceMissing);

/// <summary>How the system was stopped after the run.</summary>
/// <param name="Stopped">Every process of the run is gone (some may have been killed: Error says so).</param>
/// <param name="Kept">--keep-up: left running on purpose.</param>
/// <param name="ExitCode">simple down's exit code, or null when it did not end.</param>
public sealed record TeardownResult(bool Stopped, bool Kept, int? ExitCode, string? Error);

/// <param name="Line">Where the expectation is in the scenario file.</param>
public sealed record ExpectationResult(string Name, string? Title, string Kind, string Condition, string Verdict, string Reason, DateTimeOffset? OpenedAt, DateTimeOffset? DecidedAt, int Line);

/// <param name="State">done, failed, timed-out (its condition never came), not-done (an earlier one failed) or pending (the scenario did not start).</param>
/// <param name="StartedAt">When its action started; the windows of the expectations that wait for it open then.</param>
public sealed record EventResult(string Name, string Action, string State, string? Detail, DateTimeOffset? StartedAt, DateTimeOffset? DoneAt, int Line);
