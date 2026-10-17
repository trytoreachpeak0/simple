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

    /// <summary>The table printed in the help text.</summary>
    public const string HelpText =
        """
        Exit codes:
          0  success
          1  unexpected error, or a process still running after down stopped it
          2  invalid arguments or settings
          3  the build failed (up)
          4  a component did not become ready (up); what this instance started is stopped again
          5  the instance is not running (status, events, logs: no such log)
          6  the instance is unhealthy: a component is gone or not ready (status), or the
             instance is partly running (up) or was started from another clone (up, down)
          7  another simple command is still working on this instance or building in this clone
          8  a component did not stop within the timeout and was killed (down)
          9  the SQL Server could not be reached (up, down --reset), or the database could
             not be dropped (down --reset)
        """;
}
