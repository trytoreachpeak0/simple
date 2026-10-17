namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// Writing a slot's unlock output over Modbus, as the onboard terminal does
/// (ADR 0024): the output pulses and returns to 0 by itself, the door pops
/// open and the lock feedback opens after its delay.
/// </summary>
public sealed class UnlockTests : IDisposable
{
    private readonly IoModule module = new();

    [Fact]
    public void UnlockPulsesTheOutputPopsTheDoorAndOpensTheLockFeedbackAfterItsDelay()
    {
        // Slot 3: unlock DO3, lock feedback DI3 (1 = locked).
        Assert.True(module.Input(3));

        module.WriteOutput(3, true);

        Assert.True(module.Output(3));
        Assert.True(module.Cabinet.State().Slots[2].DoorOpen);
        module.Time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.True(module.Input(3));
        module.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(module.Input(3));
        module.Time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.True(module.Output(3));
        module.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(module.Output(3));
        Assert.False(module.Input(3));
    }

    public void Dispose() => module.Dispose();
}
