namespace Simple.Server.Riot;

/// <summary>
/// The fleet manager's maps as the server reads them: every station of every
/// map, with the areas the station serves (ADR 0002). Stations are named by
/// map and number together, because numbers repeat across maps (ADR 0015).
/// </summary>
public sealed record FleetMaps(IReadOnlyList<FleetMap> Maps)
{
    public FleetMap? Map(int id) => Maps.FirstOrDefault(m => m.Id == id);

    public FleetStation? Station(int map, int station) => Map(map)?.Stations.FirstOrDefault(s => s.Id == station);
}

public sealed record FleetMap(int Id, string Name, IReadOnlyList<FleetStation> Stations);

/// <param name="Areas">The areas the station declares it serves; empty for a station that serves no machine.</param>
public sealed record FleetStation(int Id, string Name, IReadOnlyList<string> Areas);
