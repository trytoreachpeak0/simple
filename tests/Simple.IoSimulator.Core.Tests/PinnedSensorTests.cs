namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// A pinned sensor reads its pinned value whatever happens in the slot, so a
/// test can make the readings contradict the facts; unpinned, it reads the
/// facts again.
/// </summary>
public sealed class PinnedSensorTests : IDisposable
{
    private readonly IoModule module = new();

    [Fact]
    public void APinnedLightCurtainReadsItsPinWhileTheSlotStaysEmpty()
    {
        module.Cabinet.PinLightCurtain(4, false);

        Assert.False(module.Input(12));
        Assert.Equal(new SlotState(4, DoorOpen: false, Cargo: false, LockFeedbackPin: null, LightCurtainPin: false), module.Cabinet.State().Slots[3]);

        module.Cabinet.PinLightCurtain(4, null);
        Assert.True(module.Input(12));
    }

    [Fact]
    public void APinnedLockFeedbackReadsLockedThroughAnUnlockUntilUnpinned()
    {
        module.Cabinet.PinLockFeedback(1, true);
        module.WriteOutput(1, true);
        module.Time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(module.Input(1));
        Assert.True(module.Cabinet.State().Slots[0].DoorOpen);

        module.Cabinet.PinLockFeedback(1, null);
        Assert.False(module.Input(1));
    }

    public void Dispose() => module.Dispose();
}
