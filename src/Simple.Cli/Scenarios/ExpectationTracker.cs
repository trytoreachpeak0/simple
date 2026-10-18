using System.Globalization;

namespace Simple.Cli.Scenarios;

/// <summary>
/// Judges one expectation on the looks a run takes, from the moment its
/// window opens. A look that cannot see the condition is counted, never
/// taken for a pass: an expectation left with such looks and no decisive one
/// cannot be told.
/// </summary>
public sealed class ExpectationTracker(Expectation expectation)
{
    /// <summary>
    /// How long after its window closes an event condition is judged: an
    /// entry occurs a moment before the server's event record has it.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The longest a status condition may go unlooked-at within its window
    /// for the window's end to decide: looks come every 250 ms, but an event
    /// that takes long (stopping a process) holds the next look back, and
    /// what happened in between was not seen.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1);

    private int looks;
    private int unseen;
    private bool applies;
    private DateTimeOffset? lastLook;
    private TimeSpan widestGap;
    private DateTimeOffset widestGapFrom;

    public Expectation Expectation { get; } = expectation;

    public DateTimeOffset? OpenedAt { get; private set; }

    public Verdict? Verdict { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>What the last look saw.</summary>
    public Observation? Last { get; private set; }

    public void Open(DateTimeOffset at)
    {
        if (OpenedAt is null && Verdict is null)
        {
            OpenedAt = at;
            applies = Expectation.OnlyIf is null;
        }
    }

    /// <summary>The window will never open: the event it waits for failed or was not done.</summary>
    public void Abandon(string reason, DateTimeOffset at) => Decide(Scenarios.Verdict.CannotTell, reason, at);

    /// <summary>Judges one look; true when this look decided the expectation.</summary>
    public bool Look(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Verdict is not null || OpenedAt is not { } from || snapshot.At < from)
        {
            return false;
        }

        var until = from + Expectation.Window;
        if (!applies)
        {
            var onlyIf = Expectation.OnlyIf!.Observe(snapshot, from, snapshot.At);
            switch (onlyIf.Truth)
            {
                case Truth.True:
                    applies = true;
                    break;
                case Truth.False:
                    return Decide(Scenarios.Verdict.NotApplicable, $"only-if does not hold: {onlyIf.Detail}", snapshot.At);
                default:
                    return snapshot.At > until
                        && Decide(Scenarios.Verdict.CannotTell, $"only-if could not be seen within the window: {onlyIf.Detail}", snapshot.At);
            }
        }

        var condition = Expectation.Condition;
        if (snapshot.At <= until)
        {
            var seen = condition.Observe(snapshot, from, snapshot.At);
            Last = seen;
            looks++;
            Covered(snapshot.At);
            switch (seen.Truth, Expectation.Kind)
            {
                case (Truth.True, ExpectationKind.Eventually):
                    return Decide(Scenarios.Verdict.Holds, $"seen after {Seconds(snapshot.At - from)}: {seen.Detail}", snapshot.At);
                case (Truth.False, ExpectationKind.Always):
                    return Decide(Scenarios.Verdict.DoesNotHold, $"did not hold after {Seconds(snapshot.At - from)}: {seen.Detail}", snapshot.At);
                case (Truth.True, ExpectationKind.Never):
                    return Decide(Scenarios.Verdict.DoesNotHold, $"seen after {Seconds(snapshot.At - from)}: {seen.Detail}", snapshot.At);
                case (Truth.Unknown, _):
                    unseen++;
                    break;
            }

            return false;
        }

        // The window has closed. An entry of the event record, once there,
        // stays: one look after the window, once the record has caught up,
        // decides eventually and never for the whole window.
        if (condition.IsEvent && Expectation.Kind != ExpectationKind.Always)
        {
            if (snapshot.At < until + Settle)
            {
                return false;
            }

            var final = condition.Observe(snapshot, from, until);
            Last = final;
            return final.Truth switch
            {
                Truth.Unknown => Decide(Scenarios.Verdict.CannotTell, $"could not be seen: {final.Detail}", snapshot.At),
                Truth.True => Decide(
                    Expectation.Kind == ExpectationKind.Eventually ? Scenarios.Verdict.Holds : Scenarios.Verdict.DoesNotHold,
                    $"seen: {final.Detail}",
                    snapshot.At),
                _ => Decide(
                    Expectation.Kind == ExpectationKind.Eventually ? Scenarios.Verdict.DoesNotHold : Scenarios.Verdict.Holds,
                    final.Detail,
                    snapshot.At),
            };
        }

        var last = Last?.Detail ?? "nothing";
        Covered(until);
        if (widestGap > MaxGap)
        {
            return Decide(
                Scenarios.Verdict.CannotTell,
                $"not looked at for {Seconds(widestGap)} of the window, from {widestGapFrom.ToLocalTime():HH:mm:ss.fff}: what the status showed then was not seen; last look: {last}",
                snapshot.At);
        }

        if (looks == 0 || unseen > 0)
        {
            return Decide(
                Scenarios.Verdict.CannotTell,
                $"{unseen.ToString(CultureInfo.InvariantCulture)} of {looks.ToString(CultureInfo.InvariantCulture)} looks in {Seconds(Expectation.Window)} could not see it; last look: {last}",
                snapshot.At);
        }

        return Expectation.Kind == ExpectationKind.Eventually
            ? Decide(Scenarios.Verdict.DoesNotHold, $"not seen in {looks.ToString(CultureInfo.InvariantCulture)} looks within {Seconds(Expectation.Window)}; last look: {last}", snapshot.At)
            : Decide(Scenarios.Verdict.Holds, $"{looks.ToString(CultureInfo.InvariantCulture)} looks in {Seconds(Expectation.Window)}; last look: {last}", snapshot.At);
    }

    /// <summary>
    /// Judges an expectation on the event record once more, as the run's last
    /// look has it: an entry is stamped when it occurs but may reach the
    /// record later than the look that decided. True when the two disagree:
    /// the expectation then cannot be told.
    /// </summary>
    public bool Recheck(Snapshot final)
    {
        ArgumentNullException.ThrowIfNull(final);
        if (Verdict is not ({ } decided and (Scenarios.Verdict.Holds or Scenarios.Verdict.DoesNotHold))
            || OpenedAt is not { } from
            || !Expectation.Condition.IsEvent
            || Expectation.Kind == ExpectationKind.Always)
        {
            return false;
        }

        var seen = Expectation.Condition.Observe(final, from, from + Expectation.Window);
        if (seen.Truth == Truth.Unknown)
        {
            return false;
        }

        // Eventually holds and never does not hold when an entry is there.
        var wanted = (Expectation.Kind == ExpectationKind.Eventually) == (decided == Scenarios.Verdict.Holds) ? Truth.True : Truth.False;
        if (seen.Truth == wanted)
        {
            return false;
        }

        Verdict = Scenarios.Verdict.CannotTell;
        Reason = $"the event record at the end of the run disagrees with the look that judged it {decided.Name()} ({Reason}): {seen.Detail}";
        DecidedAt = final.At;
        return true;
    }

    /// <summary>Notes the stretch since the previous look (or since the window opened) that no look saw.</summary>
    private void Covered(DateTimeOffset at)
    {
        var since = lastLook ?? OpenedAt!.Value;
        if (at - since > widestGap)
        {
            widestGap = at - since;
            widestGapFrom = since;
        }

        lastLook = at;
    }

    private bool Decide(Verdict verdict, string reason, DateTimeOffset at)
    {
        if (Verdict is not null)
        {
            return false;
        }

        Verdict = verdict;
        Reason = reason;
        DecidedAt = at;
        return true;
    }

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
}
