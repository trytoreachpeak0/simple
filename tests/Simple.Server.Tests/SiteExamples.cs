using Simple.Server.Riot;
using Simple.Server.Sites;

namespace Simple.Server.Tests;

/// <summary>
/// A small valid site and the map it is valid against, for tests that change
/// one thing and look for the one problem. Project P has dispatch zone Z with
/// areas A-01 and A-02 (machine stations 1 and 2 on map 1); TO_OVEN goes from a
/// machine to the oven (station 20), FROM_STAGING from the staging area
/// (station 21) to a machine. Vehicle v1 takes TO_OVEN, v2 FROM_STAGING; both
/// charge at station 10 and wait at 11 and 12.
/// </summary>
public static class SiteExamples
{
    public static FleetMaps Map { get; } = new(
    [
        new FleetMap(1, "map-1",
        [
            new FleetStation(1, "A-01", ["A-01"]),
            new FleetStation(2, "A-02", ["A-02"]),
            new FleetStation(3, "spare", []),
            new FleetStation(10, "charger", []),
            new FleetStation(11, "wait-1", []),
            new FleetStation(12, "wait-2", []),
            new FleetStation(13, "wait-3", []),
            new FleetStation(20, "oven", []),
            new FleetStation(21, "staging", []),
        ]),
        new FleetMap(2, "map-2",
        [
            new FleetStation(1, "B-01", ["B-01"]),
            new FleetStation(10, "charger-2", []),
            new FleetStation(11, "wait-2-1", []),
            new FleetStation(20, "oven-2", []),
        ]),
    ]);

    public static SiteConfiguration Valid { get; } = new()
    {
        Site = "example",
        Projects = [new Project("P", "项目 P")],
        Zones = [new DispatchZone("Z", "Z 区", "P", DemandOrderPolicy.NearestFirst, DispatchZone.DefaultWaitCap)],
        Areas =
        [
            new AreaSetting("A-01", "Z", "front", false),
            new AreaSetting("A-02", "Z", "rear", false),
        ],
        TaskTypes =
        [
            new TaskType("TO_OVEN", "送烘箱", TaskEnd.Machine, TaskEnd.FixedStation),
            new TaskType("FROM_STAGING", "待送区送机台", TaskEnd.FixedStation, TaskEnd.Machine),
        ],
        TaskTypeSwitches =
        [
            new TaskTypeSwitch("P", "TO_OVEN", true),
            new TaskTypeSwitch("P", "FROM_STAGING", true),
        ],
        FixedStations =
        [
            new FixedStation("P", "TO_OVEN", new StationRef(1, 20), null),
            new FixedStation("P", "FROM_STAGING", new StationRef(1, 21), TimeSpan.FromMinutes(10)),
        ],
        Vehicles =
        [
            Vehicle("v1", "key-1", ["TO_OVEN"], waitingPoint: new StationRef(1, 11)),
            Vehicle("v2", "key-2", ["FROM_STAGING"], waitingPoint: new StationRef(1, 12)),
        ],
    };

    /// <summary><see cref="Valid"/> as a site file, without its task type switches, which a file does not have.</summary>
    public const string ValidFile = """
        name: example
        projects:
          - code: P
            name: 项目 P
            zones:
              - { code: Z, name: Z 区 }
            fixed-stations:
              - { task-type: TO_OVEN, map: 1, station: 20 }
              - { task-type: FROM_STAGING, map: 1, station: 21, all-loaded: 10m }
        task-types:
          - { code: TO_OVEN, name: 送烘箱, pickup: machine, unload: fixed-station }
          - { code: FROM_STAGING, name: 待送区送机台, pickup: fixed-station, unload: machine }
        areas:
          - { area: A-01, zone: Z, door-side: front }
          - { area: A-02, zone: Z, door-side: rear }
        vehicles:
          - id: v1
            riot-device-key: key-1
            project: P
            zone: Z
            task-types: [TO_OVEN]
            slot-groups: [{ name: front, slots: [1, 2, 3, 4] }, { name: rear, slots: [5, 6, 7, 8] }]
            chargers: [{ map: 1, station: 10 }]
            waiting-point: { map: 1, station: 11 }
            io-points: &wiring
              - { slot: 1, lock: DI1, light-curtain: DI9, unlock: DO1 }
              - { slot: 2, lock: DI2, light-curtain: DI10, unlock: DO2 }
              - { slot: 3, lock: DI3, light-curtain: DI11, unlock: DO3 }
              - { slot: 4, lock: DI4, light-curtain: DI12, unlock: DO4 }
              - { slot: 5, lock: DI5, light-curtain: DI13, unlock: DO5 }
              - { slot: 6, lock: DI6, light-curtain: DI14, unlock: DO6 }
              - { slot: 7, lock: DI7, light-curtain: DI15, unlock: DO7 }
              - { slot: 8, lock: DI8, light-curtain: DI16, unlock: DO8 }
          - id: v2
            riot-device-key: key-2
            project: P
            zone: Z
            task-types: [FROM_STAGING]
            slot-groups: [{ name: front, slots: [1, 2, 3, 4] }, { name: rear, slots: [5, 6, 7, 8] }]
            chargers: [{ map: 1, station: 10 }]
            waiting-point: { map: 1, station: 12 }
            io-points: *wiring
        """;

    /// <summary>A vehicle of project P in zone Z with eight slots (1-4 front, 5-8 rear) wired like the IO simulator's cabinet.</summary>
    public static Vehicle Vehicle(
        string id,
        string deviceKey,
        IReadOnlyList<string> taskTypes,
        StationRef waitingPoint,
        string project = "P",
        string zone = "Z",
        IReadOnlyList<StationRef>? chargers = null) =>
        new(
            id,
            deviceKey,
            project,
            zone,
            taskTypes,
            [new SlotGroup("front", [1, 2, 3, 4]), new SlotGroup("rear", [5, 6, 7, 8])],
            chargers ?? [new StationRef(1, 10)],
            waitingPoint,
            [.. Enumerable.Range(1, 8).Select(Wired)]);

    /// <summary>Slot n's lock on DI n, its light curtain on DI n + 8, its unlock on DO n.</summary>
    public static SlotIo Wired(int slot) =>
        new(slot, new IoPoint(IoPointKind.Input, slot), new IoPoint(IoPointKind.Input, slot + 8), new IoPoint(IoPointKind.Output, slot));

    /// <summary>The example with its vehicle <paramref name="id"/> changed.</summary>
    public static SiteConfiguration WithVehicle(string id, Func<Vehicle, Vehicle> change) => Valid with
    {
        Vehicles = [.. Valid.Vehicles.Select(v => v.Id == id ? change(v) : v)],
    };
}
