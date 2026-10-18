using System.CommandLine;

namespace Simple.Cli.Remote;

/// <summary>The <c>simple remote</c> command and its subcommands.</summary>
public static class RemoteCommand
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["build"] = "Build the solution there (dotnet build simple.slnx). Further arguments go to dotnet build.",
        ["test"] = "Run the tests there (dotnet test --solution simple.slnx). Further arguments are MTP options. "
            + "To run one class, also name its project, or every other project ends with 8: "
            + "simple remote test --project tests/Simple.Cli.Tests --filter-class \"*CommandLineTests\". "
            + "TRX results come back unless --results-directory is given.",
        ["scenario"] = "Run, list or validate scenarios there (simple scenario run|list|validate), against this clone's working tree: "
            + "simple remote scenario run infrastructure-up. A scenario given as a path must be a .yaml file inside the clone that .gitignore does not exclude. "
            + "run takes a slot, keeps its server's key in the machine's store and ends with the verdict: 0 holds, 10 does not hold, 11 cannot tell, 12 not applicable "
            + "(1 when its system could not be stopped, 2 when the file is not valid, 7 when another run holds the instance). "
            + "Its evidence comes back with the run's output. simple remote sets the scenario instance (<instance>-scenario), --evidence and --server-key-store itself and refuses them, and --keep-up, "
            + "since a system kept up there would end with the SSH session.",
        ["exec"] = "Run any command there in the instance's worktree: simple remote exec -- <command> [arguments]. "
            + "Files it writes to %SIMPLE_REMOTE_RUN% come back.",
        ["up"] = "Bring the instance's system up there (simple up); it keeps running after this returns. Further arguments go to simple up.",
        ["down"] = "Bring the instance's system down there (simple down). Runs on what is checked out there; sends nothing.",
        ["status"] = "Show the instance's system there (simple status). Sends nothing.",
        ["events"] = "Read the server's event record there (simple events). Sends nothing.",
        ["logs"] = "Print a process's log there (simple logs <name>). Sends nothing.",
        ["forget"] = "Stop the instance there, drop its database and delete its worktree and runs (simple down --reset, then remove); "
            + "the instance its scenario runs used is reset too, and the command line's own directories of both go. "
            + "Use it before deleting the worktree here.",
    };

    public const string ProjectOption = "--project";

    /// <summary>
    /// The project as the remote side gets it: relative to the clone, with
    /// forward slashes. Null when it is outside the clone or does not exist:
    /// the remote tree is this one, so what is not here is not there.
    /// </summary>
    public static string? ProjectPath(string repository, string project)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(project);
        if (project.Length == 0 || Path.IsPathRooted(project))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, project)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !((Directory.Exists(full) && Directory.EnumerateFiles(full, "*.csproj").Any())
                || (File.Exists(full) && full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    /// <summary>
    /// A scenario as the remote side gets it. A name (infrastructure-up)
    /// stays as it is; a path to a .yaml file, relative to the current
    /// directory as for simple scenario, becomes a path relative to the clone
    /// with forward slashes, since the command runs there at the worktree's
    /// root. Null when the file does not exist or is outside the clone.
    /// </summary>
    public static string? ScenarioPath(string repository, string reference)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var full = Path.GetFullPath(reference);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : null;
    }

    /// <summary>Whether a passed-through argument would pick what dotnet test runs.</summary>
    private static bool TargetsItself(string argument) => IsOption(argument, ProjectOption, "--solution");

    private static bool IsOption(string argument, params string[] options) =>
        options.Any(option =>
            argument.Equals(option, StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(option + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why simple remote scenario does not run these arguments; null when it does, with paths made relative to the clone.</summary>
    private static async Task<string?> ScenarioRefusalAsync(string repository, List<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments is not ["run" or "list" or "validate", ..])
        {
            return "simple remote scenario needs run, list or validate: simple remote scenario run <scenario>.";
        }

        if (arguments.FirstOrDefault(a => IsOption(a, [.. RemotePlan.ScenarioOwnOptions])) is { } own)
        {
            return $"'{own}' is not passed through: simple remote scenario sets the instance, where the evidence goes and the key store itself, "
                + "and a system kept up there would end with the SSH session. Give --instance before --: it is simple remote's own option.";
        }

        // Refused here, before anything is sent and a slot is waited for.
        if (arguments is ["run", ..] && (arguments.Count != 2 || arguments[1].StartsWith('-')))
        {
            return "simple remote scenario run takes exactly one scenario: simple remote scenario run <scenario>.";
        }

        if (arguments is ["list", _, ..])
        {
            return "simple remote scenario list takes no scenario: it lists them all.";
        }

        for (var i = 1; i < arguments.Count; i++)
        {
            if (ScenarioPath(repository, arguments[i]) is not { } path)
            {
                return $"'{arguments[i]}' is not a .yaml file inside this clone ({repository}): the remote machine gets this clone's working tree and nothing else.";
            }

            if (arguments[i].EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                // git check-ignore exits 0 when the path is ignored.
                if ((await Git.TryAsync(repository, ["check-ignore", "--quiet", "--", path], cancellationToken)).ExitCode == 0)
                {
                    return $"'{arguments[i]}' is excluded by .gitignore, so the snapshot the remote machine gets does not have it. Put the scenario under scenarios/.";
                }

                arguments[i] = path;
            }
        }

        return null;
    }

    public static Command Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var remote = new Command(
            "remote",
            $"Run a command on the remote machine named by {SshMachine.Variable} (an alias in your SSH configuration), against exactly this clone's "
            + "working tree: uncommitted changes and new files are sent as a snapshot commit, nothing is pushed. The instance there is the same "
            + "as here. Like simple itself it starts a local system only: on that machine, on loopback, with its own SQL Server instance. "
            + "Commands run there with CI=true, so restore is locked as on CI: a packages.lock.json that does not match its project fails with NU1004 "
            + "(CI's separate check that the lock files are up to date is not run there). "
            + "It ends with the exit code of the command it ran there, or: " + SimpleCommand.ExitCodesOf("remote"));
        foreach (var name in RemotePlan.Commands)
        {
            var command = new Command(name, $"{Descriptions[name]} Ends with that command's exit code, or: {SimpleCommand.ExitCodesOf("remote")}") { TreatUnmatchedTokensAsErrors = false };
            if (name == "test")
            {
                command.Add(new Option<string?>(ProjectOption)
                {
                    Description = "Run only this test project, a path inside the clone (tests/Simple.Cli.Tests). Use it to run one class: "
                        + "across the whole solution every other project finds no test and ends with 8, so the run does too.",
                });
            }

            command.SetAction((parse, cancellationToken) => RunAsync(parse, environment, name, cancellationToken));
            remote.Add(command);
        }

        return remote;
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, string command, CancellationToken cancellationToken)
    {
        var json = parse.GetValue<bool>(SimpleCommand.JsonOption);
        var arguments = parse.UnmatchedTokens.ToList();
        Outcome outcome;
        string? argumentRefusal = null;
        try
        {
            if (command == "scenario")
            {
                argumentRefusal = await ScenarioRefusalAsync(environment.Repository, arguments, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // As below: with --json, stdout stays one JSON object.
            return Outcome.Fail(ExitCodes.Unexpected, e.ToString()).Write(json, environment.Out, environment.Error);
        }

        if (command == "test" && arguments.FirstOrDefault(TargetsItself) is { } passed)
        {
            // Passed through after --, it would get past the check below.
            argumentRefusal = $"'{passed}' is not passed through: the run picks its target itself; name a project with {ProjectOption} <path> before any --.";
        }
        else if (command == "test" && parse.GetValue<string?>(ProjectOption) is { } project)
        {
            if (ProjectPath(environment.Repository, project) is { } relative)
            {
                arguments.InsertRange(0, [ProjectOption, relative]);
            }
            else
            {
                argumentRefusal = $"{ProjectOption} '{project}' is not a test project or a project file inside this clone ({environment.Repository}).";
            }
        }

        var machine = environment.RemoteMachine;
        if (machine is null && SshMachine.Refusal(environment.RemoteAlias) is { } refusal)
        {
            outcome = Outcome.Fail(
                ExitCodes.Usage,
                environment.RemoteAlias is null ? $"{SshMachine.Variable} is not set: set it to the alias of the remote machine in your SSH configuration (Host <alias> in ~/.ssh/config)." : refusal);
        }
        else if (argumentRefusal is not null)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, argumentRefusal);
        }
        else if (SimpleCommand.InstanceRefusal(parse, environment.Repository, starts: true) is { } refused)
        {
            // The instance there is a clone's own: its worktree, its simple up, its scenario runs' instance.
            outcome = Outcome.Fail(ExitCodes.Usage, refused);
        }
        else if (command == "exec" && arguments.Count == 0)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, "simple remote exec needs a command: simple remote exec -- <command> [arguments].");
        }
        else
        {
            machine ??= new SshMachine(environment.RemoteAlias!);
            var instance = SimpleCommand.InstanceOf(parse, environment.Repository);
            try
            {
                outcome = await new RemoteRun(environment.Repository, instance, machine, environment.Out, environment.Error)
                    .RunAsync(command, arguments, json, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // As for every simple command: with --json, stdout stays one JSON object.
                outcome = Outcome.Fail(ExitCodes.Unexpected, e.ToString());
            }
        }

        return outcome.Write(json, environment.Out, environment.Error);
    }
}
