using Microsoft.Extensions.Logging;

namespace Simple.Logging;

/// <summary>
/// Records in the program log what nothing in the program caught, then lets
/// the program go on exactly as it would have: an exception that ends the
/// process still ends it, and a faulted task nobody awaited is not marked
/// observed. Subscribed while it lives; disposing it unsubscribes.
/// </summary>
public sealed partial class UnhandledExceptionLog : IDisposable
{
    public const string Category = "Simple.Logging.UnhandledExceptions";

    private readonly ILogger logger;

    public UnhandledExceptionLog(ILogger logger)
    {
        this.logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Records an exception a UI thread did not catch, for example from WPF's
    /// <c>Dispatcher.UnhandledException</c>; the caller leaves it unhandled.
    /// </summary>
    public void RecordUnhandled(Exception exception, string where) => Unhandled(logger, where, exception);

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Unhandled(logger, e.IsTerminating ? "the process (it ends now)" : "the process", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        TaskNotAwaited(logger, e.Exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Nothing in {Where} caught this exception")]
    private static partial void Unhandled(ILogger logger, string where, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A task failed and nothing awaited it")]
    private static partial void TaskNotAwaited(ILogger logger, Exception exception);
}
