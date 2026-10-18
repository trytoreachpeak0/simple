using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Sites;

/// <summary>A stored version of the site configuration.</summary>
/// <param name="Version">1 for the first import, one more for every later version.</param>
/// <param name="Source">How it came, for example <see cref="SiteVersionSources.Import"/>.</param>
public sealed record SiteVersion(int Version, DateTimeOffset CreatedAt, string Source, SiteConfiguration Configuration);

/// <summary>Which version is current, without its content: for the status API.</summary>
public sealed record SiteVersionSummary(int Version, string Site, DateTimeOffset CreatedAt, string Source);

/// <summary>Writes versions of the site configuration to their tables and reads the current one back.</summary>
public sealed class SiteConfigurationStore(SimpleDbContext db)
{
    public Task<SiteVersionSummary?> CurrentSummaryAsync(CancellationToken cancellationToken) =>
        db.SiteVersions.AsNoTracking()
            .OrderByDescending(v => v.Id)
            .Select(v => new SiteVersionSummary(v.Id, v.Site, v.CreatedAt, v.Source))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The current configuration: the newest version; null before the first import.</summary>
    public async Task<SiteVersion?> CurrentAsync(CancellationToken cancellationToken)
    {
        var version = await db.SiteVersions.AsNoTracking().OrderByDescending(v => v.Id).FirstOrDefaultAsync(cancellationToken);
        if (version is null)
        {
            return null;
        }

        var id = version.Id;
        var vehicles = await Rows<SiteVehicleRecord>(id).OrderBy(x => x.Position).ToListAsync(cancellationToken);
        var vehicleTaskTypes = (await Rows<SiteVehicleTaskTypeRecord>(id).OrderBy(x => x.Position).ToListAsync(cancellationToken)).ToLookup(x => x.VehicleId);
        var groups = (await Rows<SiteVehicleSlotGroupRecord>(id).OrderBy(x => x.Position).ToListAsync(cancellationToken)).ToLookup(x => x.VehicleId);
        var slots = (await Rows<SiteVehicleSlotRecord>(id).OrderBy(x => x.Position).ToListAsync(cancellationToken)).ToLookup(x => x.VehicleId);
        var chargers = (await Rows<SiteVehicleChargerRecord>(id).OrderBy(x => x.Position).ToListAsync(cancellationToken)).ToLookup(x => x.VehicleId);

        var configuration = new SiteConfiguration
        {
            Site = version.Site,
            Description = version.Description,
            Parameters = version.Parameters,
            Projects = await Rows<SiteProjectRecord>(id).OrderBy(x => x.Position).Select(x => new Project(x.Code, x.Name)).ToListAsync(cancellationToken),
            Zones = await Rows<SiteZoneRecord>(id).OrderBy(x => x.Position)
                .Select(x => new DispatchZone(x.Code, x.Name, x.Project, x.DemandOrder, x.WaitCap)).ToListAsync(cancellationToken),
            Areas = await Rows<SiteAreaRecord>(id).OrderBy(x => x.Position)
                .Select(x => new AreaSetting(x.Area, x.Zone, x.DoorSide, x.NotServed)).ToListAsync(cancellationToken),
            TaskTypes = await Rows<SiteTaskTypeRecord>(id).OrderBy(x => x.Position)
                .Select(x => new TaskType(x.Code, x.Name, x.Pickup, x.Unload)).ToListAsync(cancellationToken),
            FixedStations = await Rows<SiteFixedStationRecord>(id).OrderBy(x => x.Position)
                .Select(x => new FixedStation(x.Project, x.TaskType, new StationRef(x.Map, x.Station), x.AllLoaded)).ToListAsync(cancellationToken),
            Vehicles =
            [
                .. vehicles.Select(v => new Vehicle(
                    v.VehicleId,
                    v.RiotDeviceKey,
                    v.Project,
                    v.Zone,
                    [.. vehicleTaskTypes[v.VehicleId].Select(t => t.TaskType)],
                    [.. groups[v.VehicleId].Select(g => new SlotGroup(g.Name, [.. slots[v.VehicleId].Where(s => s.Group == g.Name).Select(s => s.Slot)]))],
                    [.. chargers[v.VehicleId].Select(c => new StationRef(c.Map, c.Station))],
                    new StationRef(v.WaitingMap, v.WaitingStation),
                    [.. slots[v.VehicleId].OrderBy(s => s.Slot).Select(s => new SlotIo(s.Slot, Point(s.Lock), Point(s.LightCurtain), Point(s.Unlock)))])),
            ],
        };
        // Switches in the order of their projects, then of their task types.
        var switches = await Rows<SiteTaskTypeSwitchRecord>(id).Select(x => new TaskTypeSwitch(x.Project, x.TaskType, x.Enabled)).ToListAsync(cancellationToken);
        var projectOrder = configuration.Projects.Select((p, i) => (p.Code, i)).ToDictionary(x => x.Code, x => x.i);
        var taskTypeOrder = configuration.TaskTypes.Select((t, i) => (t.Code, i)).ToDictionary(x => x.Code, x => x.i);
        configuration = configuration with
        {
            TaskTypeSwitches = [.. switches.OrderBy(s => projectOrder.GetValueOrDefault(s.Project)).ThenBy(s => taskTypeOrder.GetValueOrDefault(s.TaskType))],
        };
        return new SiteVersion(version.Id, version.CreatedAt, version.Source, configuration);
    }

