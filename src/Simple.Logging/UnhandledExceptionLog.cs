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

    // What RecordUnhandled last recorded, and where: the runtime raises the same
    // exception again as it ends the process, and once in the log is enough.
    private (Exception Exception, string Where)? recorded;

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
    public void RecordUnhandled(Exception exception, string where)
    {
        recorded = (exception, where);
        Unhandled(logger, where, exception);
    }

    /// <summary>What <see cref="AppDomain.UnhandledException"/> reports, as the runtime raises it.</summary>
    internal void OnUnhandled(object exceptionObject, bool isTerminating)
    {
        if (recorded is { } earlier && ReferenceEquals(earlier.Exception, exceptionObject))
        {
            EndsTheProcess(logger, earlier.Where);
        }
        else if (exceptionObject is Exception exception)
        {
            Unhandled(logger, isTerminating ? "the process (it ends now)" : "the process", exception);
        }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => OnUnhandled(e.ExceptionObject, e.IsTerminating);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        TaskNotAwaited(logger, e.Exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Nothing in {Where} caught this exception")]
    private static partial void Unhandled(ILogger logger, string where, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The exception above, which nothing in {Where} caught, ends the process")]
    private static partial void EndsTheProcess(ILogger logger, string where);

    [LoggerMessage(Level = LogLevel.Error, Message = "A task failed and nothing awaited it")]
    private static partial void TaskNotAwaited(ILogger logger, Exception exception);
}
