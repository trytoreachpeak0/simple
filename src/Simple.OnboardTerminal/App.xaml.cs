using System.Windows;
using Simple.Logging;

namespace Simple.OnboardTerminal;

/// <summary>The onboard terminal's WPF shell.</summary>
public partial class App : Application
{
    private ProgramLogProvider? log;

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        log = ShellLog.Start(e.Args, Dispatcher);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        log?.Dispose();
    }
}
