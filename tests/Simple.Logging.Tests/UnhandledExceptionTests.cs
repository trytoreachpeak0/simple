using System.Runtime.CompilerServices;

namespace Simple.Logging.Tests;

/// <summary>
/// A program log also records what nothing in the program caught (ADR 0034):
/// the program then goes on as it would have without the log.
/// </summary>
/// <remarks>
/// Run alone: the runtime raises an unobserved task exception to every
/// handler in the process, so every other test's log open at that moment
/// recorded the forgotten task too (ProgramLogTests, 2026-10-18, #85).
/// </remarks>
[Collection(nameof(UnhandledExceptionTests))]
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

    [Fact]
    public void AnExceptionTheUiThreadRecordedIsNotRecordedAgainWhenItEndsTheProcess()
    {
        using var log = new TestLog();
        var crash = new InvalidOperationException("Door-button-crash");

        log.Provider.Unhandled.RecordUnhandled(crash, "the UI thread");
        // What the runtime raises next, as the exception ends the process.
        log.Provider.Unhandled.OnUnhandled(crash, isTerminating: true);

        var text = log.Text();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Door-button-crash"));
        Assert.Contains("[FTL] Simple.Logging.UnhandledExceptions: The exception above, which nothing in the UI thread caught, ends the process", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionNothingCaughtIsRecordedWhenItEndsTheProcess()
    {
        using var log = new TestLog();

        log.Provider.Unhandled.OnUnhandled(new InvalidOperationException("Worker-crash"), isTerminating: true);

        var text = log.Text();
        Assert.Contains("[FTL] Simple.Logging.UnhandledExceptions: Nothing in the process (it ends now) caught this exception", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: Worker-crash", text, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndForget() => _ = Task.FromException(new InvalidOperationException("Forgotten-task"));
}

[CollectionDefinition(nameof(UnhandledExceptionTests), DisableParallelization = true)]
public sealed class UnhandledExceptionTestsRunAlone;
