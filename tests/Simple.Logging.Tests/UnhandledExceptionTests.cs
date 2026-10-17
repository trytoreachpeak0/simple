using System.Runtime.CompilerServices;

namespace Simple.Logging.Tests;

/// <summary>
/// A program log also records what nothing in the program caught (ADR 0034):
/// the program then goes on as it would have without the log.
/// </summary>
public sealed class UnhandledExceptionTests
{
    [Fact]
    public void AFaultedTaskNobodyAwaitedIsRecordedAndLeftUnobserved()
    {
        using var log = new TestLog();
        bool? observed = null;
        EventHandler<UnobservedTaskExceptionEventArgs> after = (_, e) =>
        {
            if (e.Exception.InnerException?.Message == "Forgotten-task")
            {
                observed = e.Observed;
            }
        };
        TaskScheduler.UnobservedTaskException += after;
        try
        {
            FaultAndForget();
            // The event is raised when the task is finalized.
            for (var attempt = 0; attempt < 10 && observed is null; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= after;
        }

        var text = log.Text();
        Assert.Contains("[ERR] Simple.Logging.UnhandledExceptions: A task failed and nothing awaited it", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: Forgotten-task", text, StringComparison.Ordinal);
        Assert.False(observed, "the log must not mark the exception observed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndForget() => _ = Task.FromException(new InvalidOperationException("Forgotten-task"));
}
