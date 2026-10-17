using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Simple.IoSimulator.Core;

public enum SlotAction
{
    /// <summary>Put cargo in.</summary>
    Load,

    /// <summary>Take the cargo out.</summary>
    Unload,
}

/// <summary>What the auto-responder is told to do.</summary>
/// <param name="Action">What it does at each door.</param>
/// <param name="Doors">How many of the next doors that open it handles.</param>
/// <param name="ActAfter">From a door opening to the cargo going in or out.</param>
/// <param name="CloseAfter">From the cargo going in or out to the door being closed.</param>
/// <param name="Within">If set, every door must be handled this long after the start, or it fails.</param>
public sealed record AutoResponse(SlotAction Action, int Doors, TimeSpan ActAfter, TimeSpan CloseAfter, TimeSpan? Within);

public enum AutoResponderState
{
    /// <summary>Nothing to do: never started, or cancelled.</summary>
    Idle,

    Running,

    /// <summary>Every door it was told to handle is handled.</summary>
    Done,

    /// <summary>Something kept it from handling a door; <see cref="AutoResponderStatus.Failure"/> says what. It does nothing more.</summary>
    Failed,
}

/// <param name="Response">What it was told; null while idle.</param>
/// <param name="DoorsHandled">Doors it has finished: cargo in or out, door closed.</param>
/// <param name="Slots">The slots whose doors it took on, in the order they opened.</param>
/// <param name="Failure">Why it failed; null unless <see cref="AutoResponderState.Failed"/>.</param>
public sealed record AutoResponderStatus(AutoResponderState State, AutoResponse? Response, int DoorsHandled, IReadOnlyList<int> Slots, string? Failure);

/// <summary>
/// Plays the person at the slots for the next doors that open, so that tests,
/// scenarios and trial runs need no script answering one door at a time: for
/// each door, after <see cref="AutoResponse.ActAfter"/> it puts cargo in or
/// takes it out, after <see cref="AutoResponse.CloseAfter"/> it closes the
/// door. A door that is closed by then, a full slot to load, an empty slot to
/// unload, or too few doors by <see cref="AutoResponse.Within"/> make it fail
/// and stop; <see cref="WaitAsync"/> then returns the failure.
/// </summary>
public sealed partial class AutoResponder : IDisposable
{
    private readonly Cabinet cabinet;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly List<ITimer> timers = [];
    private AutoResponderStatus status = new(AutoResponderState.Idle, null, 0, [], null);
    private TaskCompletionSource<AutoResponderStatus> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int run;

    public AutoResponder(Cabinet cabinet, TimeProvider time, ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        ArgumentNullException.ThrowIfNull(time);
        this.cabinet = cabinet;
        this.time = time;
        logger = (loggers ?? NullLoggerFactory.Instance).CreateLogger<AutoResponder>();
        cabinet.DoorOpened += OnDoorOpened;
    }

    public AutoResponderStatus Status()
    {
        lock (gate)
        {
            return status;
        }
    }

    /// <summary>Starts handling the next doors that open, in place of whatever it was doing.</summary>
    public AutoResponderStatus Start(AutoResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfLessThan(response.Doors, 1);
        lock (gate)
        {
            Reset();
            var current = run;
            status = new AutoResponderStatus(AutoResponderState.Running, response, 0, [], null);
            LogStarted(logger, response.Action, response.Doors, response.ActAfter.TotalSeconds, response.CloseAfter.TotalSeconds, response.Within?.TotalSeconds);
            if (response.Within is { } within)
            {
                After(within, () =>
                {
                    if (run == current && status.State == AutoResponderState.Running)
                    {
                        Fail(string.Create(CultureInfo.InvariantCulture, $"{status.DoorsHandled} of {response.Doors} doors handled within {within.TotalSeconds:0.###} s"));
                    }
                });
            }

            return status;
        }
    }

    /// <summary>Stops whatever it was doing; it is idle afterwards.</summary>
    public AutoResponderStatus Cancel()
    {
        lock (gate)
        {
            Reset();
            status = new AutoResponderStatus(AutoResponderState.Idle, null, 0, [], null);
            return status;
        }
    }

