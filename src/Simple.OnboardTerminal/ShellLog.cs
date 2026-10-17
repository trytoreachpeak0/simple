using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Simple.Logging;

namespace Simple.OnboardTerminal;

/// <summary>
/// The shell's program log (ADR 0034): <c>OnboardTerminal.log</c> under
/// <c>--Simple:Logs:Directory</c> (default
/// <c>%ProgramData%\Simple\OnboardTerminal\logs</c>), read from the command
/// line like the rest of the terminal's settings.
/// </summary>
internal static partial class ShellLog
{
    public const string Program = "OnboardTerminal";

    /// <summary>
    /// Starts the log and records every exception <paramref name="dispatcher"/>'s
    /// thread does not catch. The exception stays unhandled, so the shell ends
    /// as it did without the log; the process-wide ones are recorded by the
    /// provider itself (<see cref="UnhandledExceptionLog"/>). A limit that is
    /// not valid does not keep the shell from starting: the screen matters more
    /// on the vehicle than a log limit, so the defaults apply and the log says so.
    /// </summary>
    public static ProgramLogProvider Start(string[] args, Dispatcher dispatcher)
    {
        var arguments = new ConfigurationBuilder().AddCommandLine(args).Build();
        ProgramLogSettings settings;
        string? ignored = null;
        try
        {
            settings = ProgramLog.ReadSettings(arguments, Program);
        }
        catch (InvalidDataException e)
        {
            var directoryOnly = new ConfigurationBuilder()
                .AddInMemoryCollection([new($"{ProgramLog.Section}:Directory", arguments[$"{ProgramLog.Section}:Directory"])])
                .Build();
            settings = ProgramLog.ReadSettings(directoryOnly, Program);
            ignored = e.Message;
        }

        var log = ProgramLog.CreateProvider(Program, settings, console: false);
        if (ignored is not null)
        {
            DefaultLimits(log.CreateLogger(typeof(ShellLog).FullName!), ignored);
        }

        dispatcher.UnhandledException += (_, e) => log.Unhandled.RecordUnhandled(e.Exception, "the UI thread");
        return log;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Logging with the default limits: {Problem}")]
    private static partial void DefaultLimits(ILogger logger, string problem);
}
