using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Simple.Cli.Remote;

/// <summary>
/// <c>simple remote</c>: runs one command on the remote machine against
/// exactly this clone's working tree. The tree is taken as a snapshot commit
/// (<see cref="Snapshot"/>), sent as a git bundle of what the remote cache
/// lacks, checked out in the instance's worktree there and run; output comes
/// back as it is printed, the exit code as it is, test results into
/// <c>artifacts/remote/&lt;instance&gt;/</c>.
/// </summary>
/// <remarks>
/// Hand-written glue over git, ssh and scp. Ready-made sync tools
/// (Mutagen, Syncthing) synchronise continuously and cannot say which state
/// of the tree a run saw; a commit can.
/// </remarks>
public sealed class RemoteRun(string repository, string instance, IRemoteMachine machine, TextWriter stdout, TextWriter stderr)
{
    /// <summary>A new file larger than this is not sent.</summary>
    public const long LargeFileLimit = 10L * 1024 * 1024;

    /// <summary>How many runs of an instance the remote machine keeps.</summary>
    public const int KeepRuns = 20;

    /// <summary>
    /// How long the remote machine keeps an agent no run has started: every
    /// version of the command line installs its own (<see cref="AgentFile"/>).
    /// A clone still on a removed version copies it again.
    /// </summary>
    public static readonly TimeSpan KeepAgents = TimeSpan.FromDays(7);

    private const string BaseRef = "refs/remotes/origin/main";

    /// <summary>
    /// The line the remote side prints when the agent is missing there, or
    /// its content does not match the hash in its name. A line, not an exit
    /// code: the remote machine's shell does not pass exit codes on
    /// (<see cref="SshMachine.Command"/>).
    /// </summary>
    public const string AgentMissingLine = "##simple-remote-agent-missing";

