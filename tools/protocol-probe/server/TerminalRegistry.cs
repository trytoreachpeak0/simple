using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simple.ProtocolProbe.Server;

internal enum TerminalState
{
    Pending,
    Confirmed,
    Revoked,
    CloneSuspected,
}

/// <summary>One terminal key the server has seen. <see cref="Key"/> is the hex
/// SHA-256 of the client certificate's SubjectPublicKeyInfo (public).</summary>
internal sealed class TerminalEntry
{
    public required string Key { get; init; }
    public TerminalState State { get; set; }
    public string? VehicleId { get; set; }
    public string? PairingCode { get; set; }
}

/// <summary>
/// Which terminal keys are pending, confirmed, revoked or suspected cloned.
/// Persisted to a JSON file in the data directory so that a server restart
/// (step 6) still recognises confirmed terminals. Callers hold the hub lock.
/// </summary>
internal sealed class TerminalRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;
    private readonly Dictionary<string, TerminalEntry> entries;

    public TerminalRegistry(string path)
    {
        this.path = path;
        entries = File.Exists(path)
            ? JsonSerializer.Deserialize<List<TerminalEntry>>(File.ReadAllText(path), Json)!.ToDictionary(e => e.Key)
            : [];
    }

    public IReadOnlyCollection<TerminalEntry> All => entries.Values;

    public TerminalEntry? Find(string key) => entries.GetValueOrDefault(key);

    public TerminalEntry? FindByPairingCode(string code) =>
        entries.Values.FirstOrDefault(e => e.State == TerminalState.Pending && e.PairingCode == code);

    /// <summary>A vehicle id matches only registrations that are not revoked;
    /// otherwise the text is taken as a key prefix. Returns null and a reason
    /// when nothing or more than one entry matches.</summary>
    public TerminalEntry? Match(string text, out string? problem)
    {
        var byVehicle = entries.Values.Where(e => e.VehicleId == text && e.State != TerminalState.Revoked).ToList();
        var candidates = byVehicle.Count > 0
            ? byVehicle
            : entries.Values.Where(e => e.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
        problem = candidates.Count switch
        {
            0 => $"no terminal matches {text}",
            1 => null,
            _ => $"{text} matches {candidates.Count} terminals ({string.Join(", ", candidates.Select(e => e.Key[..12]))}); give a longer key prefix",
        };
        return problem is null ? candidates[0] : null;
    }

    public void Add(TerminalEntry entry)
    {
        entries[entry.Key] = entry;
        Save();
    }

    public void Remove(string key)
    {
        entries.Remove(key);
        Save();
    }

    public void Save() => File.WriteAllText(path, JsonSerializer.Serialize(entries.Values.ToList(), Json));
}
