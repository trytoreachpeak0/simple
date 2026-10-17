using Simple.Cli.Processes;

namespace Simple.Cli;

/// <summary>
/// The hidden command <c>simple signal-stop &lt;pid&gt;</c>, run by
/// <see cref="ChildProcess.RequestStopAsync"/> as a helper process.
/// Exit code 0 when the signal was sent.
/// </summary>
public static class SignalStopCommand
{
    public const string Name = "signal-stop";

    public static int Run(int pid) => ChildProcess.SendCtrlBreak(pid);
}
