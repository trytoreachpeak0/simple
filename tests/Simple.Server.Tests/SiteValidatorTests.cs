using Simple.Server.Riot;
using Simple.Server.Sites;
using static Simple.Server.Tests.SiteExamples;

namespace Simple.Server.Tests;

/// <summary>
/// The whole-configuration checks of docs/rules-overview.md §13, one rule a
/// test: the example is valid, and changing one thing gives exactly the
/// problem that names it.
/// </summary>
public sealed class SiteValidatorTests
{
    [Fact]
    public void TheExampleIsValid()
    {
        Assert.Empty(SiteValidator.Validate(Valid, Map));
    }

    // ---- Task types (ADR 0006) ----

    [Theory]
    [InlineData(TaskEnd.Machine)]
    [InlineData(TaskEnd.FixedStation)]
    public void ATaskTypeWithTheSameKindAtBothEndsIsRefused(TaskEnd end)
    {
        var configuration = Valid with
        {
            TaskTypes = [.. Valid.TaskTypes.Select(t => t.Code == "TO_OVEN" ? t with { Pickup = end, Unload = end } : t)],
        };

        var problem = Single(configuration);

        Assert.Equal("task-types[TO_OVEN]", problem.Where);
        Assert.Contains("一端是机台、一端是固定站点", problem.Message, StringComparison.Ordinal);
    }

    // ---- Areas (ADR 0002, 0015) ----

