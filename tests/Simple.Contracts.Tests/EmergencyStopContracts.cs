namespace Simple.Contracts.Tests;

/// <summary>The software emergency stop and its release, and what they do to orders.</summary>
public sealed class EmergencyStopContracts : OrderContractTest
{
    /// <summary>
    /// The command's success says nothing: the vehicle's emergencyState turns
    /// CAN_RECOVER a moment later, and OK again a few seconds after the release.
    /// </summary>
    [Fact]
    [Contract("BC-VEH-005")]
    public async Task TheEmergencyStateLocksAndReleasesAfterTheCommands()
    {
        Target.RequireWrites();

        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        var locked = await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上（CAN_RECOVER）");
        await Riot.Device.CancelEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "OK", "急停解除（OK）");

        Assert.Equal("CONTROL_STATE_ERR", locked.ControlState);
    }

    /// <summary>An emergency stop alone never puts the executing order into HANG: it stays EXECUTING, held by the stop.</summary>
    [Fact]
    [Contract("BC-ORDER-015")]
    public async Task AnEmergencyStopKeepsTheExecutingOrderExecuting()
    {
        Target.RequireWrites();
        var (upperId, _) = await ExecutingAsync("estop-executing");

        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上");

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Executing, "执行中、不进挂起");
        Assert.Equal(0d, (await VehicleAsync()).Speed);
    }

    /// <summary>A held order stays held through the stop and after the release; it does not go on by itself.</summary>
    [Fact]
    [Contract("BC-ORDER-020")]
    public async Task AHeldOrderStaysHeldThroughAnEmergencyStopAndItsRelease()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("estop-held");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);

        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上");
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Held, "暂停");
        await Riot.Device.CancelEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "OK", "急停解除");

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Held, "解除急停后仍暂停");
    }

    /// <summary>
    /// While the stop is locked, CONTINUE_FROM_HELD is accepted and the order
    /// executes again; only the stop holds the vehicle. So a release after it
    /// may let the vehicle go, which is why a continue waits for OK.
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-020")]
    public async Task ContinueFromHeldIsAcceptedWhileTheStopIsLocked()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("estop-continue");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上");

        await Riot.Tasks.OrderContinueAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Executing);
        Assert.Equal("CAN_RECOVER", (await VehicleAsync()).EmergencyState);
        Assert.Equal(0d, (await VehicleAsync()).Speed);
    }

    /// <summary>An order made for an emergency-stopped vehicle is accepted and stays queued.</summary>
    [Fact]
    [Contract("BC-ORDER-016")]
    public async Task AnOrderForAnEmergencyStoppedVehicleStaysQueued()
    {
        Target.RequireWrites();
        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上");
        var upperId = NewUpperId("estop-queued");

        await CreateMoveAsync(upperId, Facts.ReachableStation);

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队");
    }
}
