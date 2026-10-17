using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>Route costs and nearest-station queries. Read only.</summary>
public sealed class RouteContracts : ContractTest
{
    [Fact]
    [Contract("BC-ROUTE-001")]
    public async Task AReachableStationHasACostInMillimetres()
    {
        var cost = await Riot.Tasks.GetRouteCostAsync(Facts.MapId, Facts.ReachableStation, Facts.VehicleKey, Cancel);

        Assert.True(cost.CostsMm > 0, $"cost {cost.CostsMm}");
    }

    /// <summary>Unreachable is the result -1 inside a successful answer, not a failed call.</summary>
    [Fact]
    [Contract("BC-ROUTE-001")]
    public async Task AnUnreachableStationCostsMinusOneInASuccessfulAnswer()
    {
        if (Facts.UnreachableStation is not { } unreachable)
        {
            Assert.Skip("设置文件没有给出不可达的站点（unreachableStation）。");
            return;
        }

        var cost = await Riot.Tasks.GetRouteCostAsync(Facts.MapId, unreachable, Facts.VehicleKey, Cancel);

        Assert.Equal(-1, cost.CostsMm);
    }

    [Fact]
    [Contract("BC-ROUTE-001")]
    public async Task AVehicleOnAnotherMapCostsMinusOne()
    {
        var other = (await Riot.Maps.ListMapsAsync(Cancel)).First(m => m.MapId != Facts.MapId);
        var stations = await Riot.Maps.ListStationsAsync(other.MapId, Cancel);
        var station = stations.Count > 0 ? stations[0] : null;
        if (station is null)
        {
            Assert.Skip($"地图 {other.MapId} 上没有站点。");
            return;
        }

        var cost = await Riot.Tasks.GetRouteCostAsync(other.MapId, station.StationId, Facts.VehicleKey, Cancel);

        Assert.Equal(-1, cost.CostsMm);
    }

    [Fact]
    [Contract("BC-MAP-003")]
    public async Task TheNearestEndAmongReachableCandidatesIsTheCheapest()
    {
        var near = await Riot.Tasks.GetRouteCostAsync(Facts.MapId, Facts.ReachableStation, Facts.VehicleKey, Cancel);
        var far = await Riot.Tasks.GetRouteCostAsync(Facts.MapId, Facts.SecondReachableStation, Facts.VehicleKey, Cancel);
        var expected = near.CostsMm <= far.CostsMm ? Facts.ReachableStation : Facts.SecondReachableStation;

        var nearest = await Riot.Tasks.QueryNearestEndAsync(Facts.MapId, Facts.VehicleStation, [Facts.ReachableStation, Facts.SecondReachableStation], Cancel);

        Assert.Equal(expected, nearest);
    }

    /// <summary>One unreachable candidate makes the whole query a NullPointerException, 00002: it is not skipped.</summary>
    [Fact]
    [Contract("BC-ROUTE-002")]
    public async Task OneUnreachableCandidateFailsTheWholeNearestQuery()
    {
        if (Facts.UnreachableStation is not { } unreachable)
        {
            Assert.Skip("设置文件没有给出不可达的站点（unreachableStation）。");
            return;
        }

        var refused = await Assert.ThrowsAsync<RiotApiException>(
            () => Riot.Tasks.QueryNearestEndAsync(Facts.MapId, Facts.VehicleStation, [Facts.ReachableStation, unreachable], Cancel));

        Assert.Equal("00002", refused.BusinessCode);
    }
}
