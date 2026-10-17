namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// The auto-responder plays the person at the slots for the next doors that
/// open: after a while it puts cargo in (or takes it out), after another while
/// it closes the door. Whatever keeps it from doing so is a failure it reports
/// and stops at, never a silent success.
/// </summary>
/// <remarks>
/// The fake clock's Advance moves to its end before it fires what is due, so
/// a step the auto-responder schedules from a step is timed from there: the
/// tests advance one step at a time.
/// </remarks>
public sealed class AutoResponderTests : IDisposable
{
    private static readonly TimeSpan Four = TimeSpan.FromSeconds(4);

    private readonly IoModule module = new();
    private readonly AutoResponder responder;

    public AutoResponderTests() => responder = new AutoResponder(module.Cabinet, module.Time);

    [Fact]
    public async Task ItLoadsAndClosesTheNextDoorsThatOpenAndThenIsDone()
    {
        responder.Start(new AutoResponse(SlotAction.Load, Doors: 2, ActAfter: Four, CloseAfter: Four, Within: null));
        var waiting = WaitAsync();

        module.WriteOutput(1, true);
        module.Time.Advance(Four - TimeSpan.FromMilliseconds(1));
        Assert.False(module.Cabinet.State().Slots[0].Cargo);
        module.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(module.Cabinet.State().Slots[0].Cargo);
        module.Time.Advance(Four);
        Assert.False(module.Cabinet.State().Slots[0].DoorOpen);
        Assert.Equal(new AutoResponderProgress(AutoResponderState.Running, 1, [1], null), Progress());
        Assert.False(waiting.IsCompleted);

        module.WriteOutput(6, true);
        module.Time.Advance(Four);
        module.Time.Advance(Four);

        var done = await waiting;
        Assert.Equal(new AutoResponderProgress(AutoResponderState.Done, 2, [1, 6], null), Progress(done));
        module.WriteOutput(7, true);
        module.Time.Advance(Four);
        module.Time.Advance(Four);
        Assert.Equal(new SlotState(7, DoorOpen: true, Cargo: false, null, null), module.Cabinet.State().Slots[6]);
    }

    [Fact]
    public async Task ADoorClosedBeforeItActsIsAFailure()
    {
        responder.Start(new AutoResponse(SlotAction.Load, Doors: 1, ActAfter: Four, CloseAfter: Four, Within: null));
        module.WriteOutput(3, true);
        module.Cabinet.Close(3);

        module.Time.Advance(Four);

        var failed = await WaitAsync();
        Assert.Equal(AutoResponderState.Failed, failed.State);
        Assert.Equal("slot 3: could not load: DoorNotOpen", failed.Failure);
    }

    [Fact]
    public async Task TakingOutOfAnEmptySlotIsAFailure()
    {
        responder.Start(new AutoResponse(SlotAction.Unload, Doors: 1, ActAfter: Four, CloseAfter: Four, Within: null));
        module.WriteOutput(2, true);

        module.Time.Advance(Four);

        var failed = await WaitAsync();
        Assert.Equal("slot 2: could not unload: SlotEmpty", failed.Failure);
    }

    [Fact]
    public async Task TooFewDoorsOpeningInTimeIsAFailure()
    {
        responder.Start(new AutoResponse(SlotAction.Load, Doors: 2, ActAfter: Four, CloseAfter: Four, Within: TimeSpan.FromSeconds(60)));
        module.WriteOutput(1, true);
        module.Time.Advance(Four);
        module.Time.Advance(Four);

        module.Time.Advance(TimeSpan.FromSeconds(52));

        var failed = await WaitAsync();
        Assert.Equal("1 of 2 doors handled within 60 s", failed.Failure);
    }

    public void Dispose()
    {
        responder.Dispose();
        module.Dispose();
    }

    private Task<AutoResponderStatus> WaitAsync() =>
        responder.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private AutoResponderProgress Progress(AutoResponderStatus? status = null)
    {
        status ??= responder.Status();
        return new AutoResponderProgress(status.State, status.DoorsHandled, [.. status.Slots], status.Failure);
    }

    private sealed record AutoResponderProgress(AutoResponderState State, int DoorsHandled, IReadOnlyList<int> Slots, string? Failure)
    {
        public bool Equals(AutoResponderProgress? other) =>
            other is not null && State == other.State && DoorsHandled == other.DoorsHandled && Slots.SequenceEqual(other.Slots) && Failure == other.Failure;

        public override int GetHashCode() => HashCode.Combine(State, DoorsHandled, Failure);
    }
}
