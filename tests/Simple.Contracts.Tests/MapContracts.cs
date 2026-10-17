namespace Simple.Contracts.Tests;

/// <summary>Reading maps, stations and the route graph. Read only: these run against real RIoT too.</summary>
public sealed class MapContracts : ContractTest
{
    [Fact]
    [Contract("BC-MAP-001")]
    public async Task TheMapListNamesTheVehiclesMapByItsId()
    {
        var maps = await Riot.Maps.ListMapsAsync(Cancel);

        Assert.Contains(maps, m => m.MapId == Facts.MapId && !string.IsNullOrWhiteSpace(m.Name));
    }

    [Fact]
    [Contract("BC-MAP-002")]
    public async Task TheStationsOfTheVehiclesMapIncludeTheStationsTheContractsUse()
    {
        var stations = await Riot.Maps.ListStationsStrictAsync(Facts.MapId, Cancel);

        Assert.Contains(stations, s => s.StationId == Facts.VehicleStation);
        Assert.Contains(stations, s => s.StationId == Facts.ReachableStation);
    }

    /// <summary>An unknown map is a successful answer with no stations, not an error: an empty catalogue proves nothing.</summary>
    [Fact]
    [Contract("BC-MAP-002")]
    public async Task AnUnknownMapAnswersSuccessWithNoStations()
    {
        var stations = await Riot.Maps.ListStationsAsync(0, Cancel);

        Assert.Empty(stations);
    }

    /// <summary>Station ids repeat across maps, so a station is always named with its map.</summary>
    [Fact]
    [Contract("BC-MAP-002")]
    public async Task TwoMapsHaveAStationWithTheSameId()
    {
        if (Facts.MapsSharingAStationId is not var (first, second) || Facts.SharedStationId is not { } shared)
        {
            Assert.Skip("设置文件没有给出两个有同号站点的地图（mapsSharingAStationId、sharedStationId）。");
            return;
        }

        var onFirst = await Riot.Maps.ListStationsAsync(first, Cancel);
        var onSecond = await Riot.Maps.ListStationsAsync(second, Cancel);

        Assert.Contains(onFirst, s => s.StationId == shared);
        Assert.Contains(onSecond, s => s.StationId == shared);
    }

    /// <summary>
    /// The edge table is snake_case (<c>s_node</c>), which the generated client
    /// cannot read, and directed: some edges have no reverse twin.
    /// </summary>
    [Fact]
    [Contract("BC-MAP-003")]
    public async Task TheEdgeTableIsReadableAndDirected()
    {
        var edges = await Riot.Maps.ListEdgesAsync(Facts.MapId, Cancel);

        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.True(e.StartNode > 0 && e.EndNode > 0 && e.CostMm > 0, $"edge {e.Id}: {e}"));
        Assert.Contains(edges, e => !edges.Any(r => r.StartNode == e.EndNode && r.EndNode == e.StartNode));
    }

    [Fact]
    [Contract("BC-MAP-003")]
    public async Task EveryStationSitsOnAnEdgeOfItsMap()
    {
        var edges = await Riot.Maps.ListEdgesAsync(Facts.MapId, Cancel);
        var stations = await Riot.Maps.ListStationDetailsAsync(Facts.MapId, Cancel);

        Assert.All(stations, s => Assert.Contains(edges, e => e.Id == s.EdgeId));
        Assert.Empty(await Riot.Maps.ListRemovedEdgesAsync(Facts.MapId, Cancel));
    }
}