    [Fact]
    public void AnAreaInTwoZonesIsRefusedNamingBoth()
    {
        var configuration = Valid with
        {
            Zones = [.. Valid.Zones, new DispatchZone("Y", "Y 区", "P", DemandOrderPolicy.NearestFirst, DispatchZone.DefaultWaitCap)],
            Areas = [.. Valid.Areas, new AreaSetting("A-01", "Y", "front", false)],
        };

        var problem = Single(configuration);

        Assert.Equal("areas[A-01]", problem.Where);
        Assert.Contains("Z", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Y", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAreaIsComparedAfterTrimmingOnly()
    {
        var trimmed = Valid with { Areas = [.. Valid.Areas, new AreaSetting(" A-01 ", "Z", "front", false)] };
        var padded = Valid with { Areas = [.. Valid.Areas, new AreaSetting("0A-01", "Z", "front", false)] };

        Assert.Equal("areas[A-01]", Single(trimmed).Where);
        Assert.Empty(SiteValidator.Validate(padded, Map));
    }

    [Fact]
    public void AnAreaWithoutZoneOrDoorSideIsAllowed()
    {
        // Not dispatched to and an alarm at run time (ADR 0006, 0015), not a validation failure.
        var configuration = Valid with { Areas = [.. Valid.Areas, new AreaSetting("A-03", null, null, false)] };

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void AnAreaInAnUnknownZoneIsRefused()
    {
        var configuration = Valid with { Areas = [.. Valid.Areas, new AreaSetting("A-03", "NOPE", "front", false)] };

        Assert.Equal("areas[A-03].zone", Single(configuration).Where);
    }

    [Fact]
    public void ADoorSideThatNoVehicleHasAsASlotGroupIsRefused()
    {
        var configuration = Valid with { Areas = [.. Valid.Areas, new AreaSetting("A-03", "Z", "left", false)] };

        Assert.Equal("areas[A-03].door-side", Single(configuration).Where);
    }

    // ---- Task types and vehicles (ADR 0023) ----

    [Fact]
    public void TwoVehiclesOfOneProjectTakingOneTaskTypeAreRefused()
    {
        var configuration = WithVehicle("v2", v => v with { TaskTypes = ["FROM_STAGING", "TO_OVEN"] });

        var problem = Single(configuration);

        Assert.Equal("vehicles[v2].task-types", problem.Where);
        Assert.Contains("v1", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VehiclesOfTwoProjectsMayTakeTheSameTaskType()
    {
        var configuration = WithTwoProjects() with
        {
            Vehicles = [.. Valid.Vehicles, Vehicle("v3", "key-3", ["TO_OVEN"], new StationRef(2, 11), project: "Q", zone: "Y", chargers: [new StationRef(2, 10)])],
        };

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void ASwitchedOnTaskTypeThatNoVehicleTakesIsRefused()
    {
        var configuration = WithVehicle("v2", v => v with { TaskTypes = [] });

        var problem = Single(configuration);

        Assert.Equal("task-type-switches[P/FROM_STAGING]", problem.Where);
    }

    [Fact]
    public void ASwitchedOffTaskTypeNeedsNoVehicle()
    {
        var configuration = WithVehicle("v2", v => v with { TaskTypes = [] }) with
        {
            TaskTypeSwitches = [new TaskTypeSwitch("P", "TO_OVEN", true), new TaskTypeSwitch("P", "FROM_STAGING", false)],
        };

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void AVehicleWhoseZoneBelongsToAnotherProjectIsRefused()
    {
        var configuration = WithTwoProjects() with
        {
            Vehicles = [.. Valid.Vehicles, Vehicle("v3", "key-3", [], new StationRef(2, 11), project: "P", zone: "Y")],
        };

        var problem = Single(configuration);

        Assert.Equal("vehicles[v3].zone", problem.Where);
    }

    [Fact]
    public void AVehicleTakingAnUnknownTaskTypeIsRefused()
    {
        Assert.Equal("vehicles[v1].task-types", Single(WithVehicle("v1", v => v with { TaskTypes = ["TO_OVEN", "NOPE"] })).Where);
    }

    [Fact]
    public void TwoVehiclesWithOneIdOrOneDeviceKeyAreRefused()
    {
        var sameId = WithVehicle("v2", v => v with { Id = "v1" });
        var sameKey = WithVehicle("v2", v => v with { RiotDeviceKey = "key-1" });

        Assert.Contains(SiteValidator.Validate(sameId, Map), p => p.Where == "vehicles[v1]" && p.Message.Contains("不止一次", StringComparison.Ordinal));
        Assert.Equal("vehicles[v2].riot-device-key", Single(sameKey).Where);
    }

    // ---- Stations (ADR 0015, 0020, 0021) ----

    [Fact]
    public void AFixedStationNotOnTheMapIsRefused()
    {
        var configuration = Valid with
        {
            FixedStations = [Valid.FixedStations[0] with { Station = new StationRef(1, 99) }, Valid.FixedStations[1]],
        };

        var problem = Single(configuration);

        Assert.Equal("fixed-stations[P/TO_OVEN/1]", problem.Where);
        Assert.Contains("地图 1 站 99", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStationOnAMapTheFleetManagerDoesNotHaveIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with { WaitingPoint = new StationRef(9, 11) });

        Assert.Equal("vehicles[v1].waiting-point", Single(configuration).Where);
    }

    [Fact]
    public void AChargerNotOnTheMapIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with { Chargers = [new StationRef(1, 10), new StationRef(1, 98)] });

        Assert.Equal("vehicles[v1].chargers[1]", Single(configuration).Where);
    }

    [Fact]
    public void AVehicleWithoutAChargerIsRefused()
    {
        Assert.Equal("vehicles[v1].chargers", Single(WithVehicle("v1", v => v with { Chargers = [] })).Where);
    }

    [Fact]
    public void TwoVehiclesWithOneWaitingPointAreRefused()
    {
        var configuration = WithVehicle("v2", v => v with { WaitingPoint = new StationRef(1, 11) });

        var problem = Single(configuration);

        Assert.Equal("vehicles[v2].waiting-point", problem.Where);
        Assert.Contains("v1", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChargerSharedWithinAProjectIsAllowed()
    {
        // Both example vehicles of project P already list charger 10.
        Assert.Empty(SiteValidator.Validate(Valid, Map));
    }

    [Fact]
    public void AChargerSharedByTwoProjectsIsRefused()
    {
        var configuration = WithTwoProjects() with
        {
            Vehicles = [.. Valid.Vehicles, Vehicle("v3", "key-3", [], new StationRef(2, 11), project: "Q", zone: "Y", chargers: [new StationRef(1, 10)])],
        };

        var problem = Single(configuration);

        Assert.Equal("vehicles[v3].chargers[0]", problem.Where);
        Assert.Contains("P", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, "机台站点")] // waiting point on a machine station (area A-01)
    [InlineData(1, 10, "充电桩")] // on the charger
    [InlineData(1, 20, "固定站点")] // on the oven
    public void AWaitingPointThatIsAlsoAnotherUseIsRefused(int map, int station, string otherUse)
    {
        var configuration = WithVehicle("v1", v => v with { WaitingPoint = new StationRef(map, station) });

        var problem = Single(configuration);

        Assert.Equal("vehicles[v1].waiting-point", problem.Where);
        Assert.Contains(otherUse, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChargerOnAFixedStationIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with { Chargers = [new StationRef(1, 10), new StationRef(1, 21)] });

        var problem = Single(configuration);

        Assert.Equal("vehicles[v1].chargers[1]", problem.Where);
        Assert.Contains("固定站点", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFixedStationOnAMachineStationIsRefused()
    {
        var configuration = Valid with
        {
            FixedStations = [Valid.FixedStations[0] with { Station = new StationRef(1, 2) }, Valid.FixedStations[1]],
        };

        var problem = Single(configuration);

        Assert.Equal("fixed-stations[P/TO_OVEN/1]", problem.Where);
        Assert.Contains("A-02", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFixedStationsForOneProjectMapAndTaskTypeAreRefused()
    {
        var configuration = Valid with
        {
            FixedStations = [.. Valid.FixedStations, new FixedStation("P", "TO_OVEN", new StationRef(1, 3), null)],
        };

        Assert.Equal("fixed-stations[P/TO_OVEN/1]", Single(configuration).Where);
    }

    [Fact]
    public void OneTaskTypeMayHaveAFixedStationOnEachMap()
    {
        var configuration = Valid with
        {
            FixedStations = [.. Valid.FixedStations, new FixedStation("P", "TO_OVEN", new StationRef(2, 20), null)],
        };

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void OneStationAsTheFixedStationOfTwoTaskTypesIsRefused()
    {
        var configuration = Valid with
        {
            FixedStations = [Valid.FixedStations[0], Valid.FixedStations[1] with { Station = new StationRef(1, 20) }],
        };

        var problem = Single(configuration);

        Assert.Equal("fixed-stations[P/FROM_STAGING/1]", problem.Where);
        Assert.Contains("TO_OVEN", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneStationAsTheFixedStationOfTwoProjectsIsRefused()
    {
        var configuration = WithTwoProjects() with
        {
            FixedStations = [.. Valid.FixedStations, new FixedStation("Q", "TO_OVEN", new StationRef(1, 20), null)],
        };

        Assert.Equal("fixed-stations[Q/TO_OVEN/1]", Single(configuration).Where);
    }

    [Fact]
    public void AFixedStationOfAnUnknownProjectOrTaskTypeIsRefused()
    {
        var configuration = Valid with
        {
            FixedStations = [.. Valid.FixedStations, new FixedStation("NOPE", "TO_OVEN", new StationRef(1, 3), null)],
        };

        Assert.Equal("fixed-stations[NOPE/TO_OVEN/1].project", Single(configuration).Where);
    }

    // ---- Battery levels (ADR 0020) ----

    [Theory]
    [InlineData(35, 35, 60, 99)] // urgent not below charge-needed
    [InlineData(20, 60, 60, 99)] // charge-needed not below ready
    [InlineData(20, 35, 99, 98)] // ready above full
    [InlineData(-1, 35, 60, 99)] // not a percentage
    [InlineData(20, 35, 60, 101)]
    public void BatteryLevelsOutOfOrderAreRefused(int urgent, int needed, int ready, int full)
    {
        var configuration = Valid with
        {
            Parameters = new SiteParameters { UrgentChargeLevel = urgent, ChargeNeededLevel = needed, ReadyLevel = ready, FullLevel = full },
        };

        Assert.StartsWith("parameters.", Single(configuration).Where, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyMayEqualFull()
    {
        var configuration = Valid with { Parameters = new SiteParameters { ReadyLevel = 99, FullLevel = 99 } };

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void DurationsAndCountsMustBePositive()
    {
        var configuration = Valid with
        {
            Parameters = new SiteParameters { DepartureAllLoaded = TimeSpan.Zero, BrakeCount = 0 },
            Zones = [Valid.Zones[0] with { WaitCap = TimeSpan.FromSeconds(-1) }],
            FixedStations = [Valid.FixedStations[0] with { AllLoaded = TimeSpan.Zero }, Valid.FixedStations[1]],
        };

        var problems = SiteValidator.Validate(configuration, Map).Select(p => p.Where).Order(StringComparer.Ordinal);

        Assert.Equal(
            ["fixed-stations[P/TO_OVEN/1].all-loaded", "parameters.brake-count", "parameters.departure-all-loaded", "zones[Z].wait-cap"],
            problems);
    }

    // ---- The vehicle's slots and IO point map (ADR 0006, 0024) ----

    [Fact]
    public void ASlotInTwoGroupsIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with { SlotGroups = [new SlotGroup("front", [1, 2, 3, 4]), new SlotGroup("rear", [4, 5, 6, 7, 8])] });

        Assert.Contains(SiteValidator.Validate(configuration, Map), p => p.Where == "vehicles[v1].slot-groups" && p.Message.Contains('4', StringComparison.Ordinal));
    }

    [Fact]
    public void ASlotWithoutIoPointsIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with { IoPoints = [.. v.IoPoints.Where(io => io.Slot != 8)] });

        Assert.Equal("vehicles[v1].io-points", Single(configuration).Where);
    }

    [Fact]
    public void IoPointsForASlotTheVehicleDoesNotHaveAreRefused()
    {
        var configuration = WithVehicle("v1", v => v with { IoPoints = [.. v.IoPoints, Wired(9)] });

        Assert.Contains(SiteValidator.Validate(configuration, Map), p => p.Where == "vehicles[v1].io-points[slot 9]");
    }

    [Fact]
    public void AnOutputAsALockOrAnInputAsAnUnlockIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with
        {
            IoPoints = [.. v.IoPoints.Select(io => io.Slot == 1 ? io with { Lock = new IoPoint(IoPointKind.Output, 9), Unlock = new IoPoint(IoPointKind.Input, 16) } : io)],
        });

        var problems = SiteValidator.Validate(configuration, Map).Select(p => p.Where).Order(StringComparer.Ordinal);

        Assert.Equal(["vehicles[v1].io-points[slot 1].lock", "vehicles[v1].io-points[slot 1].unlock"], problems);
    }

    [Fact]
    public void OneIoPointWiredToTwoSignalsIsRefused()
    {
        var configuration = WithVehicle("v1", v => v with
        {
            IoPoints = [.. v.IoPoints.Select(io => io.Slot == 2 ? io with { Lock = new IoPoint(IoPointKind.Input, 1) } : io)],
        });

        var problem = Single(configuration);

        Assert.Equal("vehicles[v1].io-points[slot 2].lock", problem.Where);
        Assert.Contains("DI1", problem.Message, StringComparison.Ordinal);
    }

    // ---- References ----

    [Fact]
    public void TwoProjectsZonesOrTaskTypesWithOneCodeAreRefused()
    {
        var configuration = Valid with
        {
            Projects = [.. Valid.Projects, new Project("P", null)],
            Zones = [.. Valid.Zones, Valid.Zones[0]],
            TaskTypes = [.. Valid.TaskTypes, Valid.TaskTypes[0]],
        };

        var problems = SiteValidator.Validate(configuration, Map).Select(p => p.Where).ToList();

        Assert.Contains("projects[P]", problems);
        Assert.Contains("zones[Z]", problems);
        Assert.Contains("task-types[TO_OVEN]", problems);
    }

    [Fact]
    public void ValuesLongerThanTheirColumnsAreRefused()
    {
        var longId = new string('v', SiteModel.CodeMaxLength + 1);
        var longName = new string('名', SiteModel.NameMaxLength + 1);
        var configuration = WithVehicle("v1", v => v with { Id = longId }) with
        {
            TaskTypes = [Valid.TaskTypes[0] with { Name = longName }, Valid.TaskTypes[1]],
            Areas = [.. Valid.Areas, new AreaSetting(new string('A', SiteModel.CodeMaxLength + 1), "Z", "front", false)],
        };

        var problems = SiteValidator.Validate(configuration, Map).Select(p => p.Where).ToList();

        Assert.Contains($"vehicles[{longId}].id", problems);
        Assert.Contains("task-types[TO_OVEN].name", problems);
        Assert.Contains(problems, p => p.StartsWith("areas[AAA", StringComparison.Ordinal) && p.EndsWith("].area", StringComparison.Ordinal));
    }

    [Fact]
    public void ValuesAtTheirColumnsLengthAreAllowed()
    {
        var configuration = WithVehicle("v1", v => v with { Id = new string('v', SiteModel.CodeMaxLength) });

        Assert.Empty(SiteValidator.Validate(configuration, Map));
    }

    [Fact]
    public void AZoneOfAnUnknownProjectIsRefused()
    {
        var configuration = Valid with { Zones = [.. Valid.Zones, new DispatchZone("Y", "Y 区", "NOPE", DemandOrderPolicy.NearestFirst, DispatchZone.DefaultWaitCap)] };

        Assert.Equal("zones[Y].project", Single(configuration).Where);
    }

    [Fact]
    public void EveryProblemIsListedNotOnlyTheFirst()
    {
        var configuration = WithVehicle("v1", v => v with { Chargers = [], WaitingPoint = new StationRef(1, 99) });

        Assert.Equal(2, SiteValidator.Validate(configuration, Map).Count);
    }

    private static SiteProblem Single(SiteConfiguration configuration)
    {
        var problems = SiteValidator.Validate(configuration, Map);
        return Assert.Single(problems);
    }

    /// <summary>The example plus project Q with zone Y (area B-01 on map 2).</summary>
    private static SiteConfiguration WithTwoProjects() => Valid with
    {
        Projects = [.. Valid.Projects, new Project("Q", null)],
        Zones = [.. Valid.Zones, new DispatchZone("Y", "Y 区", "Q", DemandOrderPolicy.FirstComeFirstServed, DispatchZone.DefaultWaitCap)],
        Areas = [.. Valid.Areas, new AreaSetting("B-01", "Y", "front", false)],
    };
}
