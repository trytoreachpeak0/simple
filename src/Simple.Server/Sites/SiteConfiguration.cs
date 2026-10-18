namespace Simple.Server.Sites;

/// <summary>
/// A site's whole parameter configuration (ADR 0017, docs/rules-overview.md
/// §13): projects and their dispatch zones, area settings, task types and
/// their per-project switches, fixed stations, the vehicle list and the
/// durations, counts and battery levels of summary table two. The server
/// holds the live one; <c>sites/&lt;site&gt;/site.yaml</c> is only where a site
/// starts. Vehicle enabling, slot disabling and charger disabling are switches
/// people press, not configuration, and are not here (ADR 0011, 0012, 0020).
/// Endpoints and credentials are never here either.
/// </summary>
public sealed record SiteConfiguration
{
    /// <summary>Which site it is, for example <c>sample</c> or <c>d8</c>.</summary>
    public required string Site { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<Project> Projects { get; init; } = [];

    public IReadOnlyList<DispatchZone> Zones { get; init; } = [];

    public IReadOnlyList<AreaSetting> Areas { get; init; } = [];

    public IReadOnlyList<TaskType> TaskTypes { get; init; } = [];

    /// <summary>
    /// Every project's switch for every task type (ADR 0017). An import turns
    /// them all off; a pair without a row is off too.
    /// </summary>
    public IReadOnlyList<TaskTypeSwitch> TaskTypeSwitches { get; init; } = [];

    public IReadOnlyList<FixedStation> FixedStations { get; init; } = [];

    public IReadOnlyList<Vehicle> Vehicles { get; init; } = [];

    public SiteParameters Parameters { get; init; } = new();

    /// <summary>Whether a project's task type is switched on.</summary>
    public bool IsEnabled(string project, string taskType) =>
        TaskTypeSwitches.Any(s => s.Project == project && s.TaskType == taskType && s.Enabled);

    /// <summary>The same configuration with every project's every task type switched off, as a first import leaves it (ADR 0017).</summary>
    public SiteConfiguration WithEverySwitchOff() => this with
    {
        TaskTypeSwitches = [.. Projects.SelectMany(p => TaskTypes.Select(t => new TaskTypeSwitch(p.Code, t.Code, false)))],
    };
}

/// <summary>A group of vehicles and dispatch zones that one team of team leaders and engineers looks after (ADR 0015).</summary>
/// <param name="Code">How accounts and the vehicle overview's address name it.</param>
/// <param name="Name">Its Chinese name for people; optional.</param>
public sealed record Project(string Code, string? Name);

/// <summary>A pickup range of one project (ADR 0015, 0023, 0036, 0038).</summary>
/// <param name="Code">For example <c>WIRE_BONDING</c>.</param>
/// <param name="Name">Its Chinese name, for example 焊线区 (ADR 0038).</param>
/// <param name="WaitCap">T of <see cref="DemandOrderPolicy.NearestFirstWithWaitCap"/>; the zone keeps it whatever its policy (ADR 0036).</param>
public sealed record DispatchZone(string Code, string Name, string Project, DemandOrderPolicy DemandOrder, TimeSpan WaitCap)
{
    /// <summary>T's default: 15 minutes (ADR 0036).</summary>
    public static readonly TimeSpan DefaultWaitCap = TimeSpan.FromMinutes(15);
}

/// <summary>Which demand a vehicle takes first when it could take several (ADR 0036).</summary>
public enum DemandOrderPolicy
{
    FirstComeFirstServed,

    /// <summary>The default.</summary>
    NearestFirst,

    NearestFirstWithWaitCap,

