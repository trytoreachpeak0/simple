using System.Text.Json;
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
    [Contract("round 25", "Q-033", "#11", "ADR 0020")]
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

    /// <summary>
    /// The next order leaves the charger by itself: RIoT puts act(78,2,0)
    /// first in it (the 2026-10-17 agv03 field observation, BC-ORDER-023 on
    /// the lab's branch docs/riot-charging-no-dispatch).
    /// </summary>
    [Fact]
    [Contract("Q-033", "BC-ORDER-023", "#11", "ADR 0020")]
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
    [Contract("round 24", "Q-033", "#11", "ADR 0020")]
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

    /// <summary>
    /// Charging stops short of 100 %: the battery management may stop at 97 to
    /// 99 %, and the vehicle still reads CHARGING on the charger after that.
    /// </summary>
    [Fact]
    [Contract("ADR 0020", "#11")]
    [NotVerifiedOnRealRiot("电池管理可能在 97%～99% 就停充，battery 报不出 100%（推测，ADR 0020）；充满以后 batteryState 是否还报 CHARGING 待实测（#11 PR #13 审查补充第 3 条），假 RIoT 取仍报 CHARGING")]
    public async Task ChargingStopsShortOfAHundredPercentAndTheVehicleStillReadsCharging()
    {
        var control = Target.RequireControl("等车在桩上充到快满");
        var charger = RequireCharger();
        await control.SetVehicleAsync(Facts.VehicleKey, battery: 95);
        var upperId = NewUpperId("charge-full");

        await Riot.Order.CreateMoveOrderAsync(upperId, Facts.VehicleKey, Facts.MapId, charger, StartCharging, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Success);
        await UntilAsync(_ => CardAsync(), c => c.BatteryPercent >= 97, "电量涨到 97% 以上");

        await ThroughoutAsync(_ => CardAsync(), c => c is { BatteryPercent: < 100, BatteryState: "CHARGING" }, "电量不到 100%、仍报 CHARGING");
    }

    /// <summary>
    /// The charger is free once the vehicle has left it: the first reading of
    /// currentStation that is no longer the charger is 0, the vehicle on its way.
    /// </summary>
    [Fact]
    [Contract("ADR 0020", "#11", "BC-STATE-002")]
    [NotVerifiedOnRealRiot("车离开桩时 currentStation 不再是这个桩、读 0；什么时候变、会不会滞后待实测（ADR 0020；#11 PR #13 审查补充第 4 条）")]
    public async Task TheChargerIsFreeOnceTheVehicleHasLeftIt()
    {
        Target.RequireWrites();
        var charger = RequireCharger();
        var charge = NewUpperId("charge-leave");
        await Riot.Order.CreateMoveOrderAsync(charge, Facts.VehicleKey, Facts.MapId, charger, StartCharging, cancellationToken: Cancel);
        await UntilStateAsync(charge, OrderStates.Success);
        Assert.Equal(charger, await CurrentStationAsync());

        await CreateMoveAsync(NewUpperId("leave"), Facts.ReachableStation);
        var left = await UntilAsync(_ => CurrentStationAsync(), s => s != charger, "currentStation 不再是桩");

        Assert.Equal(0, left);
    }

    private async Task<int> CurrentStationAsync()
    {
        using var http = Target.CreateRawClient(withCallKey: true);
        using var body = JsonDocument.Parse(await http.GetStringAsync(new Uri($"api/task/v1/task/getVehicleInfo/{Facts.VehicleKey}", UriKind.Relative), Cancel));
        return body.RootElement.GetProperty("vehicle").GetProperty("currentStation").GetInt32();
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
