using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Simple.Logging;

namespace Simple.IoSimulator;

/// <summary>
/// The IO simulator. Its program log (ADR 0034) is <c>IoSimulator.log</c>
/// under <c>--Simple:Logs:Directory</c> (default
/// <c>%ProgramData%\Simple\IoSimulator\logs</c>), with every exception the UI
/// thread does not catch recorded and left unhandled.
/// </summary>
public partial class App : Application
{
    private const string Program = "IoSimulator";

    private ProgramLogProvider? log;

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        ProgramLogSettings settings;
        try
        {
            settings = ProgramLog.ReadSettings(new ConfigurationBuilder().AddCommandLine(e.Args).Build(), Program);
        }
        catch (InvalidDataException problem)
        {
            MessageBox.Show($"The IO simulator refuses to start: {problem.Message}", "SIMPLE IO simulator", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        log = ProgramLog.CreateProvider(Program, settings, console: false);
        DispatcherUnhandledException += (_, unhandled) => log.Unhandled.RecordUnhandled(unhandled.Exception, "the UI thread");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        log?.Dispose();
    }
}
