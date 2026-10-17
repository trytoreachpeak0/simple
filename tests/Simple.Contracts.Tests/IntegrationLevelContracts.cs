namespace Simple.Contracts.Tests;

/// <summary>Taking a vehicle off line in RIoT and back, and what that does to its orders.</summary>
public sealed class IntegrationLevelContracts : OrderContractTest
{
    [Fact]
    [Contract("BC-VEH-003")]
    public async Task DisableAndEnableSwitchTheIntegrationLevel()
    {
        Target.RequireWrites();

        await Riot.Tasks.DispatchDisableAsync(Facts.VehicleKey, Cancel);
        var off = await UntilAsync(_ => VehicleAsync(), v => v.IntegrationLevel == "OFF_LINE", "车下线");
        await Riot.Tasks.DispatchEnableAsync(Facts.VehicleKey, Cancel);
        var on = await UntilAsync(_ => VehicleAsync(), v => v.IntegrationLevel == "ON_LINE", "车上线");

        Assert.Equal(false, off.Enable);
        Assert.Equal(true, on.Enable);
    }

    /// <summary>Off line does not refuse an order: it is made and stays queued.</summary>
    [Fact]
    [Contract("BC-VEH-004")]
    public async Task AnOrderForAnOfflineVehicleIsMadeAndStaysQueued()
    {
        Target.RequireWrites();
        await Riot.Tasks.DispatchDisableAsync(Facts.VehicleKey, Cancel);
        var upperId = NewUpperId("offline-queued");

        await CreateMoveAsync(upperId, Facts.ReachableStation);

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队");
    }

    /// <summary>Taking a vehicle off line does not stop the order it is executing.</summary>
    [Fact]
    [Contract("BC-VEH-004")]
    public async Task DisablingAVehicleDoesNotStopItsExecutingOrder()
    {
        Target.RequireWrites();
        var (upperId, _) = await ExecutingAsync("offline-executing");

        await Riot.Tasks.DispatchDisableAsync(Facts.VehicleKey, Cancel);

        await UntilStateAsync(upperId, OrderStates.Success);
    }

    /// <summary>
    /// A held or hung order of an off-line vehicle can still be continued.
    /// The product owner's statement, seen only on the test RIoT (ADR 0011).
    /// </summary>
    [Fact]
    [Contract("ADR 0011")]
    [NotVerifiedOnRealRiot("下线的车上，暂停、挂起的单仍能恢复执行（产品负责人，ADR 0011；只在测试环境观测过）")]
    public async Task AHeldOrderOfAnOfflineVehicleCanContinue()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("offline-held");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        await Riot.Tasks.DispatchDisableAsync(Facts.VehicleKey, Cancel);

        await Riot.Tasks.OrderContinueAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Success);
    }
}
