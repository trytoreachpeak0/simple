using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Cli.Instances;
using Simple.Cli.Processes;

namespace Simple.Cli;

/// <summary>Brings one instance of the local system up and down and reports on it.</summary>
public sealed partial class LocalSystem(string repository, InstanceStore store, TextWriter progress)
{
    public static readonly TimeSpan LockWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Settings the components must not pick up from the caller's environment:
    /// the command line starts a local system only, and these are how a real
    /// endpoint or another database would reach a component.
    /// </summary>
    private static readonly string[] ScrubbedPrefixes = ["Simple__", "ConnectionStrings__", "ASPNETCORE_"];

    private static readonly string[] ScrubbedNames =
    [
        "urls", "http_ports", "https_ports", "DOTNET_URLS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS",
        // The components run as Production, whatever the caller's shell says.
        "DOTNET_ENVIRONMENT",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// The configuration the command line itself was built in (Debug through
    /// simple.ps1, Release on CI). Components are built the same way, so on CI
    /// the solution's Release build is reused instead of building Debug anew.
    /// </summary>
    private static readonly string Configuration =
        typeof(LocalSystem).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration is { Length: > 0 } configuration
            ? configuration
            : "Debug";

    public string Repository { get; } = repository;

    public async Task<Outcome> UpAsync(string instance, string sqlServer, UpOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var components = Components.All.Where(c => c.Count(options) > 0).ToList();
        if (options.Terminals > 0 && Components.All.All(c => c.Count(options) == c.Count(options with { Terminals = 0 })))
        {
            return Outcome.Fail(ExitCodes.Usage, "--terminals: no component of this build runs per onboard terminal yet.");
        }

        using var held = await store.LockInstanceAsync(instance, LockWait, cancellationToken);
        if (held is null)
        {
            return Busy(instance);
        }

        if (store.Read(instance) is { } existing)
        {
            var alive = existing.Processes.Where(p => IsAlive(p)).ToList();
            if (alive.Count > 0 && !SamePath(existing.Repository, Repository))
            {
                return Outcome.Fail(
                    ExitCodes.Unhealthy,
                    $"Instance {instance} is running from another clone, {existing.Repository}. Use --instance to pick another name.");
            }

            if (alive.Count == existing.Processes.Count && alive.Count > 0)
            {
                var wanted = components.SelectMany(c => Components.ProcessNames(c, c.Count(options))).Order();
                if (!wanted.SequenceEqual(existing.Processes.Select(p => p.Name).Order()))
                {
                    return Outcome.Fail(
                        ExitCodes.Unhealthy,
                        $"Instance {instance} is already running with other components ({string.Join(", ", existing.Processes.Select(p => p.Name))}). Run simple down first.");
                }

                var report = await ReportAsync(existing, cancellationToken);
                return report.AllReady
                    ? Outcome.Ok(report with { AlreadyRunning = true }, [$"Instance {instance} is already running; nothing started.", .. report.Lines()])
                    : Outcome.Fail(ExitCodes.Unhealthy, $"Instance {instance} is already running but not every component is ready.", report, report.Lines());
            }

            if (alive.Count > 0)
            {
                return Outcome.Fail(
                    ExitCodes.Unhealthy,
                    $"Instance {instance} is partly running: {string.Join(", ", alive.Select(p => $"{p.Name} (pid {p.Pid})"))} alive, the rest gone. Run simple down first.");
            }

            progress.WriteLine($"Instance {instance} was not stopped with simple down last time; its processes are gone. Starting afresh.");
            store.Delete(existing);
        }

        if (await VerifySqlServerAsync(sqlServer, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Kept after down, so down --reset finds the database where up made it
        // even when the caller's SIMPLE_SQLSERVER has changed since.
        store.WriteSqlServer(instance, sqlServer);
        Directory.CreateDirectory(store.LogDirectory(instance));
        File.Delete(store.LogPath(instance, "build"));
        var executables = new Dictionary<string, string>();
        using (var buildHeld = await InstanceStore.LockBuildAsync(Repository, LockWait, cancellationToken))
        {
            if (buildHeld is null)
            {
                return Outcome.Fail(ExitCodes.Busy, $"Another simple command has been building in {Repository} for {LockWait.TotalMinutes} minutes.");
            }

            foreach (var component in components)
            {
                var built = await BuildAsync(instance, component, cancellationToken);
                if (built.Error is not null)
                {
                    return built.Error;
                }

                executables[component.Name] = built.Executable!;
            }
        }

        var processes = new List<ProcessRecord>();
        InstanceState state;
        using (var portsHeld = await store.LockPortsAsync(LockWait, cancellationToken))
        {
            if (portsHeld is null)
            {
                return Outcome.Fail(ExitCodes.Busy, $"Another simple command has held {Path.Combine(store.Home, "ports.lock")} for {LockWait.TotalMinutes} minutes.");
            }

            var claimed = store.PortsClaimedByOthers(instance);
            var needed = components.Sum(c => c.Count(options) * c.PortsPerProcess);
            var ports = PortAllocator.Allocate(instance, needed, port => !claimed.Contains(port) && PortAllocator.CanBind(port));
            state = new InstanceState(instance, Repository, sqlServer, DateTimeOffset.UtcNow, ports, processes);
            store.Write(state);

            // Hand the ports out in start order.
            var next = 0;
            var portsOf = new Dictionary<string, IReadOnlyList<int>>();
            var contexts = new List<(Component Component, string Name, ComponentContext Context)>();
            foreach (var component in components)
            {
                var index = 0;
                foreach (var name in Components.ProcessNames(component, component.Count(options)))
                {
                    index++;
                    portsOf[name] = ports.Skip(next).Take(component.PortsPerProcess).ToList();
                    next += component.PortsPerProcess;
                    contexts.Add((component, name, new ComponentContext(instance, sqlServer, index, portsOf[name], portsOf)));
                }
            }

            var environment = ComponentEnvironment();
            foreach (var (component, name, context) in contexts)
            {
                var executable = executables[component.Name];
                var log = store.LogPath(instance, name);
                progress.WriteLine($"Starting {name}...");
                (int Pid, DateTimeOffset StartedAt) started;
                try
                {
                    started = ChildProcess.Start(executable, component.Arguments(context), Path.GetDirectoryName(executable)!, environment, log);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
                {
                    var stopped = await StopAllAsync(state, cancellationToken);
                    store.Delete(state);
                    return Outcome.Fail(ExitCodes.NotReady, $"{name} could not be started: {e.Message}", stopped);
                }

                processes.Add(new ProcessRecord(name, component.Name, context.Index, started.Pid, started.StartedAt, executable, context.Ports, log));
                store.Write(state);
            }
        }

        var failure = await WaitUntilReadyAsync(state, timeout, cancellationToken);
        if (failure is not null)
        {
            var stopped = await StopAllAsync(state, cancellationToken);
            store.Delete(state);
            var tail = Tail(failure.Value.Process.Log, 20);
            return Outcome.Fail(
                ExitCodes.NotReady,
                $"{failure.Value.Process.Name} {failure.Value.Reason}. Its log: {failure.Value.Process.Log}",
                new { failure.Value.Process.Name, failure.Value.Reason, failure.Value.Process.Log, LogTail = tail, Stopped = stopped },
                ["Last lines of the log:", .. tail]);
        }

        var ready = await ReportAsync(state, cancellationToken);
        return Outcome.Ok(ready, [$"Instance {instance} is up.", .. ready.Lines()]);
    }

    public async Task<Outcome> DownAsync(string instance, string sqlServer, bool reset, CancellationToken cancellationToken)
    {
        using var held = await store.LockInstanceAsync(instance, LockWait, cancellationToken);
        if (held is null)
        {
            return Busy(instance);
        }

        var state = store.Read(instance);
        if (state is not null && !SamePath(state.Repository, Repository))
        {
            return Outcome.Fail(
                ExitCodes.Unhealthy,
                $"Instance {instance} was started from another clone, {state.Repository}. Run simple down there, or use --instance.");
        }

        var stopped = new List<StopReport>();
        if (state is not null)
        {
            stopped.AddRange(await StopAllAsync(state, cancellationToken));
            var left = state.Processes.Where(p => IsAlive(p)).ToList();
            if (left.Count > 0)
            {
                return Outcome.Fail(
                    ExitCodes.Unexpected,
                    $"Still running after stop: {string.Join(", ", left.Select(p => $"{p.Name} (pid {p.Pid})"))}. The state file {store.StatePath(instance)} is kept.",
                    new { Instance = instance, Stopped = stopped });
            }

            store.Delete(state);
        }

        sqlServer = state?.SqlServer ?? store.ReadSqlServer(instance) ?? sqlServer;

        var lines = new List<string>();
        lines.AddRange(state is null
            ? [$"Instance {instance} is not running."]
            : stopped.Select(s => $"{s.Name} (pid {s.Pid}): {s.Outcome}"));

        bool? dropped = null;
        if (reset)
        {
            if (await VerifySqlServerAsync(sqlServer, cancellationToken) is { } refused)
            {
                return refused with { Result = new { Instance = instance, Stopped = stopped }, Lines = lines };
            }

            try
            {
                dropped = await SqlServerTarget.DropDatabaseAsync(sqlServer, instance, cancellationToken);
                store.ForgetSqlServer(instance);
            }
            catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                return Outcome.Fail(
                    ExitCodes.DatabaseFailed,
                    $"Could not drop {SqlServerTarget.DatabaseName(instance)} on {SqlServerTarget.DataSource(sqlServer)}: {e.Message}",
                    new { Instance = instance, Stopped = stopped },
                    lines);
            }

            lines.Add(dropped.Value
                ? $"Dropped database {SqlServerTarget.DatabaseName(instance)} on {SqlServerTarget.DataSource(sqlServer)}."
                : $"Database {SqlServerTarget.DatabaseName(instance)} did not exist on {SqlServerTarget.DataSource(sqlServer)}.");
        }

        var result = new { Instance = instance, WasRunning = state is not null, Stopped = stopped, DatabaseDropped = dropped };
        var forced = stopped.Where(s => s.Outcome == StopOutcome.Killed).ToList();
        return forced.Count > 0
            ? Outcome.Fail(
                ExitCodes.StopForced,
                $"Killed after {StopTimeout.TotalSeconds} s without stopping: {string.Join(", ", forced.Select(s => s.Name))}. Their stop may be missing from the event record.",
                result,
                lines)
            : Outcome.Ok(result, lines);
    }

    public async Task<Outcome> StatusAsync(string instance, CancellationToken cancellationToken)
    {
        if (store.Read(instance) is not { } state)
        {
            return NotRunning(instance);
        }

        var report = await ReportAsync(state, cancellationToken);
        return report.AllReady
            ? Outcome.Ok(report, report.Lines())
            : Outcome.Fail(ExitCodes.Unhealthy, $"Instance {instance} is not healthy: not every component is running and ready.", report, report.Lines());
    }

    public async Task<Outcome> EventsAsync(string instance, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        if (store.Read(instance) is not { } state)
        {
            return NotRunning(instance);
        }

        var server = state.Processes.FirstOrDefault(p => p.Component == Components.Server.Name);
        if (server is null || !IsAlive(server))
        {
            return Outcome.Fail(ExitCodes.NotRunning, $"The server of instance {instance} is not running. Run simple up.");
        }

        var baseUri = ApiUri(server);
        var events = new List<JsonElement>();
        long? after = null;
        while (true)
        {
            var query = "limit=1000"
                + (after is { } cursor ? $"&after={cursor.ToString(CultureInfo.InvariantCulture)}" : string.Empty)
                + (since is { } from ? $"&since={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" : string.Empty);
            var page = await Http.GetFromJsonAsync<JsonElement>(new Uri(baseUri, $"/api/v1/events?{query}"), cancellationToken);
            events.AddRange(page.GetProperty("items").EnumerateArray());
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            after = page.GetProperty("nextCursor").GetInt64();
        }

        return Outcome.Ok(new { Instance = instance, Events = events }, events.Select(FormatEvent).ToList());
    }

    public Outcome Logs(string instance, string name, int? tail)
    {
        var path = store.LogPath(instance, name);
        if (!LogName().IsMatch(name) || !File.Exists(path))
        {
            var known = Directory.Exists(store.LogDirectory(instance))
                ? Directory.EnumerateFiles(store.LogDirectory(instance), "*.log").Select(Path.GetFileNameWithoutExtension).Order().ToList()
                : [];
            return Outcome.Fail(
                ExitCodes.NotRunning,
                $"Instance {instance} has no log '{name}'. "
                + (known.Count > 0 ? $"Logs: {string.Join(", ", known)}." : "It has never been started here."));
        }

        var lines = tail is { } count ? Tail(path, count) : ReadLines(path);
        return Outcome.Ok(new { Instance = instance, Name = name, Path = path, Lines = lines }, lines);
    }

    /// <summary>Asks the SQL Server which instance it is (<see cref="SqlServerTarget.VerifyAsync"/>); null when it may be used.</summary>
    private static async Task<Outcome?> VerifySqlServerAsync(string sqlServer, CancellationToken cancellationToken)
    {
        string? refusal;
        try
        {
            refusal = await SqlServerTarget.VerifyAsync(sqlServer, cancellationToken);
        }
        catch (Microsoft.Data.SqlClient.SqlException e)
        {
            return Outcome.Fail(ExitCodes.DatabaseFailed, $"Could not reach the SQL Server {SqlServerTarget.DataSource(sqlServer)}: {e.Message}");
        }

        return refusal is null ? null : Outcome.Fail(ExitCodes.Usage, refusal);
    }

    private static Outcome Busy(string instance) =>
        Outcome.Fail(ExitCodes.Busy, $"Another simple command has been working on instance {instance} for {LockWait.TotalMinutes} minutes.");

    private static Outcome NotRunning(string instance) =>
        Outcome.Fail(ExitCodes.NotRunning, $"Instance {instance} is not running. Run simple up.");

    private static bool IsAlive(ProcessRecord process)
    {
        using var found = ChildProcess.Find(process.Pid, process.ProcessStartedAt);
        return found is not null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static Uri ApiUri(ProcessRecord process) =>
        new($"http://127.0.0.1:{process.Ports[Components.All.First(c => c.Name == process.Component).ApiPort ?? 0].ToString(CultureInfo.InvariantCulture)}");

    private static Dictionary<string, string> ComponentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!ScrubbedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && !ScrubbedNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                environment[key] = (string?)entry.Value ?? string.Empty;
            }
        }

        return environment;
    }

    /// <summary>Builds a component's project once and copies the output to where the instance runs it from.</summary>
    private async Task<(string? Executable, Outcome? Error)> BuildAsync(string instance, Component component, CancellationToken cancellationToken)
    {
        var project = Path.Combine(Repository, component.Project);
        var log = store.LogPath(instance, "build");
        progress.WriteLine($"Building {component.Project}...");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "build", project, "-c", Configuration, "-nologo", "-nodeReuse:false", "-t:Build", "-getProperty:TargetPath" })
        {
            start.ArgumentList.Add(argument);
        }

