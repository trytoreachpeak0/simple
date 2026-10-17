namespace Simple.Cli;

/// <summary>
/// The exit codes of every simple command. They are fixed: CI and AI sessions
/// branch on them, so a code never changes its meaning.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int Usage = 2;
    public const int BuildFailed = 3;
    public const int NotReady = 4;
    public const int NotRunning = 5;
    public const int Unhealthy = 6;
    public const int Busy = 7;
    public const int StopForced = 8;
    public const int DatabaseFailed = 9;

    // simple scenario run: a verdict other than holds (0). Never reused.
    public const int DoesNotHold = 10;
    public const int CannotTell = 11;
    public const int NotApplicable = 12;

    // simple remote ends with the remote command's own exit code. Its own
    // failures use codes no simple command and no test run (MTP: 0-13) uses.
    public const int RemoteFailed = 20;
    public const int RemoteRefused = 21;
    public const int RemoteBusy = 22;

    /// <summary>The table printed in the help text.</summary>
    public const string HelpText =
        """
        Exit codes:
          0  success
          1  unexpected error, a process still running after down stopped it, or a file
             the instance ran from still held after its processes stopped (up, down)
          2  invalid arguments or settings
          3  the build failed (up)
          4  a component did not become ready (up); what this instance started is stopped again
          5  the instance is not running (status, events, logs: no such log, or no program log yet)
          6  the instance is unhealthy: a component is gone or not ready (status), or the
             instance is partly running (up) or was started from another clone (up, down)
          7  another simple command is still working on this instance or building in this clone
          8  a component did not stop within the timeout and was killed (down)
          9  the SQL Server could not be reached (up, down --reset), or the database or the
             server key could not be deleted (down --reset)

        simple scenario run ends with the scenario's verdict: 0 holds, or one of
          10  does not hold: an expectation was observed to fail
          11  cannot tell: an expectation could not be observed, or the scenario could not
              be driven as written (the system did not come up, an event failed or its
              condition never came). Never a pass.
          12  not applicable: no expectation applied (their only-if did not hold)
        A scenario file that is not valid ends simple scenario run and validate with 2.

        simple remote ends with the exit code of the command it ran on the remote machine
        (a test run's own codes included), or with one of its own:
          20  the command did not run: git, ssh or scp failed, or the remote side failed
              before starting it
          21  the snapshot was not sent: it adds large files (listed)
          22  the instance or a slot on the remote machine stayed busy too long
        """;
}
