namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// What the person at the slot does: put cargo in, take it out, close the
/// door. Cargo goes in and out only through an open door; the light curtain
/// and the lock feedback follow after their delays.
/// </summary>
public sealed class PersonAtTheSlotTests : IDisposable
{
    private readonly IoModule module = new();

    [Fact]
    public void CargoGoesInThroughAnOpenDoorAndTheClosedDoorLocks()
    {
        // Slot 2: lock feedback DI2, light curtain DI10 (0 = cargo in the way).
        Assert.Equal(SlotResult.DoorNotOpen, module.Cabinet.Load(2));
        module.WriteOutput(2, true);
        module.Time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(SlotResult.Done, module.Cabinet.Load(2));
        Assert.True(module.Input(10));
        module.Time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(module.Input(10));
        Assert.Equal(SlotResult.SlotNotEmpty, module.Cabinet.Load(2));

        Assert.Equal(SlotResult.Done, module.Cabinet.Close(2));
        Assert.False(module.Input(2));
        module.Time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(module.Input(2));
        Assert.Equal(SlotResult.DoorNotOpen, module.Cabinet.Close(2));
        Assert.Equal(SlotResult.DoorNotOpen, module.Cabinet.Unload(2));
        Assert.Equal(new SlotState(2, DoorOpen: false, Cargo: true, null, null), module.Cabinet.State().Slots[1]);
    }

    [Fact]
    public void CargoThereFromTheStartIsSeenAtOnceAndComesOutThroughTheDoor()
    {
        // A scenario's initial state: slots 1 and 3 hold cargo when the cabinet starts.
        using var cabinet = new Cabinet(module.Time, CabinetTiming.Default, [1, 3]);

        var inputs = cabinet.Inputs();
        Assert.False(inputs[8]);
        Assert.True(inputs[9]);
        Assert.False(inputs[10]);
        Assert.True(cabinet.State().Slots[0].Cargo);
        cabinet.WriteOutput(1, true);
        Assert.Equal(SlotResult.SlotNotEmpty, cabinet.Load(1));
        Assert.Equal(SlotResult.Done, cabinet.Unload(1));
    }

    [Fact]
    public void CargoComesOutThroughAnOpenDoorAndAnEmptySlotHasNothingToTakeOut()
    {
        module.WriteOutput(5, true);
        Assert.Equal(SlotResult.SlotEmpty, module.Cabinet.Unload(5));
        module.Cabinet.Load(5);
        module.Time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(module.Input(13));

        Assert.Equal(SlotResult.Done, module.Cabinet.Unload(5));
        module.Time.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(module.Input(13));
        module.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(module.Input(13));
    }

    public void Dispose() => module.Dispose();
}
