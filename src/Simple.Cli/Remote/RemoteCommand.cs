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
        ["exec"] = "Run any command there in the instance's worktree: simple remote exec -- <command> [arguments]. "
            + "Files it writes to %SIMPLE_REMOTE_RUN% come back.",
        ["up"] = "Bring the instance's system up there (simple up); it keeps running after this returns. Further arguments go to simple up.",
        ["down"] = "Bring the instance's system down there (simple down). Runs on what is checked out there; sends nothing.",
        ["status"] = "Show the instance's system there (simple status). Sends nothing.",
        ["events"] = "Read the server's event record there (simple events). Sends nothing.",
        ["logs"] = "Print a process's log there (simple logs <name>). Sends nothing.",
        ["forget"] = "Stop the instance there, drop its database and delete its worktree and runs (simple down --reset, then remove). "
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
            || !(Directory.Exists(full) || (File.Exists(full) && full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    public static Command Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var remote = new Command(
            "remote",
            $"Run a command on the remote machine named by {SshMachine.Variable} (an alias in your SSH configuration), against exactly this clone's "
            + "working tree: uncommitted changes and new files are sent as a snapshot commit, nothing is pushed. The instance there is the same "
            + "as here. Like simple itself it starts a local system only: on that machine, on loopback, with its own SQL Server instance. "
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
        string? projectRefusal = null;
        if (command == "test" && parse.GetValue<string?>(ProjectOption) is { } project)
        {
            if (ProjectPath(environment.Repository, project) is { } relative)
            {
                arguments.InsertRange(0, [ProjectOption, relative]);
            }
            else
            {
                projectRefusal = $"{ProjectOption} '{project}' is not a test project or a project file inside this clone ({environment.Repository}).";
            }
        }

        var machine = environment.RemoteMachine;
        if (machine is null && SshMachine.Refusal(environment.RemoteAlias) is { } refusal)
        {
            outcome = Outcome.Fail(
                ExitCodes.Usage,
                environment.RemoteAlias is null ? $"{SshMachine.Variable} is not set: set it to the alias of the remote machine in your SSH configuration (Host <alias> in ~/.ssh/config)." : refusal);
        }
        else if (projectRefusal is not null)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, projectRefusal);
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
