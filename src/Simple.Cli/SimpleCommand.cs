using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using Simple.Cli.Instances;

namespace Simple.Cli;

/// <summary>The <c>simple</c> command line: its commands, options and help text.</summary>
public static partial class SimpleCommand
{
    public const string InstanceOption = "--instance";
    public const string JsonOption = "--json";
    public const string BreakAwayOption = "--break-away";
    public const string ServerKeyStoreOption = "--server-key-store";

    /// <summary>
    /// The exit codes each command can end with, listed in its help. Every
    /// command can also end with 1 (unexpected error) and 2 (bad arguments, or
    /// SIMPLE_SQLSERVER refused). Checked by hand against LocalSystem: keep
    /// this table in step when a command gains a new outcome.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int[]> CommandExitCodes = new Dictionary<string, int[]>
    {
        ["up"] = [0, 1, 2, 3, 4, 6, 7, 9],
        ["down"] = [0, 1, 2, 6, 7, 8, 9],
        ["status"] = [0, 1, 2, 5, 6],
        ["events"] = [0, 1, 2, 5],
        ["logs"] = [0, 1, 2, 5],
        // Besides the exit code of the command it ran on the remote machine.
        ["remote"] = [1, 2, 5, 20, 21, 22],
        // run: the verdict, 1 when its system could not be stopped, 7 when
        // another run holds the instance; validate: 2 for a file that is not valid.
        ["scenario"] = [0, 1, 2, 7, 10, 11, 12],
    };

