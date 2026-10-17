using System.Text.Json;

namespace Simple.Contracts.Tests;

/// <summary>
/// Who stands on a station or is heading for it, and whether a station keeps
/// its number. SIMPLE names fixed stations by map and station number
/// (ADR 0015) and frees a charger once its vehicle has left it (ADR 0020).
/// </summary>
public sealed class StationContracts : OrderContractTest
{
    /// <summary>
    /// RIoT has no "who is on this station" query: a caller walks every
    /// vehicle and reads its card. Read only, so it also runs against real RIoT.
    /// </summary>
    [Fact]
    [Contract("ADR 0020", "#11")]
    [NotVerifiedOnRealRiot("要判断别的车有没有停在某个站点上、或正开往这个站点，只能遍历全部车辆和订单（产品负责人记得上一代调研过；ADR 0020）")]
    public async Task EveryVehicleOnTheListHasACardThatSaysWhereItStands()
    {
        var vehicles = await Riot.Tasks.GetDispatchableVehiclesAsync(Cancel);

        Assert.Contains(vehicles, v => v.DeviceKey == Facts.VehicleKey);
        foreach (var vehicle in vehicles)
        {
            var card = await Riot.Tasks.GetVehicleCardAsync(vehicle.DeviceKey, Cancel);
            Assert.Equal(vehicle.DeviceKey, card.DeviceKey);
            Assert.NotNull(card.CurrentPosition);
        }
    }

    /// <summary>Where a vehicle is heading is read from its executing order: the list gives the order, the detail its end station.</summary>
    [Fact]
    [Contract("ADR 0020", "#11", "BC-ORDER-013")]
    [NotVerifiedOnRealRiot("要判断别的车有没有正开往某个站点，只能遍历全部订单（产品负责人记得上一代调研过；ADR 0020）")]
    public async Task TheOrderListShowsWhereAnExecutingVehicleIsHeading()
    {
        Target.RequireWrites();
        await ExecutingAsync("heading");
        // Keep it executing while it is read: a software stop holds the
        // vehicle and leaves the order EXECUTING (BC-ORDER-015), where a fast
        // order would otherwise be done before the list is read.
        await Riot.Device.TriggerEmergencyStopAsync(Facts.VehicleKey, Cancel);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "急停锁上");

        var page = await Riot.Order.ListOrdersByStatesAsync([OrderStates.Executing], 1, 100, Cancel);
        var record = Assert.Single(page.Records, r => r.ExecuteVehicleKey == Facts.VehicleKey);
        var order = await ReadAsync(record.UpperId!);

        Assert.Equal(Facts.ReachableStation, order.EndStationNo);
    }

    /// <summary>
    /// A station keeps its number for good: a new station gets a number no
    /// station had before, even when the highest one was deleted.
    /// </summary>
    [Fact]
    [Contract("ADR 0015", "#11")]
    [NotVerifiedOnRealRiot("站点的站号永不改变：新建站点只递增，删掉的站号不会再分给别的站点（产品负责人 2026-10-16，ADR 0015）")]
    public async Task ANewStationGetsANumberNoStationHadBefore()
    {
        var control = Target.RequireControl("在 RIoT 的地图编辑里删掉一个站点、再新建一个");
        var before = (await Riot.Maps.ListStationsStrictAsync(Facts.MapId, Cancel)).Select(s => s.StationId).ToList();
        var highest = before.Max();

        await control.DeleteStationAsync(Facts.MapId, highest);
        var added = await control.AddStationAsync(Facts.MapId, "contract-new-station");
        var after = (await Riot.Maps.ListStationsStrictAsync(Facts.MapId, Cancel)).Select(s => s.StationId).ToList();

        Assert.True(added > highest, $"新站点的站号是 {added}，不比删掉的最大站号 {highest} 大。");
        Assert.DoesNotContain(highest, after);
        Assert.Contains(added, after);
        Assert.All(before.Where(id => id != highest), id => Assert.Contains(id, after));
    }

    /// <summary>
    /// Cancelling an order while the vehicle moves stops it where it is: off
    /// any station, at speed 0, not back at the start. The product owner says
    /// it stops hard; the fake shows only where it ends up.
    /// </summary>
    [Fact]
    [Contract("ADR 0021", "#11", "BC-ORDER-003")]
    [NotVerifiedOnRealRiot("取消执行中的订单，车正在移动时会猛停、停在原地（产品负责人，ADR 0021；实验室第 9 轮只有取消后的一个样本，没有速度随时间的记录）")]
    public async Task CancellingAMovingOrderStopsTheVehicleWhereItIs()
    {
        Target.RequireWrites();
        var start = (await Riot.Maps.ListStationDetailsAsync(Facts.MapId, Cancel)).Single(s => s.StationId == Facts.VehicleStation);
        var (upperId, order) = await ExecutingAsync("cancel-moving");
        await UntilAsync(_ => RawVehicleAsync(), v => v is { Station: 0, Speed: > 0 } && (v.X, v.Y) != (start.PosX, start.PosY), "车在路上");

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Cancelled);
        var stopped = await RawVehicleAsync();

        Assert.NotEqual((start.PosX, start.PosY), (stopped.X, stopped.Y));
        await ThroughoutAsync(_ => RawVehicleAsync(), v => v == stopped, "停在原地、不在站上、车速 0");
        Assert.Equal((0, 0d), (stopped.Station, stopped.Speed));
    }

    private async Task<(int Station, double Speed, double X, double Y)> RawVehicleAsync()
    {
        using var http = Target.CreateRawClient(withCallKey: true);
        using var body = JsonDocument.Parse(await http.GetStringAsync(new Uri($"api/task/v1/task/getVehicleInfo/{Facts.VehicleKey}", UriKind.Relative), Cancel));
        var vehicle = body.RootElement.GetProperty("vehicle");
        var position = vehicle.GetProperty("precisePosition");
        return (vehicle.GetProperty("currentStation").GetInt32(), vehicle.GetProperty("speed").GetDouble(), position.GetProperty("x").GetDouble(), position.GetProperty("y").GetDouble());
    }
}