    private static readonly Lazy<byte[]> Agent = new(() =>
    {
        using var stream = typeof(RemoteRun).Assembly.GetManifestResourceStream("Simple.Cli.Remote.agent.ps1")
            ?? throw new InvalidOperationException("The remote agent script is not embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    private readonly Lock writing = new();

    /// <summary>Where the remote side fetches what a bundle builds on; empty when this clone has no origin.</summary>
    private string originUrl = string.Empty;

    /// <summary>How long to wait for the instance or a slot on the remote machine.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the remote side goes on without a heartbeat from here before
    /// it ends the run as abandoned: the network is gone. A killed ssh or
    /// Ctrl+C ends the session's input, which it notices at once.
    /// </summary>
    public TimeSpan Alive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The agent's file name on the remote machine: a changed script never meets an old copy.</summary>
    public static string AgentFile => $"agent-{Convert.ToHexStringLower(SHA256.HashData(Agent.Value))[..12]}.ps1";

    /// <summary>Where the results of this instance's runs come back to.</summary>
    public string LocalResults => Path.Combine(repository, "artifacts", "remote", instance);

    public async Task<Outcome> RunAsync(string command, IReadOnlyList<string> arguments, bool json, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(command, arguments, json, cancellationToken);
        }
        catch (RemoteException e)
        {
            return Outcome.Fail(e.ExitCode, e.Message);
        }
    }

    private async Task<Outcome> RunCoreAsync(string command, IReadOnlyList<string> arguments, bool json, CancellationToken cancellationToken)
    {
        originUrl = (await Git.TryAsync(repository, ["remote", "get-url", "origin"], cancellationToken)) is { ExitCode: 0 } origin
            ? WithoutCredentials(origin.Output.Trim())
            : string.Empty;
        var checkout = RemotePlan.For(command, arguments, string.Empty, json).Checkout;
        Snapshot? snapshot = null;
        string? baseRef = null;
        string? run = null;
        if (checkout)
        {
            snapshot = await Snapshot.TakeAsync(repository, cancellationToken);
            baseRef = (await Git.TryAsync(repository, ["rev-parse", "--verify", "--quiet", BaseRef], cancellationToken)).ExitCode == 0 ? BaseRef : null;
            var large = await Snapshot.LargeFilesAsync(repository, snapshot.Commit, baseRef, LargeFileLimit, cancellationToken);
            if (large.Count > 0)
            {
                return Outcome.Fail(
                    ExitCodes.RemoteRefused,
                    $"Not sent: the snapshot adds files over {LargeFileLimit / 1024 / 1024} MiB. Delete them or add them to .gitignore:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, large.Select(f => $"  {f.Path} ({f.Size / 1024 / 1024} MiB)")),
                    new { Files = large.Select(f => new { f.Path, f.Size }) });
            }

            run = $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{snapshot.Commit[..8]}";
        }

        var plan = RemotePlan.For(command, arguments, $"{machine.Root}/runs/{instance}/{run}", json);
        if (!plan.KeepsResults)
        {
            run = null;
        }

        var where = Where(snapshot);
        Progress($"{command} on {where}");

        string? bundle = null;
        if (snapshot is not null)
        {
            var probe = await AgentAsync(Request("probe", plan, snapshot, null, null), PassOn, PassOn, cancellationToken);
            if (!probe.GetProperty("hasCommit").GetBoolean())
            {
                bundle = await SendBundleAsync(snapshot, baseRef, cancellationToken);
            }
        }

        StreamWriter? log = null;
        if (run is not null)
        {
            Directory.CreateDirectory(Path.Combine(LocalResults, run));
            log = new StreamWriter(Path.Combine(LocalResults, run, "output.log"), append: false, new UTF8Encoding(false));
        }

        var collected = json && plan.RunsSimple ? new StringBuilder() : null;
        JsonElement result;
        try
        {
            void Output(string line)
            {
                lock (writing)
                {
                    log?.WriteLine(line);
                    if (collected is not null)
                    {
                        collected.AppendLine(line);
                    }
                    else
                    {
                        (json ? stderr : stdout).WriteLine(line);
                    }
                }
            }

            void Error(string line)
            {
                lock (writing)
                {
                    log?.WriteLine(line);
                    stderr.WriteLine(line);
                }
            }

            result = await AgentAsync(Request(plan.Forget ? "forget" : "run", plan, snapshot, bundle, run), Output, Error, cancellationToken);
            if (Status(result) == "needFullBundle" && snapshot is not null)
            {
                Progress("the remote cache lacks what the bundle builds on; sending the full history");
                bundle = await SendBundleAsync(snapshot, null, cancellationToken);
                result = await AgentAsync(Request(plan.Forget ? "forget" : "run", plan, snapshot, bundle, run), Output, Error, cancellationToken);
            }
        }
        finally
        {
            if (log is not null)
            {
                await log.DisposeAsync();
            }
        }

        switch (Status(result))
        {
            case "ran":
                break;
            case "busy":
                return Outcome.Fail(ExitCodes.RemoteBusy, $"simple remote: {result.GetProperty("error").GetString()}");
            case "noWorktree":
                return Outcome.Fail(ExitCodes.NotRunning, $"Instance {instance} has never been set up on {machine.Name}. Run simple remote up (or build, test) first.");
            case "failed":
                return Outcome.Fail(ExitCodes.RemoteFailed, $"simple remote: {command} did not run on {machine.Name}: {result.GetProperty("error").GetString()}");
            default:
                return Outcome.Fail(ExitCodes.RemoteFailed, $"simple remote: {command} did not run on {machine.Name}: {result.GetRawText()}");
        }

        var exitCode = result.GetProperty("exitCode").GetInt32();
        string? results = null;
        if (run is not null)
        {
            results = Path.Combine(LocalResults, run);
            if (result.GetProperty("hasResults").GetBoolean())
            {
                await machine.ReceiveAsync($"runs/{instance}/{run}", LocalResults, cancellationToken);
            }
        }

        if (plan.Forget && exitCode == 0)
        {
            await Git.TryAsync(repository, ["update-ref", "-d", Snapshot.Ref(instance)], cancellationToken);
        }

        var report = new RemoteReport(
            machine.Name,
            instance,
            command,
            arguments,
            snapshot?.Commit,
            snapshot?.Head,
            snapshot?.Uncommitted,
            exitCode,
            results,
            collected is null ? null : Parse(collected.ToString()));
        var footer = $"simple remote: {command} exited with {exitCode} on {where}" + (results is null ? string.Empty : $"; output and results in {results}");
        if (exitCode == 0)
        {
            Progress(footer[("simple remote: ".Length)..]);
            return Outcome.Ok(report);
        }

        return Outcome.Fail(exitCode, footer, report);
    }

    private string Where(Snapshot? snapshot) =>
        $"{machine.Name}, instance {instance}"
        + (snapshot is null
            ? string.Empty
            : snapshot.Uncommitted == 0
                ? $", commit {snapshot.Commit[..12]} (HEAD, nothing uncommitted)"
                : $", commit {snapshot.Commit[..12]} (HEAD {snapshot.Head[..12]} + {snapshot.Uncommitted} uncommitted files)");

    private void Progress(string line)
    {
        lock (writing)
        {
            stderr.WriteLine($"simple remote: {line}");
        }
    }

    /// <summary>
    /// The origin as the remote side gets it: without a user name or password
    /// in the URL, which would otherwise travel in the request, show in the
    /// remote command line and be printed into the run's output. The remote
    /// side only fetches; the repository it fetches is public.
    /// </summary>
    public static string WithoutCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : url;

    /// <summary>A line the remote side printed outside the command, already marked as its own.</summary>
    private void PassOn(string line)
    {
        lock (writing)
        {
            stderr.WriteLine(line);
        }
    }

    private static string? Status(JsonElement result) =>
        result.TryGetProperty("status", out var status) ? status.GetString() : null;

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    private object Request(string operation, RemotePlan plan, Snapshot? snapshot, string? bundle, string? run) => new
    {
        Nonce = Guid.NewGuid().ToString("N"),
        Operation = operation,
        Instance = instance,
        Label = $"{plan.FileName} {string.Join(' ', plan.Arguments.Take(3))} from {Environment.MachineName}",
        Commit = snapshot?.Commit,
        Ref = Snapshot.Ref(instance),
        Bundle = bundle,
        OriginUrl = originUrl,
        OriginBranch = "main",
        OriginRef = Snapshot.OriginRef(instance),
        plan.FileName,
        Arguments = plan.CommandLine,
        Slot = plan.Slot,
        machine.Slots,
        Environment = RunEnvironment(machine),
        Run = run,
        WaitSeconds = (int)Wait.TotalSeconds,
        AliveSeconds = Alive.TotalSeconds,
        KeepRuns,
        KeepAgentDays = KeepAgents.TotalDays,
        machine.Sweep,
    };

    /// <summary>Set for every command run on <paramref name="machine"/>, on top of the machine's own.</summary>
    internal static Dictionary<string, string> RunEnvironment(IRemoteMachine machine)
    {
        var environment = new Dictionary<string, string>
        {
            // Restore as on CI (Directory.Build.props): a stale
            // packages.lock.json fails there with NU1004, not first on CI (#80).
            ["CI"] = "true",
            // The remote machine may run another language; output is judged
            // by people and sessions reading English.
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["DOTNET_NOLOGO"] = "true",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true",
            // No MSBuild node outlives the run holding its output open.
            ["MSBUILDDISABLENODEREUSE"] = "1",
            // The tests' servers keep their keys in the machine's store: over
            // an SSH key login the user's store cannot be used (no DPAPI).
            // Read by the TestKeyStore of each test project.
            ["SIMPLE_TEST_SERVER_KEY_STORE"] = "Machine",
        };
        foreach (var (name, value) in machine.Environment)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// Bundles the snapshot and what it builds on that <paramref name="baseRef"/>
    /// does not have (everything when null) and sends it. Null when there is
    /// nothing to send: the snapshot is already on the origin, which the
    /// remote side fetches.
    /// </summary>
    private async Task<string?> SendBundleAsync(Snapshot snapshot, string? baseRef, CancellationToken cancellationToken)
    {
        var name = Snapshot.Ref(instance);
        await Git.RunAsync(repository, ["update-ref", name, snapshot.Commit], cancellationToken);
        if (baseRef is not null
            && (await Git.RunAsync(repository, ["rev-list", "--count", snapshot.Commit, "--not", baseRef], cancellationToken)).Trim() == "0")
        {
            return null;
        }

        var directory = Directory.CreateTempSubdirectory("simple-remote-").FullName;
        try
        {
            // Unique per run: two runs of one instance may send the same snapshot.
            var file = Path.Combine(directory, $"{instance}-{snapshot.Commit[..12]}-{Guid.NewGuid().ToString("N")[..8]}.bundle");
            await Git.RunAsync(repository, ["bundle", "create", "--quiet", file, name, .. baseRef is null ? Array.Empty<string>() : ["^" + baseRef]], cancellationToken);
            Progress($"sending {new FileInfo(file).Length / 1024} KiB" + (baseRef is null ? " (full history)" : string.Empty));
            await machine.SendAsync(file, "incoming", cancellationToken);
            return $"incoming/{Path.GetFileName(file)}";
        }
        finally
        {
            Scratch.Delete(directory);
        }
    }

    /// <summary>
    /// Runs the agent on the remote machine with one request and returns its
    /// result line; installs the agent first when that machine lacks this
    /// version of it.
    /// </summary>
    private async Task<JsonElement> AgentAsync(object request, Action<string> output, Action<string> error, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(request, Outcome.JsonOptions);
        var marker = $"##simple-remote-{json.GetProperty("nonce").GetString()} ";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.GetRawText()));
        var agent = Quote($"{machine.Root}/bin/{AgentFile}");
        var script = $$"""
            $agent = {{agent}}
            if (-not (Test-Path -LiteralPath $agent)) { [Console]::Error.WriteLine('{{AgentMissingLine}}'); exit 1 }
            & $agent -Request '{{encoded}}'
            exit $LASTEXITCODE
            """;
        for (var attempt = 0; ; attempt++)
        {
            JsonElement? result = null;
            var missing = false;
            var exitCode = await machine.RunAsync(
                script,
                output,
                line =>
                {
                    // The command's last output may not end its line, and then
                    // the result follows it on the same line.
                    var at = line.IndexOf(marker, StringComparison.Ordinal);
                    if (line == AgentMissingLine)
                    {
                        missing = true;
                    }
                    else if (at >= 0)
                    {
                        if (at > 0)
                        {
                            error(line[..at]);
                        }

                        using var document = JsonDocument.Parse(line[(at + marker.Length)..]);
                        result = document.RootElement.Clone();
                    }
                    else
                    {
                        error(line);
                    }
                },
                cancellationToken);
            if (result is { } done)
            {
                return done;
            }

            if (missing && attempt == 0)
            {
                await InstallAgentAsync(cancellationToken);
                continue;
            }

            throw new RemoteException(
                ExitCodes.RemoteFailed,
                missing
                    ? $"{machine.Root}/bin/{AgentFile} on {machine.Name} is missing or does not match its name, even after copying it again."
                    : $"The remote side on {machine.Name} ended with exit code {exitCode} without reporting what happened.");
        }
    }

    private async Task InstallAgentAsync(CancellationToken cancellationToken)
    {
        Progress($"installing {AgentFile} on {machine.Name}");
        var errors = new List<string>();
        var exitCode = await machine.RunAsync(
            $"New-Item -ItemType Directory -Force -Path {Quote($"{machine.Root}/bin")}, {Quote($"{machine.Root}/incoming")} | Out-Null",
            _ => { },
            errors.Add,
            cancellationToken);
        if (exitCode != 0)
        {
            throw new RemoteException(ExitCodes.RemoteFailed, $"Could not create {machine.Root} on {machine.Name}: {string.Join(' ', errors)}");
        }

        var directory = Directory.CreateTempSubdirectory("simple-remote-").FullName;
        try
        {
            var file = Path.Combine(directory, AgentFile);
            await File.WriteAllBytesAsync(file, Agent.Value, cancellationToken);
            await machine.SendAsync(file, "bin", cancellationToken);
        }
        finally
        {
            Scratch.Delete(directory);
        }
    }

    /// <summary>A PowerShell single-quoted string.</summary>
    private static string Quote(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
}

/// <summary>What <c>simple remote --json</c> reports besides the exit code.</summary>
/// <param name="Commit">The snapshot that ran; null for commands that run on what is there (down, status, events, logs, forget).</param>
/// <param name="ExitCode">The remote command's own exit code.</param>
/// <param name="Results">The local directory with the run's output and results.</param>
/// <param name="Remote">The remote simple command's own JSON (up, down, status, events, logs, forget).</param>
public sealed record RemoteReport(
    string Machine,
    string Instance,
    string Command,
    IReadOnlyList<string> Arguments,
    string? Commit,
    string? Head,
    int? Uncommitted,
    int ExitCode,
    string? Results,
    JsonElement? Remote);