        using var build = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet build.");
        var output = build.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = build.StandardError.ReadToEndAsync(cancellationToken);
        await build.WaitForExitAsync(cancellationToken);
        var text = await output + await errors;
        await File.AppendAllTextAsync(log, $"> dotnet {string.Join(' ', start.ArgumentList)}{Environment.NewLine}{text}", cancellationToken);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (build.ExitCode != 0 || lines.Length == 0)
        {
            return (null, Outcome.Fail(
                ExitCodes.BuildFailed,
                $"Building {component.Project} failed (dotnet exit code {build.ExitCode}). The build log: {log}",
                new { component.Project, Log = log, Output = lines },
                lines));
        }

        var targetPath = lines[^1];
        var source = Path.GetDirectoryName(targetPath)!;
        var bin = InstanceStore.BinDirectory(Repository, instance, component.Name);
        if (Directory.Exists(bin))
        {
            Directory.Delete(bin, recursive: true);
        }

        CopyDirectory(source, bin);
        return (Path.Combine(bin, Path.ChangeExtension(Path.GetFileName(targetPath), ".exe")), null);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private async Task<(ProcessRecord Process, string Reason)?> WaitUntilReadyAsync(InstanceState state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var waiting = state.Processes.ToList();
        progress.WriteLine($"Waiting for {string.Join(", ", waiting.Select(p => p.Name))} to be ready...");
        while (true)
        {
            foreach (var process in waiting.ToList())
            {
                if (!IsAlive(process))
                {
                    return (process, "exited before it was ready");
                }

                if (await IsReadyAsync(state, process, cancellationToken))
                {
                    waiting.Remove(process);
                }
            }

            if (waiting.Count == 0)
            {
                return null;
            }

            if (deadline.Elapsed > timeout)
            {
                return (waiting[0], $"was not ready within {timeout.TotalSeconds} s");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static Task<bool> IsReadyAsync(InstanceState state, ProcessRecord process, CancellationToken cancellationToken)
    {
        var component = Components.All.First(c => c.Name == process.Component);
        var portsOf = state.Processes.ToDictionary(p => p.Name, p => p.Ports);
        return component.IsReady(new ComponentContext(state.Instance, state.SqlServer, process.Index, process.Ports, portsOf), Http, cancellationToken);
    }

    /// <summary>Stops every recorded process, last started first.</summary>
    private async Task<List<StopReport>> StopAllAsync(InstanceState state, CancellationToken cancellationToken)
    {
        var reports = new List<StopReport>();
        foreach (var process in state.Processes.Reverse())
        {
            progress.WriteLine($"Stopping {process.Name} (pid {process.Pid})...");
            reports.Add(new StopReport(process.Name, process.Pid, await StopAsync(process, cancellationToken)));
        }

        return reports;
    }

    private static async Task<StopOutcome> StopAsync(ProcessRecord record, CancellationToken cancellationToken)
    {
        using var process = ChildProcess.Find(record.Pid, record.ProcessStartedAt);
        if (process is null)
        {
            return StopOutcome.AlreadyGone;
        }

        if (await ChildProcess.RequestStopAsync(record.Pid, cancellationToken))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(StopTimeout);
            try
            {
                await process.WaitForExitAsync(wait.Token);
                return StopOutcome.Stopped;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Did not stop in time: killed below.
            }
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
        return StopOutcome.Killed;
    }

    private static async Task<InstanceReport> ReportAsync(InstanceState state, CancellationToken cancellationToken)
    {
        var processes = new List<ProcessReport>();
        JsonElement? server = null;
        foreach (var process in state.Processes)
        {
            var alive = IsAlive(process);
            var ready = alive && await IsReadyAsync(state, process, cancellationToken);
            processes.Add(new ProcessReport(process.Name, process.Component, process.Pid, alive, ready, ApiUri(process), process.Log));
            if (alive && process.Component == Components.Server.Name)
            {
                try
                {
                    server = await Http.GetFromJsonAsync<JsonElement>(new Uri(ApiUri(process), "/api/v1/status"), cancellationToken);
                }
                catch (Exception e) when (e is HttpRequestException or JsonException
                    || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // It does not answer: the server line already says not ready.
                }
            }
        }

        return new InstanceReport(
            state.Instance,
            AlreadyRunning: false,
            state.Repository,
            SqlServerTarget.DataSource(state.SqlServer),
            state.Database,
            state.StartedAt,
            processes,
            server);
    }

    private static string FormatEvent(JsonElement e)
    {
        var occurred = e.GetProperty("occurredAt").GetDateTimeOffset().ToLocalTime();
        var data = e.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? " " + d.GetRawText() : string.Empty;
        return $"#{e.GetProperty("id").GetInt64()} {occurred:yyyy-MM-dd HH:mm:ss.fff} {e.GetProperty("type").GetString()} [{e.GetProperty("source").GetString()}]{data}";
    }

    private static List<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static List<string> Tail(string path, int count) =>
        File.Exists(path) ? ReadLines(path).TakeLast(count).ToList() : [];

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial System.Text.RegularExpressions.Regex LogName();
}

public enum StopOutcome
{
    /// <summary>Stopped gracefully after Ctrl+Break.</summary>
    Stopped,

    /// <summary>Did not stop within the timeout and was killed with its child processes.</summary>
    Killed,

    /// <summary>Was no longer running.</summary>
    AlreadyGone,
}

public sealed record StopReport(string Name, int Pid, StopOutcome Outcome);

public sealed record ProcessReport(string Name, string Component, int Pid, bool Alive, bool Ready, Uri Url, string Log);

/// <param name="Server">The server's own <c>/api/v1/status</c>, or null when it does not answer.</param>
public sealed record InstanceReport(
    string Instance,
    bool AlreadyRunning,
    string Repository,
    string SqlServer,
    string Database,
    DateTimeOffset StartedAt,
    IReadOnlyList<ProcessReport> Processes,
    JsonElement? Server)
{
    public bool AllReady => Processes.All(p => p.Ready);

    public IReadOnlyList<string> Lines() =>
    [
        $"Instance {Instance} from {Repository}, database {Database} on {SqlServer}",
        .. Processes.Select(p => $"  {p.Name,-12} {(p.Ready ? "ready" : p.Alive ? "not ready" : "gone"),-10} pid {p.Pid,-7} {p.Url}  log {p.Log}"),
    ];
}
