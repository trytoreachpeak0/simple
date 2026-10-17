using System.CommandLine;

namespace Simple.Cli.Remote;

/// <summary>The <c>simple remote</c> command and its subcommands.</summary>
public static class RemoteCommand
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["build"] = "Build the solution there (dotnet build simple.slnx). Further arguments go to dotnet build.",
        ["test"] = "Run the tests there (dotnet test --solution simple.slnx). Further arguments are MTP options, for example --filter-class \"*CliTests\". "
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

    public static Command Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var remote = new Command(
            "remote",
            $"Run a command on the remote machine named by {SshMachine.Variable} (an alias in your SSH configuration), against exactly this clone's "
            + "working tree: uncommitted changes and new files are sent as a snapshot commit, nothing is pushed. The instance there is the same "
            + "as here. Like simple itself it starts a local system only: on that machine, on loopback, with its own SQL Server instance. "
            + "Exit codes: the remote command's own, or 2, 20, 21, 22 (see simple --help).");
        foreach (var name in RemotePlan.Commands)
        {
            var command = new Command(name, Descriptions[name]) { TreatUnmatchedTokensAsErrors = false };
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
        var machine = environment.RemoteMachine;
        if (machine is null && SshMachine.Refusal(environment.RemoteAlias) is { } refusal)
        {
            outcome = Outcome.Fail(
                ExitCodes.Usage,
                environment.RemoteAlias is null ? $"{SshMachine.Variable} is not set: set it to the SSH alias of the remote machine, for example simple01." : refusal);
        }
        else if (command == "exec" && arguments.Count == 0)
        {
            outcome = Outcome.Fail(ExitCodes.Usage, "simple remote exec needs a command: simple remote exec -- <command> [arguments].");
        }
        else
        {
            machine ??= new SshMachine(environment.RemoteAlias!);
            var instance = SimpleCommand.InstanceOf(parse, environment.Repository);
            outcome = await new RemoteRun(environment.Repository, instance, machine, environment.Out, environment.Error)
                .RunAsync(command, arguments, json, cancellationToken);
        }

        return outcome.Write(json, environment.Out, environment.Error);
    }
}
