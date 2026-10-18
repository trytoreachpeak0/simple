namespace Simple.Cli.Remote;

/// <summary>
/// What one <c>simple remote</c> command runs on the remote machine, in the
/// instance's worktree there, and how.
/// </summary>
/// <param name="Checkout">Sends the working tree and checks it out first.</param>
/// <param name="Slot">Takes one of the machine's slots for heavy work while it runs.</param>
/// <param name="KeepsResults">Gets a run directory, whose contents (test results, scenario evidence) come back.</param>
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
    public static readonly IReadOnlyList<string> Commands = ["build", "test", "scenario", "exec", "up", "down", "status", "events", "logs", "forget"];

    /// <summary>What simple remote scenario passes on itself; the caller may not.</summary>
    public static readonly IReadOnlyList<string> ScenarioOwnOptions =
        ["--evidence", global::Simple.Cli.SimpleCommand.ServerKeyStoreOption, global::Simple.Cli.SimpleCommand.InstanceOption, "--keep-up"];

    /// <param name="runDirectory">Where the run's results go on the remote machine (build, test, scenario run, exec).</param>
    /// <param name="instance">The instance there; a scenario run uses the scenario instance named after it, as forget resets it.</param>
    public static RemotePlan For(string command, IReadOnlyList<string> arguments, string runDirectory, bool json, string instance = "")
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] jsonFlag = json ? [global::Simple.Cli.SimpleCommand.JsonOption] : [];
        return command switch
        {
            "build" => new("dotnet", ["build", "simple.slnx", "-nologo", .. arguments], Checkout: true, Slot: true, KeepsResults: true, RunsSimple: false),
            // --project (RemoteCommand) runs one project in place of the solution.
            "test" => new("dotnet", ["test", .. Target(arguments), .. ResultsArguments(arguments, runDirectory), .. arguments], true, true, true, false),
            // A scenario run builds and brings a system up, so it takes a slot
            // like up. Its server cannot use the user's key store over SSH,
            // and its evidence goes into the run directory to come back. Its
            // instance is named here, the same one forget resets.
            "scenario" when arguments is ["run", ..] => SimpleCli(
                [
                    "scenario", "run",
                    global::Simple.Cli.SimpleCommand.InstanceOption, ScenarioInstance(instance),
                    global::Simple.Cli.SimpleCommand.ServerKeyStoreOption, nameof(ServerKeyStore.Machine),
                    "--evidence", runDirectory,
                    .. arguments.Skip(1), .. jsonFlag,
                ],
                checkout: true,
                slot: true) with { KeepsResults = true },
            // Checked out, so a scenario written here and not committed is the one checked there.
            "scenario" when arguments is ["list" or "validate", ..] => SimpleCli(["scenario", .. arguments, .. jsonFlag], checkout: true, slot: false),
            "scenario" => throw new RemoteException(ExitCodes.Usage, "simple remote scenario needs run, list or validate: simple remote scenario run <scenario>."),
            "exec" when arguments.Count > 0 => new(arguments[0], arguments.Skip(1).ToList(), true, true, true, false),
            "exec" => throw new RemoteException(ExitCodes.Usage, "simple remote exec needs a command: simple remote exec -- <command> [arguments]."),
            // up builds, so it takes a slot like the other heavy commands; the
            // system it starts outlives the command and holds no slot.
            // Over SSH the components must outlive the session (--break-away),
            // and the server cannot use the user's key store (no DPAPI for a
            // key login).
            "up" => SimpleCli(
                ["up", global::Simple.Cli.SimpleCommand.BreakAwayOption, global::Simple.Cli.SimpleCommand.ServerKeyStoreOption, nameof(ServerKeyStore.Machine), .. arguments, .. jsonFlag],
                checkout: true,
                slot: true),
            "down" or "status" or "events" or "logs" => SimpleCli([command, .. arguments, .. jsonFlag], checkout: false, slot: false),
            "forget" => SimpleCli(["down", "--reset", .. arguments, .. jsonFlag], checkout: false, slot: false) with { Forget = true },
            _ => throw new ArgumentException($"Unknown remote command '{command}'.", nameof(command)),
        };
    }

    /// <summary>
    /// What simple remote forget runs first: scenario runs there use an
    /// instance of their own (<see cref="global::Simple.Cli.Scenarios.ScenarioRunner.InstanceFor"/>),
    /// whose database and server key would otherwise outlive the worktree.
    /// </summary>
    public static RemotePlan ForgetScenarios(string instance) =>
        SimpleCli(["down", "--reset", global::Simple.Cli.SimpleCommand.InstanceOption, ScenarioInstance(instance)], checkout: false, slot: false);

    private static string ScenarioInstance(string instance) => global::Simple.Cli.Scenarios.ScenarioRunner.InstanceFor(instance);

    /// <summary>The command line the remote side starts the program with.</summary>
    public string CommandLine => Processes.CommandLine.Join(Arguments);

    private static RemotePlan SimpleCli(IReadOnlyList<string> arguments, bool checkout, bool slot) =>
        new("pwsh", ["-NoProfile", "-NonInteractive", "-File", "simple.ps1", .. arguments], checkout, slot, KeepsResults: false, RunsSimple: true);

    private static string[] Target(IReadOnlyList<string> arguments) =>
        arguments.Contains(RemoteCommand.ProjectOption, StringComparer.OrdinalIgnoreCase) ? [] : ["--solution", "simple.slnx"];

    /// <summary>TRX results into the run directory, unless the caller chose where results go.</summary>
    private static string[] ResultsArguments(IReadOnlyList<string> arguments, string runDirectory) =>
        arguments.Contains("--results-directory", StringComparer.OrdinalIgnoreCase)
            ? []
            : ["--results-directory", $"{runDirectory}/TestResults", "--report-xunit-trx"];
}