    /// <summary>
    /// Adds a version and returns its number. Saves within the context's
    /// transaction when there is one. The configuration must be valid
    /// (<see cref="SiteValidator"/>): every slot has exactly one row of the IO
    /// point map, which is stored with it.
    /// </summary>
    public async Task<int> AddAsync(SiteConfiguration configuration, string source, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var version = new SiteVersionRecord
        {
            CreatedAt = createdAt,
            Site = configuration.Site,
            Description = configuration.Description,
            Source = source,
            Parameters = configuration.Parameters,
        };
        db.SiteVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        var id = version.Id;
        db.AddRange(configuration.Projects.Select((p, i) => new SiteProjectRecord { VersionId = id, Position = i, Code = p.Code, Name = p.Name }));
        db.AddRange(configuration.Zones.Select((z, i) => new SiteZoneRecord
        {
            VersionId = id, Position = i, Code = z.Code, Name = z.Name, Project = z.Project, DemandOrder = z.DemandOrder, WaitCap = z.WaitCap,
        }));
        db.AddRange(configuration.Areas.Select((a, i) => new SiteAreaRecord
        {
            VersionId = id, Position = i, Area = a.Area, Zone = a.Zone, DoorSide = a.DoorSide, NotServed = a.NotServed,
        }));
        db.AddRange(configuration.TaskTypes.Select((t, i) => new SiteTaskTypeRecord
        {
            VersionId = id, Position = i, Code = t.Code, Name = t.Name, Pickup = t.Pickup, Unload = t.Unload,
        }));
        db.AddRange(configuration.TaskTypeSwitches.Select(s => new SiteTaskTypeSwitchRecord
        {
            VersionId = id, Project = s.Project, TaskType = s.TaskType, Enabled = s.Enabled,
        }));
        db.AddRange(configuration.FixedStations.Select((f, i) => new SiteFixedStationRecord
        {
            VersionId = id, Position = i, Project = f.Project, TaskType = f.TaskType, Map = f.Station.Map, Station = f.Station.Station, AllLoaded = f.AllLoaded,
        }));
        foreach (var (v, i) in configuration.Vehicles.Select((v, i) => (v, i)))
        {
            db.Add(new SiteVehicleRecord
            {
                VersionId = id,
                Position = i,
                VehicleId = v.Id,
                RiotDeviceKey = v.RiotDeviceKey,
                Project = v.Project,
                Zone = v.Zone,
                WaitingMap = v.WaitingPoint.Map,
                WaitingStation = v.WaitingPoint.Station,
            });
            db.AddRange(v.TaskTypes.Select((t, j) => new SiteVehicleTaskTypeRecord { VersionId = id, VehicleId = v.Id, Position = j, TaskType = t }));
            db.AddRange(v.SlotGroups.Select((g, j) => new SiteVehicleSlotGroupRecord { VersionId = id, VehicleId = v.Id, Position = j, Name = g.Name }));
            var io = v.IoPoints.ToDictionary(x => x.Slot);
            db.AddRange(v.SlotGroups.SelectMany(g => g.Slots.Select((slot, j) => new SiteVehicleSlotRecord
            {
                VersionId = id,
                VehicleId = v.Id,
                Slot = slot,
                Group = g.Name,
                Position = j,
                Lock = io[slot].Lock.ToString(),
                LightCurtain = io[slot].LightCurtain.ToString(),
                Unlock = io[slot].Unlock.ToString(),
            })));
            db.AddRange(v.Chargers.Select((c, j) => new SiteVehicleChargerRecord { VersionId = id, VehicleId = v.Id, Position = j, Map = c.Map, Station = c.Station }));
        }

        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    private IQueryable<T> Rows<T>(int version)
        where T : class =>
        db.Set<T>().AsNoTracking().Where(x => EF.Property<int>(x, "VersionId") == version);

    private static IoPoint Point(string text) =>
        IoPoint.Parse(text) ?? throw new InvalidDataException($"The IO point '{text}' in the database is not DI<n> or DO<n>.");
}