    ShortestOverall,
}

/// <summary>
/// One area's setting (ADR 0002, 0006, 0015). Which station serves the area
/// is read from the map, never set here (ADR 0002). An area without a zone or
/// without a door side is not dispatched to; that is an alarm at run time, not
/// a validation failure (ADR 0006, 0015).
/// </summary>
/// <param name="Area">The area as the MES writes it, trimmed (ADR 0002).</param>
/// <param name="DoorSide">The slot group its tasks use, by name (ADR 0006).</param>
/// <param name="NotServed">Nothing is dispatched to it (ADR 0002, 0017).</param>
public sealed record AreaSetting(string Area, string? Zone, string? DoorSide, bool NotServed);

/// <summary>A kind of transport and its two ends (ADR 0006, 0038; CONTEXT.md "Task type").</summary>
/// <param name="Code">For example <c>STAGING_TO_WIRE</c>; what the MES gateway names.</param>
/// <param name="Name">Its Chinese name, the only one the onboard screen shows (ADR 0038).</param>
public sealed record TaskType(string Code, string Name, TaskEnd Pickup, TaskEnd Unload);

/// <summary>What one end of a task type is.</summary>
public enum TaskEnd
{
    /// <summary>A machine, found through its area (ADR 0002).</summary>
    Machine,

    /// <summary>The project's fixed station for the task type (ADR 0015).</summary>
    FixedStation,
}

public sealed record TaskTypeSwitch(string Project, string TaskType, bool Enabled);

/// <summary>A station, always named with its map: station numbers repeat across maps (ADR 0015).</summary>
public sealed record StationRef(int Map, int Station)
{
    public override string ToString() => $"地图 {Map} 站 {Station}";
}

/// <summary>
/// A project's fixed station for one task type on one map (ADR 0015), with
/// the optional "all loaded" countdown that applies while a vehicle loads
/// there (ADR 0035).
/// </summary>
public sealed record FixedStation(string Project, string TaskType, StationRef Station, TimeSpan? AllLoaded);

/// <summary>One entry of the vehicle list (ADR 0006, 0007, 0015, 0020, 0021, 0023, 0024).</summary>
/// <param name="Id">The vehicle ID SIMPLE assigned, which never changes (ADR 0007).</param>
/// <param name="RiotDeviceKey">Which vehicle it is in the fleet manager now (ADR 0007).</param>
/// <param name="TaskTypes">The task types it takes (ADR 0023).</param>
/// <param name="SlotGroups">Its slot groups in order, each with its slots in order (ADR 0006).</param>
/// <param name="Chargers">The chargers it may use, first choice first (ADR 0020).</param>
/// <param name="WaitingPoint">Its own waiting point (ADR 0021).</param>
/// <param name="IoPoints">Which IO module points each slot's lock, light curtain and unlock are wired to (ADR 0024).</param>
public sealed record Vehicle(
    string Id,
    string RiotDeviceKey,
    string Project,
    string Zone,
    IReadOnlyList<string> TaskTypes,
    IReadOnlyList<SlotGroup> SlotGroups,
    IReadOnlyList<StationRef> Chargers,
    StationRef WaitingPoint,
    IReadOnlyList<SlotIo> IoPoints)
{
    /// <summary>Every slot, in the vehicle list's order: group by group.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<int> Slots => SlotGroups.SelectMany(g => g.Slots);
}

/// <summary>Slots that open to the same side, in order (CONTEXT.md "Slot group").</summary>
public sealed record SlotGroup(string Name, IReadOnlyList<int> Slots);

/// <summary>One slot's row of the IO point map (ADR 0024): two inputs and an output.</summary>
public sealed record SlotIo(int Slot, IoPoint Lock, IoPoint LightCurtain, IoPoint Unlock);

/// <summary>An IO module channel as the onboard protocol numbers it: <c>DI1</c>… for inputs, <c>DO1</c>… for outputs.</summary>
public readonly record struct IoPoint(IoPointKind Kind, int Number)
{
    public override string ToString() => $"{(Kind == IoPointKind.Input ? "DI" : "DO")}{Number}";

    /// <summary>Reads <c>DI3</c> or <c>DO3</c>; null for anything else.</summary>
    public static IoPoint? Parse(string? text)
    {
        if (text is null || text.Length < 3)
        {
            return null;
        }

        var kind = text[..2].ToUpperInvariant() switch
        {
            "DI" => IoPointKind.Input,
            "DO" => IoPointKind.Output,
            _ => (IoPointKind?)null,
        };
        return kind is { } k
            && int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 1
            ? new IoPoint(k, number)
            : null;
    }
}

public enum IoPointKind
{
    Input,
    Output,
}
