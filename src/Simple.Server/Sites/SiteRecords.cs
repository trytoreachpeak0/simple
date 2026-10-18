namespace Simple.Server.Sites;

// The site configuration's tables. Every version is a whole copy of the
// configuration: the rows of one version share its VersionId, and the
// current configuration is the version with the highest id. Rows keep the
// order the configuration lists them in (Position). Nothing here is ever
// changed or deleted; a change will be a new version (ADR 0017).

/// <summary>One version of the site configuration, with its parameters as columns.</summary>
public sealed class SiteVersionRecord
{
    /// <summary>The row's own key, assigned by the database; the version's rows refer to it. Not the version number: it may skip.</summary>
    public int Id { get; private set; }

    /// <summary>The version number people see: 1 for the first import, then one more each time, without gaps.</summary>
    public int Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public required string Site { get; init; }

    public string? Description { get; init; }

    /// <summary>How it came: <see cref="SiteVersionSources.Import"/>.</summary>
    public required string Source { get; init; }

    public required SiteParameters Parameters { get; init; }
}

public static class SiteVersionSources
{
    /// <summary>Imported from <c>sites/&lt;site&gt;/site.yaml</c> at first install.</summary>
    public const string Import = "import";
}

public sealed class SiteProjectRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string Code { get; init; }

    public string? Name { get; init; }
}

public sealed class SiteZoneRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public required string Project { get; init; }

    public DemandOrderPolicy DemandOrder { get; init; }

    public TimeSpan WaitCap { get; init; }
}

public sealed class SiteAreaRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string Area { get; init; }

    public string? Zone { get; init; }

    public string? DoorSide { get; init; }

    public bool NotServed { get; init; }
}

public sealed class SiteTaskTypeRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public TaskEnd Pickup { get; init; }

    public TaskEnd Unload { get; init; }
}

public sealed class SiteTaskTypeSwitchRecord
{
    public int VersionId { get; init; }

    public required string Project { get; init; }

    public required string TaskType { get; init; }

    public bool Enabled { get; init; }
}

public sealed class SiteFixedStationRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string Project { get; init; }

    public required string TaskType { get; init; }

    public int Map { get; init; }

    public int Station { get; init; }

    public TimeSpan? AllLoaded { get; init; }
}

public sealed class SiteVehicleRecord
{
    public int VersionId { get; init; }

    public int Position { get; init; }

    public required string VehicleId { get; init; }

    public required string RiotDeviceKey { get; init; }

    public required string Project { get; init; }

    public required string Zone { get; init; }

    public int WaitingMap { get; init; }

    public int WaitingStation { get; init; }
}

public sealed class SiteVehicleTaskTypeRecord
{
    public int VersionId { get; init; }

    public required string VehicleId { get; init; }

    public int Position { get; init; }

    public required string TaskType { get; init; }
}

public sealed class SiteVehicleSlotGroupRecord
{
    public int VersionId { get; init; }

    public required string VehicleId { get; init; }

    public int Position { get; init; }

    public required string Name { get; init; }
}

/// <summary>A slot: its group, its place in the group and its row of the IO point map (ADR 0024).</summary>
public sealed class SiteVehicleSlotRecord
{
    public int VersionId { get; init; }

    public required string VehicleId { get; init; }

    public int Slot { get; init; }

    public required string Group { get; init; }

    public int Position { get; init; }

    /// <summary>For example <c>DI1</c>.</summary>
    public required string Lock { get; init; }

    public required string LightCurtain { get; init; }

    /// <summary>For example <c>DO1</c>.</summary>
    public required string Unlock { get; init; }
}

public sealed class SiteVehicleChargerRecord
{
    public int VersionId { get; init; }

    public required string VehicleId { get; init; }

    public int Position { get; init; }

    public int Map { get; init; }

    public int Station { get; init; }
}
