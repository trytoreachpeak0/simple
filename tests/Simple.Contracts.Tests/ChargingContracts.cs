using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>
/// The charge order: a move to the charger followed by act(78,1,0). Few
/// samples on real RIoT (lab rounds 24 and 25; #11 comment item 7).
/// </summary>
public sealed class ChargingContracts : OrderContractTest
{
    private static readonly OrderMissionAction StartCharging = new(78, 1, 0);

    /// <summary>The order turns SUCCESS once the vehicle starts charging, and the vehicle goes on charging without an order.</summary>
    [Fact]
    [Contract("round 25", "Q-033")]
    public async Task AChargeOrderSucceedsOnceTheVehicleCharges()
    {
        Target.RequireWrites();
        var charger = RequireCharger();
        var upperId = NewUpperId("charge");

        await Riot.Order.CreateMoveOrderAsync(upperId, Facts.VehicleKey, Facts.MapId, charger, StartCharging, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Success);
        var card = await UntilAsync(_ => CardAsync(), c => c.ProcState == "IDLE", "车回到 IDLE");

        Assert.Equal("CHARGING", card.BatteryState);
        Assert.Equal(charger, card.CurrentPosition);
    }

    /// <summary>The next order leaves the charger by itself: RIoT puts act(78,2,0) first in it.</summary>
    [Fact]
    [Contract("Q-033", "BC-ORDER-022")]
    public async Task TheNextOrderStartsWithEndChargingPutInByRiot()
    {
        Target.RequireWrites();
        var charger = RequireCharger();
        var charge = NewUpperId("charge");
        await Riot.Order.CreateMoveOrderAsync(charge, Facts.VehicleKey, Facts.MapId, charger, StartCharging, cancellationToken: Cancel);
        await UntilStateAsync(charge, OrderStates.Success);
        await UntilAsync(_ => CardAsync(), c => c.ProcState == "IDLE", "车回到 IDLE");
        var leave = NewUpperId("leave-charger");

        await CreateMoveAsync(leave, Facts.ReachableStation);
        var done = await UntilStateAsync(leave, OrderStates.Success);

        var first = done.Missions[0];
        Assert.Equal(("act", 78, 2, 0), (first.Type, first.ActionId, first.ActionParam1, first.ActionParam2));
        Assert.Equal("NO_CHARGE", (await CardAsync()).BatteryState);
    }

    /// <summary>A vehicle that cannot charge at the charger hangs the order, with act result code 407802.</summary>
    [Fact]
    [Contract("round 24", "Q-033")]
    public async Task AChargeThatCannotStartHangsWith407802()
    {
        var control = Target.RequireControl("拔掉充电桩的充电器");
        var charger = RequireCharger();
        await control.SetChargerConnectedAsync(Facts.MapId, charger, connected: false);
        var upperId = NewUpperId("charge-fails");

        await Riot.Order.CreateMoveOrderAsync(upperId, Facts.VehicleKey, Facts.MapId, charger, StartCharging, cancellationToken: Cancel);
        var hung = await UntilStateAsync(upperId, OrderStates.Hang);

        Assert.Contains(hung.Missions, m => m is { Type: "act", ActionId: 78, ResultCode: 407802 });
    }

    private int RequireCharger()
    {
        if (Facts.ChargingStation is not { } charger)
        {
            Assert.Skip("没有已知的充电桩站点。");
            return 0;
        }

        return charger;
    }
}
