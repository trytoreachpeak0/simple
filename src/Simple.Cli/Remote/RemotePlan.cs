namespace Simple.Cli.Remote;

/// <summary>
/// What one <c>simple remote</c> command runs on the remote machine, in the
/// instance's worktree there, and how.
/// </summary>
/// <param name="Checkout">Sends the working tree and checks it out first.</param>
/// <param name="Slot">Takes one of the machine's slots for heavy work while it runs.</param>
/// <param name="KeepsResults">Gets a run directory, whose contents (test results) come back.</param>
/// <param name="RunsSimple">Runs the remote <c>simple</c>; with --json its JSON is kept in the result.</param>
/// <param name="Forget">Removes the instance from the remote machine once the command has succeeded.</param>
public sealed record RemotePlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Checkout,
    bool Slot,
    bool KeepsResults,
    bool RunsSimple,
    bool Forget = false)
{
    public static readonly IReadOnlyList<string> Commands = ["build", "test", "exec", "up", "down", "status", "events", "logs", "forget"];

    /// <param name="runDirectory">Where the run's results go on the remote machine (build, test, exec).</param>
    public static RemotePlan For(string command, IReadOnlyList<string> arguments, string runDirectory, bool json)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] jsonFlag = json ? [global::Simple.Cli.SimpleCommand.JsonOption] : [];
        return command switch
        {
            "build" => new("dotnet", ["build", "simple.slnx", "-nologo", .. arguments], Checkout: true, Slot: true, KeepsResults: true, RunsSimple: false),
            "test" => new("dotnet", ["test", "--solution", "simple.slnx", .. ResultsArguments(arguments, runDirectory), .. arguments], true, true, true, false),
            "exec" when arguments.Count > 0 => new(arguments[0], arguments.Skip(1).ToList(), true, true, true, false),
            "exec" => throw new RemoteException(ExitCodes.Usage, "simple remote exec needs a command: simple remote exec -- <command> [arguments]."),
            // up builds, so it takes a slot like the other heavy commands; the
            // system it starts outlives the command and holds no slot.
            "up" => SimpleCli(["up", .. arguments, .. jsonFlag], checkout: true, slot: true),
            "down" or "status" or "events" or "logs" => SimpleCli([command, .. arguments, .. jsonFlag], checkout: false, slot: false),
            "forget" => SimpleCli(["down", "--reset", .. arguments, .. jsonFlag], checkout: false, slot: false) with { Forget = true },
            _ => throw new ArgumentException($"Unknown remote command '{command}'.", nameof(command)),
        };
    }

    /// <summary>The command line the remote side starts the program with.</summary>
    public string CommandLine => Processes.CommandLine.Join(Arguments);

    private static RemotePlan SimpleCli(IReadOnlyList<string> arguments, bool checkout, bool slot) =>
        new("pwsh", ["-NoProfile", "-NonInteractive", "-File", "simple.ps1", .. arguments], checkout, slot, KeepsResults: false, RunsSimple: true);

    /// <summary>TRX results into the run directory, unless the caller chose where results go.</summary>
    private static string[] ResultsArguments(IReadOnlyList<string> arguments, string runDirectory) =>
        arguments.Contains("--results-directory", StringComparer.OrdinalIgnoreCase)
            ? []
            : ["--results-directory", $"{runDirectory}/TestResults", "--report-xunit-trx"];
}
