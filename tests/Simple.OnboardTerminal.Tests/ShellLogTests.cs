using System.IO;
using System.Windows.Threading;
using Simple.Logging;

namespace Simple.OnboardTerminal.Tests;

/// <summary>
/// The WPF shell's program log (ADR 0034): an exception nothing on the UI
/// thread caught is recorded, and the shell then goes on as before.
/// </summary>
public sealed class ShellLogTests
{
    [Fact]
    public void AnInvalidLimitDoesNotKeepTheShellFromStartingAndIsNamedInTheLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "simple-shell-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using (ShellLog.Start([$"--Simple:Logs:Directory={directory}", "--Simple:Logs:FileSizeLimitBytes=ten"], dispatcher))
            {
            }

            Assert.Single(ProgramLog.Files(directory));
            var text = ProgramLog.ReadText(directory);
            Assert.Contains(
                "[WRN] Simple.OnboardTerminal.ShellLog: Logging with the default limits: Simple:Logs:FileSizeLimitBytes 'ten' is not a whole number above 0.",
                text,
                StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file just closed can still be held for a moment
                // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
            }
        }
    }

    [Fact]
    public void AnExceptionTheUiThreadDidNotCatchIsRecordedAndLeftUnhandled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "simple-shell-tests", Guid.NewGuid().ToString("N"));
        bool? handledBeforeUs = null;
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var log = ShellLog.Start([$"--Simple:Logs:Directory={directory}"], dispatcher);
                // Stands in for what follows the shell's own handler: here the
                // test keeps the thread alive instead of letting the process end.
                dispatcher.UnhandledException += (_, e) =>
                {
                    handledBeforeUs = e.Handled;
                    e.Handled = true;
                    dispatcher.InvokeShutdown();
                };
                dispatcher.BeginInvoke(() => throw new InvalidOperationException("Door-button-crash"));
                Dispatcher.Run();
            }
            catch (Exception e) when (e is not ThreadInterruptedException)
            {
                failure = e;
            }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ui.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish within 30 s.");

        try
        {
            Assert.Null(failure);
            Assert.False(handledBeforeUs, "the shell's handler must leave the exception unhandled");
            var file = Assert.Single(ProgramLog.Files(directory));
            Assert.Equal("OnboardTerminal.log", Path.GetFileName(file));
            var text = ProgramLog.ReadText(directory);
            Assert.Contains("[FTL] Simple.Logging.UnhandledExceptions: Nothing in the UI thread caught this exception", text, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException: Door-button-crash", text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file just closed can still be held for a moment
                // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
            }
        }
    }
}
