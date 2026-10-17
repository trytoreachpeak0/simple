using System.Text.Json;
using System.Text.Json.Nodes;
using Simple.Cli.Scenarios;

namespace Simple.Cli.Tests;

/// <summary>
/// Judging one expectation on a run's looks, without a system: each kind
/// reaches each of the four verdicts, a look that cannot see the condition is
/// never taken for a pass, and the event record is judged once it has caught
/// up after the window.
/// </summary>
public sealed class ExpectationTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private static readonly StatusCondition RiotConnected = Status("riot.state", Comparison.EqualTo, "connected");

    [Fact]
    public void EventuallyHoldsAtTheFirstLookThatSeesIt()
    {
        var tracker = Open(ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(5));

        Assert.False(tracker.Look(Look(1, """{"riot":{"state":"connecting"}}""")));
        Assert.True(tracker.Look(Look(2, """{"riot":{"state":"connected"}}""")));

        Assert.Equal(Verdict.Holds, tracker.Verdict);
        Assert.Contains("riot.state is \"connected\"", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EventuallyDoesNotHoldWhenEveryLookSawItFalse()
    {
        var tracker = Open(ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, """{"riot":{"state":"unreachable"}}"""));
        tracker.Look(Look(2, """{"riot":{"state":"unreachable"}}"""));
        Assert.Null(tracker.Verdict);
        Assert.True(tracker.Look(Look(3, """{"riot":{"state":"unreachable"}}""")));

        Assert.Equal(Verdict.DoesNotHold, tracker.Verdict);
    }

    [Fact]
    public void EventuallyCannotBeToldWhenALookCouldNotSeeIt()
    {
        var tracker = Open(ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, """{"riot":{"state":"unreachable"}}"""));
        tracker.Look(Unreadable(2));
        tracker.Look(Look(3, """{"riot":{"state":"unreachable"}}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("1 of 2 looks", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APathTheStatusDoesNotHaveCannotBeTold()
    {
        var tracker = Open(ExpectationKind.Eventually, Status("onboardTerminals[vehicleId=vehicle-9].connected", Comparison.EqualTo, true), TimeSpan.FromSeconds(1));

        tracker.Look(Look(0.5, """{"onboardTerminals":[{"vehicleId":"vehicle-1","connected":true}]}"""));
        tracker.Look(Look(2, """{"onboardTerminals":[{"vehicleId":"vehicle-1","connected":true}]}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("the status has no onboardTerminals[vehicleId=vehicle-9].connected", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AlwaysDoesNotHoldAtTheFirstLookThatSeesItFalse()
    {
        var tracker = Open(ExpectationKind.Always, Status("onboardTerminals.count", Comparison.EqualTo, 2), TimeSpan.FromSeconds(10));

        tracker.Look(Look(1, """{"onboardTerminals":[{},{}]}"""));
        Assert.True(tracker.Look(Look(2, """{"onboardTerminals":[{}]}""")));

        Assert.Equal(Verdict.DoesNotHold, tracker.Verdict);
        Assert.Contains("onboardTerminals.count is 1", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AlwaysHoldsWhenEveryLookSawIt()
    {
        var tracker = Open(ExpectationKind.Always, Status("mesGateway.rowCount", Comparison.AtLeast, 3), TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, """{"mesGateway":{"rowCount":3}}"""));
        tracker.Look(Look(2, """{"mesGateway":{"rowCount":4}}"""));
        tracker.Look(Look(3, """{"mesGateway":{"rowCount":0}}"""));

        Assert.Equal(Verdict.Holds, tracker.Verdict);
    }

    [Fact]
    public void AlwaysWithNoLookInTheWindowCannotBeTold()
    {
        var tracker = Open(ExpectationKind.Always, RiotConnected, TimeSpan.FromSeconds(1));

        tracker.Look(Look(5, """{"riot":{"state":"connected"}}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
    }

    [Fact]
    public void NeverDoesNotHoldOnceTheEventIsRecorded()
    {
        var tracker = Open(ExpectationKind.Never, new EventCondition("onboard.refused", null, null), TimeSpan.FromSeconds(10));

        tracker.Look(Look(1, "{}", Event(1, 0.5, "onboard.refused")));

        Assert.Equal(Verdict.DoesNotHold, tracker.Verdict);
    }

    [Fact]
    public void AnEventBeforeTheWindowDoesNotCount()
    {
        var tracker = Open(ExpectationKind.Never, new EventCondition("onboard.refused", null, null), TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, "{}", Event(1, -1, "onboard.refused")));
        tracker.Look(Look(3, "{}", Event(1, -1, "onboard.refused")));
        Assert.Null(tracker.Verdict);
        tracker.Look(Look(4, "{}", Event(1, -1, "onboard.refused")));

        Assert.Equal(Verdict.Holds, tracker.Verdict);
    }

    [Fact]
    public void AnEventAfterTheWindowDoesNotCount()
    {
        var tracker = Open(ExpectationKind.Eventually, new EventCondition("mes.recovered", null, null), TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, "{}"));
        tracker.Look(Look(4, "{}", Event(7, 2.5, "mes.recovered")));

        Assert.Equal(Verdict.DoesNotHold, tracker.Verdict);
    }

    [Fact]
    public void ALookBeforeTheWindowOpensIsNotJudged()
    {
        var tracker = Open(ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(5));

        Assert.False(tracker.Look(Look(-1, """{"riot":{"state":"connected"}}""")));

        Assert.Null(tracker.Verdict);
    }

    [Theory]
    [InlineData(ExpectationKind.Always, "connected")]
    [InlineData(ExpectationKind.Never, "unreachable")]
    public void AStatusWindowWithAStretchNoLookSawCannotBeTold(ExpectationKind kind, string state)
    {
        // One look early on, the next long after the window: an event that
        // held the loop back. What the status showed in between was not seen.
        var tracker = Open(kind, Status("riot.state", Comparison.EqualTo, state == "connected" ? "connected" : "disconnected"), TimeSpan.FromSeconds(5));

        var status = "{\"riot\":{\"state\":\"" + state + "\"}}";
        tracker.Look(Look(0.1, status));
        tracker.Look(Look(10, status));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("not looked at for 4.9 s", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStretchBeforeTheFirstLookCounts()
    {
        // The window opened 1.5 s before anything was looked at.
        var tracker = Open(ExpectationKind.Always, RiotConnected, TimeSpan.FromSeconds(3));

        for (var at = 1.5; at <= 3; at += 0.25)
        {
            tracker.Look(Look(at, """{"riot":{"state":"connected"}}"""));
        }

        tracker.Look(Look(3.5, """{"riot":{"state":"connected"}}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("not looked at for 1.5 s", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EventuallyNotSeenWithAStretchNoLookSawCannotBeTold()
    {
        var tracker = Open(ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(5));

        tracker.Look(Look(0.1, """{"riot":{"state":"unreachable"}}"""));
        tracker.Look(Look(0.3, """{"riot":{"state":"unreachable"}}"""));
        tracker.Look(Look(4.5, """{"riot":{"state":"unreachable"}}"""));
        tracker.Look(Look(6, """{"riot":{"state":"unreachable"}}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("not looked at for 4.2 s", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventDecisionTheFinalRecordContradictsCannotBeTold()
    {
        var tracker = Open(ExpectationKind.Never, new EventCondition("onboard.refused", null, null), TimeSpan.FromSeconds(1));
        tracker.Look(Look(0.5, "{}"));
        tracker.Look(Look(3, "{}"));
        Assert.Equal(Verdict.Holds, tracker.Verdict);

        // Stamped inside the window, it reached the record only after the deciding look.
        Assert.True(tracker.Recheck(Look(5, "{}", Event(9, 0.9, "onboard.refused"))));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
        Assert.Contains("disagrees", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventDecisionTheFinalRecordAgreesWithStands()
    {
        var tracker = Open(ExpectationKind.Eventually, new EventCondition("mes.recovered", null, null), TimeSpan.FromSeconds(2));
        tracker.Look(Look(1, "{}", Event(3, 0.5, "mes.recovered")));

        Assert.False(tracker.Recheck(Look(5, "{}", Event(3, 0.5, "mes.recovered"))));
        Assert.False(tracker.Recheck(Unreadable(6)));

        Assert.Equal(Verdict.Holds, tracker.Verdict);
    }

    [Fact]
    public void AnEventRecordedLateButOccurringInTheWindowIsFoundAfterTheWindow()
    {
        var tracker = Open(ExpectationKind.Eventually, new EventCondition("mes.recovered", null, null), TimeSpan.FromSeconds(2));

        tracker.Look(Look(1, "{}"));
        // The look just after the window: the record has not caught up yet.
        Assert.False(tracker.Look(Look(2.5, "{}")));
        Assert.True(tracker.Look(Look(3.6, "{}", Event(7, 1.9, "mes.recovered"))));

        Assert.Equal(Verdict.Holds, tracker.Verdict);
    }

    [Fact]
    public void AnEventRecordThatCannotBeReadAfterTheWindowCannotBeTold()
    {
        var tracker = Open(ExpectationKind.Never, new EventCondition("onboard.refused", null, null), TimeSpan.FromSeconds(1));

        tracker.Look(Look(0.5, "{}"));
        tracker.Look(Unreadable(3));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
    }

    [Fact]
    public void AnEventMatchesOnlyWithTheDataAndSourceAsked()
    {
        var condition = new EventCondition("onboard.disconnected", "server", new JsonObject { ["vehicleId"] = "vehicle-2" });
        var tracker = Open(ExpectationKind.Eventually, condition, TimeSpan.FromSeconds(1));

        tracker.Look(Look(0.5, "{}", Event(1, 0.1, "onboard.disconnected", """{"vehicleId":"vehicle-1"}"""), Event(2, 0.2, "onboard.disconnected", """{"vehicleId":"vehicle-2"}""", "onboard-terminal")));
        tracker.Look(Look(3, "{}", Event(1, 0.1, "onboard.disconnected", """{"vehicleId":"vehicle-1"}"""), Event(2, 0.2, "onboard.disconnected", """{"vehicleId":"vehicle-2"}""", "onboard-terminal")));

        Assert.Equal(Verdict.DoesNotHold, tracker.Verdict);
    }

    [Fact]
    public void AnExpectationWhoseOnlyIfDoesNotHoldIsNotApplicable()
    {
        var expectation = new Expectation(null, "e", null, ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(5), null, Status("onboardTerminals.count", Comparison.AtLeast, 1), 1);
        var tracker = new ExpectationTracker(expectation);
        tracker.Open(Start);

        tracker.Look(Look(1, """{"onboardTerminals":[],"riot":{"state":"connected"}}"""));

        Assert.Equal(Verdict.NotApplicable, tracker.Verdict);
        Assert.Contains("only-if does not hold", tracker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOnlyIfThatCannotBeSeenCannotBeTold()
    {
        var expectation = new Expectation(null, "e", null, ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(1), null, Status("nothing.here", Comparison.EqualTo, 1), 1);
        var tracker = new ExpectationTracker(expectation);
        tracker.Open(Start);

        tracker.Look(Look(0.5, """{"riot":{"state":"connected"}}"""));
        Assert.Null(tracker.Verdict);
        tracker.Look(Look(2, """{"riot":{"state":"connected"}}"""));

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
    }

    [Fact]
    public void AnAbandonedExpectationCannotBeToldAndAWindowNotOpenSeesNothing()
    {
        var tracker = new ExpectationTracker(new Expectation(null, "e", null, ExpectationKind.Eventually, RiotConnected, TimeSpan.FromSeconds(1), "later", null, 1));

        Assert.False(tracker.Look(Look(1, """{"riot":{"state":"connected"}}""")));
        tracker.Abandon("event later failed", Start);

        Assert.Equal(Verdict.CannotTell, tracker.Verdict);
    }

    [Theory]
    [InlineData(new[] { Verdict.Holds, Verdict.Holds }, true, Verdict.Holds)]
    [InlineData(new[] { Verdict.Holds, Verdict.NotApplicable }, true, Verdict.Holds)]
    [InlineData(new[] { Verdict.NotApplicable, Verdict.NotApplicable }, true, Verdict.NotApplicable)]
    [InlineData(new[] { Verdict.Holds, Verdict.CannotTell }, true, Verdict.CannotTell)]
    [InlineData(new[] { Verdict.CannotTell, Verdict.DoesNotHold }, true, Verdict.DoesNotHold)]
    [InlineData(new[] { Verdict.Holds }, false, Verdict.CannotTell)]
    [InlineData(new[] { Verdict.DoesNotHold }, false, Verdict.DoesNotHold)]
    public void AScenariosVerdictComesFromItsExpectations(Verdict[] expectations, bool driven, Verdict verdict) =>
        Assert.Equal(verdict, Verdicts.Combine(expectations, driven));

    [Fact]
    public void EachVerdictHasItsOwnExitCode()
    {
        var codes = Enum.GetValues<Verdict>().Select(v => v.ExitCode()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(ExitCodes.Success, Verdict.Holds.ExitCode());
        Assert.All(Enum.GetValues<Verdict>(), v => Assert.Equal(v, Verdicts.Parse(v.Name())));
    }

    private static ExpectationTracker Open(ExpectationKind kind, Condition condition, TimeSpan window)
    {
        var tracker = new ExpectationTracker(new Expectation(null, "e", null, kind, condition, window, null, null, 1));
        tracker.Open(Start);
        return tracker;
    }

    private static StatusCondition Status(string path, Comparison comparison, JsonNode? operand) =>
        new(path, StatusCondition.ParsePath(path, out _)!, comparison, operand);

    private static Snapshot Look(double seconds, string status, params RecordedEvent[] events) =>
        new(Start.AddSeconds(seconds), JsonDocument.Parse(status).RootElement.Clone(), null, events, null);

    private static Snapshot Unreadable(double seconds) =>
        new(Start.AddSeconds(seconds), null, "connection refused", null, "connection refused");

    private static RecordedEvent Event(long id, double seconds, string type, string? data = null, string source = "server")
    {
        var element = data is null ? (JsonElement?)null : JsonDocument.Parse(data).RootElement.Clone();
        return new RecordedEvent(id, Start.AddSeconds(seconds), type, source, element, JsonDocument.Parse("{}").RootElement.Clone());
    }
}