    /// <summary>Builds the command tree. The actions run against <paramref name="environment"/>.</summary>
    public static RootCommand Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var instance = new Option<string?>(InstanceOption)
        {
            Description = "The instance to act on. Default: the name of this clone's folder, so two worktrees never share an instance. "
                + "The database is Simple_<instance>.",
            Recursive = true,
        };
        instance.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } name && !SqlServerTarget.IsValidInstanceName(name))
            {
                result.AddError($"--instance '{name}' is not valid: use 1-50 letters, digits, '-' or '_'.");
            }
        });

        var json = new Option<bool>(JsonOption)
        {
            Description = "Write one JSON object {exitCode, error, result} to stdout instead of text.",
            Recursive = true,
        };

        var terminals = new Option<int>("--terminals")
        {
            Description = "How many onboard terminals to start.",
            DefaultValueFactory = _ => 0,
        };
        terminals.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.AddError("--terminals must be 0 or more.");
            }
        });

        var timeout = new Option<int>("--timeout")
        {
            Description = "Seconds to wait for every component to be ready.",
            DefaultValueFactory = _ => 120,
        };
        timeout.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
            {
                result.AddError("--timeout must be at least 1 second.");
            }
        });

        var reset = new Option<bool>("--reset")
        {
            Description = "Also drop this instance's database, Simple_<instance>, and delete its server key; first the same for <instance>-scenario, "
                + "the instance its scenario runs use, unless the instance is itself one. No other database is touched.",
        };

        var since = new Option<DateTimeOffset?>("--since")
        {
            Description = "Only events from this time on: a duration back from now (30s, 5m, 2h, 1d) or a timestamp (2026-10-17T09:00+08:00).",
            CustomParser = ParseSince,
        };

        var logName = new Argument<string>("name")
        {
            Description = "A process of the instance as status lists it (server), or build (with --output) for the last build's output.",
        };

        var tail = new Option<int?>("--tail")
        {
            Description = "Only the last N lines.",
        };
        tail.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
            {
                result.AddError("--tail must be at least 1.");
            }
        });

        var breakAway = new Option<bool>(BreakAwayOption)
        {
            Description = "Internal, for simple remote up: start the components outside this session's job, so they outlive the SSH session.",
            Hidden = true,
        };

        var keyStore = new Option<string>(ServerKeyStoreOption)
        {
            Description = "Internal, for simple remote up: where the server keeps its key, User (default) or Machine. "
                + "Machine is for an SSH key login, which cannot use the user's store; only an administrator can create or delete a key there.",
            Hidden = true,
            DefaultValueFactory = _ => nameof(ServerKeyStore.User),
        };
        keyStore.Validators.Add(result =>
        {
            if (!ServerKey.TryParse(result.GetValueOrDefault<string>(), out _))
            {
                result.AddError($"{ServerKeyStoreOption} '{result.GetValueOrDefault<string>()}' is neither User nor Machine.");
            }
        });

        var up = new Command(
            "up",
            "Build once, start every component of the local system from the build output, and return when all are ready. "
            + "Running it again while the instance is up starts nothing. " + ExitCodesOf("up"))
        {
            terminals,
            timeout,
            breakAway,
            keyStore,
        };
        up.SetAction((parse, cancellationToken) => RunAsync(
            parse,
            environment,
            (system, instance) => system.UpAsync(
                instance,
                environment.SqlServer,
                UpOptionsOf(parse),
                TimeSpan.FromSeconds(parse.GetValue(timeout)),
                cancellationToken),
            starts: true));

        var down = new Command(
            "down",
            "Stop the processes this instance started, by the process ids it recorded, gracefully (Ctrl+Break) so the server records server.stopped. "
            + ExitCodesOf("down"))
        {
            reset,
        };
        down.SetAction((parse, cancellationToken) => RunAsync(parse, environment, (system, instance) =>
            parse.GetValue(reset)
                ? system.ResetAsync(instance, environment.SqlServer, cancellationToken)
                : system.DownAsync(instance, environment.SqlServer, reset: false, cancellationToken)));

        var status = new Command(
            "status",
            "Show every process of the instance, whether it is running and ready, and the server's own status. " + ExitCodesOf("status"));
        status.SetAction((parse, cancellationToken) => RunAsync(parse, environment, (system, instance) =>
            system.StatusAsync(instance, cancellationToken)));

        var events = new Command("events", "Read the server's event record, oldest first. " + ExitCodesOf("events"))
        {
            since,
        };
        events.SetAction((parse, cancellationToken) => RunAsync(parse, environment, (system, instance) =>
            system.EventsAsync(instance, parse.GetValue(since), cancellationToken)));

        var output = new Option<bool>("--output")
        {
            Description = "What the process wrote to its console instead: startup refusals and crashes the runtime printed. Written anew at each up.",
        };

        var logs = new Command(
            "logs",
            "Print a process's program log, oldest first. The program writes it itself, credentials redacted, and keeps it size-limited from one up to the next. "
            + ExitCodesOf("logs"))
        {
            logName,
            tail,
            output,
        };
        logs.SetAction((parse, _) => RunAsync(parse, environment, (system, instance) =>
            Task.FromResult(system.Logs(instance, parse.GetValue(logName)!, parse.GetValue(tail), parse.GetValue(output)))));

        var signal = new Command(SignalStopCommand.Name, "Internal: sends Ctrl+Break to a process's console.") { Hidden = true };
        var pid = new Argument<int>("pid");
        signal.Add(pid);
        signal.SetAction(parse => SignalStopCommand.Run(parse.GetValue(pid)));

        return new RootCommand(
            "Bring a local SIMPLE system up and down, and look into it. Every component runs as a process of its own on loopback ports "
            + "chosen for the instance; the database is on LocalDB, or on the local SQL Server instance named in "
            + $"{SqlServerTarget.Variable}. State and logs are under {InstanceStore.HomeVariable} (default %LOCALAPPDATA%\\simple)."
            + Environment.NewLine + Environment.NewLine + ExitCodes.HelpText)
        {
            instance,
            json,
            up,
            down,
            status,
            events,
            logs,
            Remote.RemoteCommand.Create(environment),
            Scenarios.ScenarioCommand.Create(environment),
            signal,
        };
    }

    public static async Task<int> InvokeAsync(string[] args, CliEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var parse = Create(environment).Parse(args);
        if (parse.Errors.Count > 0)
        {
            return Outcome.Fail(
                    ExitCodes.Usage,
                    string.Join(Environment.NewLine, parse.Errors.Select(e => e.Message).Append("Run simple --help for usage.")))
                .Write(parse.GetValue<bool>(JsonOption), environment.Out, environment.Error);
        }

        var configuration = new InvocationConfiguration { Output = environment.Out, Error = environment.Error };
        if (Scenarios.ScenarioCommand.IsRun(parse))
        {
            // On Ctrl+C a scenario run still stops its system and writes its
            // result; the default would end the process two seconds later.
            configuration.ProcessTerminationTimeout = Scenarios.ScenarioRunner.TeardownLimit + TimeSpan.FromMinutes(1);
        }

        return await parse.InvokeAsync(configuration, cancellationToken);
    }

    /// <summary>The instance a command acts on: --instance, or the clone's folder name.</summary>
    public static string InstanceOf(ParseResult parse, string repository)
    {
        ArgumentNullException.ThrowIfNull(parse);
        return parse.GetValue<string?>(InstanceOption) ?? SqlServerTarget.InstanceNameFrom(Path.GetFileName(Path.TrimEndingDirectorySeparator(repository)));
    }

    /// <summary>
    /// Why the instance a command acts on may not be used, or null when it may
    /// (#88). Scenario runs use &lt;instance&gt;-scenario
    /// (<see cref="Scenarios.ScenarioRunner.InstanceFor"/>), so a clone's own
    /// instance never ends in -scenario: a clone in a folder named foo-scenario
    /// would share the database and server key of foo's scenario runs. A
    /// scenario instance may still be named with --instance to look into it
    /// or stop it, but not to start it as a clone's own (<paramref name="starts"/>).
    /// </summary>
    public static string? InstanceRefusal(ParseResult parse, string repository, bool starts)
    {
        ArgumentNullException.ThrowIfNull(parse);
        if (parse.GetValue<string?>(InstanceOption) is { } named)
        {
            return starts && Scenarios.ScenarioRunner.IsScenarioInstance(named)
                ? $"{InstanceOption} '{named}' ends in {Scenarios.ScenarioRunner.InstanceSuffix}, which only scenario runs use for their own instance (simple scenario run). "
                    + $"Choose a name that does not end in {Scenarios.ScenarioRunner.InstanceSuffix}."
                : null;
        }

        var own = InstanceOf(parse, repository);
        return Scenarios.ScenarioRunner.IsScenarioInstance(own)
            ? $"This clone's folder gives instance '{own}', which ends in {Scenarios.ScenarioRunner.InstanceSuffix}: scenario runs of a clone in a folder named "
                + $"'{own[..^Scenarios.ScenarioRunner.InstanceSuffix.Length]}' use that instance, and the two would share a database and a server key. "
                + $"Rename the folder, or pass {InstanceOption} with a name that does not end in {Scenarios.ScenarioRunner.InstanceSuffix}."
            : null;
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, Func<LocalSystem, string, Task<Outcome>> action, bool starts = false)
    {
        var json = parse.GetValue<bool>(JsonOption);
        Outcome outcome;
        if (SqlServerTarget.Refusal(environment.SqlServer) is { } refusal)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, refusal);
        }
        else if (InstanceRefusal(parse, environment.Repository, starts) is { } refused)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, refused);
        }
        else
        {
            try
            {
                var system = new LocalSystem(environment.Repository, new InstanceStore(environment.Home), environment.Error);
                outcome = await action(system, InstanceOf(parse, environment.Repository));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                outcome = Outcome.Fail(ExitCodes.Unexpected, e.ToString());
            }
        }

        return outcome.Write(json, environment.Out, environment.Error);
    }

    /// <summary>What a parsed <c>up</c> asks for.</summary>
    public static UpOptions UpOptionsOf(ParseResult parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var text = parse.GetValue<string>(ServerKeyStoreOption);
        return new UpOptions(
            parse.GetValue<int>("--terminals"),
            parse.GetValue<bool>(BreakAwayOption),
            ServerKey.TryParse(text, out var store) ? store : throw new ArgumentException($"'{text}' is neither User nor Machine.", nameof(parse)));
    }

    public static string ExitCodesOf(string command) => $"Exit codes {string.Join(", ", CommandExitCodes[command])}.";

    private static DateTimeOffset? ParseSince(ArgumentResult result)
    {
        var text = result.Tokens.Single().Value;
        var duration = Duration().Match(text);
        if (duration.Success)
        {
            var amount = int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture);
            var span = duration.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
            return DateTimeOffset.UtcNow - span;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
        {
            return timestamp;
        }

        result.AddError($"--since '{text}' is neither a duration (30s, 5m, 2h, 1d) nor a timestamp.");
        return null;
    }

    [GeneratedRegex("^([0-9]{1,6})([smhd])$")]
    private static partial Regex Duration();
}

/// <summary>Where the command line runs: which clone, where its files go, which SQL Server, where it writes.</summary>
public sealed record CliEnvironment(string Repository, string Home, string SqlServer, TextWriter Out, TextWriter Error)
{
    /// <summary>The SSH alias simple remote runs on, from SIMPLE_REMOTE.</summary>
    public string? RemoteAlias { get; init; }

    /// <summary>The machine simple remote runs on instead of <see cref="RemoteAlias"/>; tests set it.</summary>
    public Remote.IRemoteMachine? RemoteMachine { get; init; }

    public static CliEnvironment FromProcess() =>
        new(FindRepository(AppContext.BaseDirectory), InstanceStore.DefaultHome(), SqlServerTarget.Configured(), Console.Out, Console.Error)
        {
            RemoteAlias = Environment.GetEnvironmentVariable(Remote.SshMachine.Variable) is { Length: > 0 } alias ? alias : null,
        };

    /// <summary>The clone the command line was built from: the nearest folder above it that holds simple.slnx.</summary>
    public static string FindRepository(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "simple.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No simple.slnx above {start}: the command line runs from a build inside a SIMPLE clone.");
    }
}
