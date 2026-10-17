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

    /// <summary>The edge table is snake_case (<c>s_node</c>), which the generated client cannot read.</summary>
    [Fact]
    [Contract("BC-MAP-003")]
    public async Task TheEdgeTableIsReadable()
    {
        var edges = await Riot.Maps.ListEdgesAsync(Facts.MapId, Cancel);

        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.True(e.StartNode > 0 && e.EndNode > 0 && e.CostMm > 0, $"edge {e.Id}: {e}"));
    }

    /// <summary>
    /// The graph is directed: on map25 only 148 of 403 edges have a reverse
    /// twin. Whether a given real map has a one-way edge is the map's state,
    /// not RIoT's behaviour, so only the fake's map is held to it.
    /// </summary>
    [Fact]
    [Contract("BC-MAP-003")]
    public async Task TheFakesMapHasOneWayEdges()
    {
        Assert.SkipUnless(Target.Control is not null, "现场地图有没有单向边是地图本身的情况，不是 RIoT 的行为；实验室只在 map25 上数过。");
        var edges = await Riot.Maps.ListEdgesAsync(Facts.MapId, Cancel);

        Assert.Contains(edges, e => !edges.Any(r => r.StartNode == e.EndNode && r.EndNode == e.StartNode));
    }

    [Fact]
    [Contract("BC-MAP-003")]
    public async Task EveryStationSitsOnAnEdgeOfItsMap()
    {
        var edges = await Riot.Maps.ListEdgesAsync(Facts.MapId, Cancel);
        var stations = await Riot.Maps.ListStationDetailsAsync(Facts.MapId, Cancel);

        Assert.All(stations, s => Assert.Contains(edges, e => e.Id == s.EdgeId));
    }

    /// <summary>The removed edges and stations read, empty or not (map25 had none in round 43; that is its state, not a rule).</summary>
    [Fact]
    [Contract("BC-MAP-003")]
    public async Task RemovedEdgesAndStationsAreReadable()
    {
        var removedEdges = await Riot.Maps.ListRemovedEdgesAsync(Facts.MapId, Cancel);
        var removedStations = await Riot.Maps.ListRemovedStationsAsync(Facts.MapId, Cancel);

        Assert.All(removedEdges, r => Assert.True(r.EdgeId > 0));
        Assert.All(removedStations, r => Assert.True(r.StationId > 0));
    }
}
