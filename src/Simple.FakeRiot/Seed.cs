using System.Text.Json;

namespace Simple.FakeRiot;

/// <summary>
/// What the fake starts with. The seed's own format, not RIoT's: the fake
/// derives RIoT's wire shapes (snake_case edges, dotted station keys) from it.
/// Node ids are station ids; an edge runs from one station to another.
/// </summary>
public sealed record Seed(IReadOnlyList<SeedMap> Maps, IReadOnlyList<SeedVehicle> Vehicles)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static Seed Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Seed>(stream, Options)
            ?? throw new InvalidDataException($"The seed file '{path}' is empty.");
    }
}

/// <param name="Type">RIoT's station type; 2 is a charging station.</param>
public sealed record SeedStation(int Id, string Name, int Type, double X, double Y);

public sealed record SeedEdge(int Id, int From, int To);

public sealed record SeedMap(int Id, string Name, IReadOnlyList<SeedStation> Stations, IReadOnlyList<SeedEdge> Edges);

public sealed record SeedVehicle(string DeviceKey, string Name, int VehicleId, int MapId, int Station, int Battery);