    /// <summary>Returns once it is done or failed (at once if it is not running), or idle after a cancel.</summary>
    public Task<AutoResponderStatus> WaitAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return status.State == AutoResponderState.Running
                ? finished.Task.WaitAsync(cancellationToken)
                : Task.FromResult(status);
        }
    }

    public void Dispose()
    {
        cabinet.DoorOpened -= OnDoorOpened;
        lock (gate)
        {
            Reset();
        }
    }

    private void OnDoorOpened(int slot)
    {
        lock (gate)
        {
            if (status is not { State: AutoResponderState.Running, Response: { } response } || status.Slots.Count >= response.Doors)
            {
                return;
            }

            var current = run;
            status = status with { Slots = [.. status.Slots, slot] };
            var verb = response.Action == SlotAction.Load ? "load" : "unload";
            After(response.ActAfter, current, () => response.Action == SlotAction.Load ? cabinet.Load(slot) : cabinet.Unload(slot), done =>
            {
                if (done != SlotResult.Done)
                {
                    Fail(string.Create(CultureInfo.InvariantCulture, $"slot {slot}: could not {verb}: {done}"));
                    return;
                }

                After(response.CloseAfter, current, () => cabinet.Close(slot), closed =>
                {
                    if (closed != SlotResult.Done)
                    {
                        Fail(string.Create(CultureInfo.InvariantCulture, $"slot {slot}: could not close the door: {closed}"));
                        return;
                    }

                    status = status with { DoorsHandled = status.DoorsHandled + 1 };
                    if (status.DoorsHandled == response.Doors)
                    {
                        status = status with { State = AutoResponderState.Done };
                        LogDone(logger, response.Doors, status.Slots);
                        finished.TrySetResult(status);
                    }
                });
            });
        }
    }

    /// <summary>
    /// After <paramref name="delay"/>, if run <paramref name="current"/> is
    /// still running: does <paramref name="act"/> to the cabinet outside the
    /// lock, then <paramref name="then"/> with its result under the lock. The
    /// cabinet is never touched under this lock: its changes reach the
    /// Modbus server's lock, under which the server tells of opened doors.
    /// Called under the lock.
    /// </summary>
    private void After(TimeSpan delay, int current, Func<SlotResult> act, Action<SlotResult> then) =>
        timers.Add(time.CreateTimer(
            _ =>
            {
                lock (gate)
                {
                    if (run != current || status.State != AutoResponderState.Running)
                    {
                        return;
                    }
                }

                var result = act();
                lock (gate)
                {
                    if (run == current && status.State == AutoResponderState.Running)
                    {
                        then(result);
                    }
                }
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan));

    /// <summary>Runs <paramref name="step"/> under the lock after <paramref name="delay"/>. Called under the lock.</summary>
    private void After(TimeSpan delay, Action step) =>
        timers.Add(time.CreateTimer(
            _ =>
            {
                lock (gate)
                {
                    step();
                }
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan));

    /// <summary>Called under the lock.</summary>
    private void Fail(string failure)
    {
        status = status with { State = AutoResponderState.Failed, Failure = failure };
        LogFailed(logger, failure);
        finished.TrySetResult(status);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-responder started: {Action} at the next {Doors} door(s), acting after {ActSeconds} s, closing after {CloseSeconds} s, within {WithinSeconds} s")]
    private static partial void LogStarted(ILogger logger, SlotAction action, int doors, double actSeconds, double closeSeconds, double? withinSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-responder done: {Doors} door(s), slots {Slots}")]
    private static partial void LogDone(ILogger logger, int doors, IReadOnlyList<int> slots);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-responder failed: {Failure}")]
    private static partial void LogFailed(ILogger logger, string failure);

    /// <summary>Ends the current run: its timers stop, and a waiter gets the state it ends in. Called under the lock.</summary>
    private void Reset()
    {
        run++;
        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        timers.Clear();
        finished.TrySetResult(status with { State = status.State == AutoResponderState.Running ? AutoResponderState.Idle : status.State });
        finished = new TaskCompletionSource<AutoResponderStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
