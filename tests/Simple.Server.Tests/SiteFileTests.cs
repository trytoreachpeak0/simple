using Simple.Server.Sites;

namespace Simple.Server.Tests;

/// <summary>Reading <c>sites/&lt;site&gt;/site.yaml</c> into a site configuration, and saying where a file is wrong.</summary>
public sealed class SiteFileTests
{
    private const string Full = """
        name: example
        description: 一个例子
        projects:
          - code: P
            name: 项目 P
            zones:
              - code: Z
                name: Z 区
              - code: Y
                name: Y 区
                demand-order: nearest-first-with-wait-cap
                wait-cap: 20m
            fixed-stations:
              - { task-type: TO_OVEN, map: 1, station: 20 }
              - { task-type: FROM_STAGING, map: 1, station: 21, all-loaded: 10m }
        task-types:
          - { code: TO_OVEN, name: 送烘箱, pickup: machine, unload: fixed-station }
          - { code: FROM_STAGING, name: 待送区送机台, pickup: fixed-station, unload: machine }
        areas:
          - { area: A-01, zone: Z, door-side: front }
          - { area: " A-02 ", zone: Z, door-side: rear }
          - { area: A-03, not-served: true }
        vehicles:
          - id: v1
            riot-device-key: key-1
            project: P
            zone: Z
            task-types: [TO_OVEN]
            slot-groups:
              - { name: front, slots: [1, 2] }
              - { name: rear, slots: [3] }
            chargers:
              - { map: 1, station: 10 }
              - { map: 1, station: 9 }
            waiting-point: { map: 1, station: 11 }
            io-points:
              - { slot: 1, lock: DI1, light-curtain: DI9, unlock: DO1 }
              - { slot: 2, lock: DI2, light-curtain: DI10, unlock: DO2 }
              - { slot: 3, lock: DI3, light-curtain: DI11, unlock: DO3 }
        parameters:
          departure-all-loaded: 90s
          brake-count: 4
          ready-level: 70
          unlock-request-max-age: 2500ms
        """;

    [Fact]
    public void AFullFileReadsIntoTheConfiguration()
    {
        var c = Read(Full);

        Assert.Equal("example", c.Site);
        Assert.Equal("一个例子", c.Description);
        Assert.Equal(new Project("P", "项目 P"), Assert.Single(c.Projects));
        Assert.Equal(
            [
                new DispatchZone("Z", "Z 区", "P", DemandOrderPolicy.NearestFirst, TimeSpan.FromMinutes(15)),
                new DispatchZone("Y", "Y 区", "P", DemandOrderPolicy.NearestFirstWithWaitCap, TimeSpan.FromMinutes(20)),
            ],
            c.Zones);
        Assert.Equal(
            [
                new FixedStation("P", "TO_OVEN", new StationRef(1, 20), null),
                new FixedStation("P", "FROM_STAGING", new StationRef(1, 21), TimeSpan.FromMinutes(10)),
            ],
            c.FixedStations);
        Assert.Equal(
            [
                new TaskType("TO_OVEN", "送烘箱", TaskEnd.Machine, TaskEnd.FixedStation),
                new TaskType("FROM_STAGING", "待送区送机台", TaskEnd.FixedStation, TaskEnd.Machine),
            ],
            c.TaskTypes);
        Assert.Equal(
            [
                new AreaSetting("A-01", "Z", "front", false),
                new AreaSetting("A-02", "Z", "rear", false),
                new AreaSetting("A-03", null, null, true),
            ],
            c.Areas);

        var v = Assert.Single(c.Vehicles);
        Assert.Equal(("v1", "key-1", "P", "Z"), (v.Id, v.RiotDeviceKey, v.Project, v.Zone));
        Assert.Equal(["TO_OVEN"], v.TaskTypes);
        Assert.Equal(["front", "rear"], v.SlotGroups.Select(g => g.Name));
        Assert.Equal([1, 2, 3], v.Slots);
        Assert.Equal([new StationRef(1, 10), new StationRef(1, 9)], v.Chargers);
        Assert.Equal(new StationRef(1, 11), v.WaitingPoint);
        Assert.Equal(SiteExamples.Wired(2), v.IoPoints[1]);

        Assert.Equal(TimeSpan.FromSeconds(90), c.Parameters.DepartureAllLoaded);
        Assert.Equal(4, c.Parameters.BrakeCount);
        Assert.Equal(70, c.Parameters.ReadyLevel);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), c.Parameters.UnlockRequestMaxAge);
        // What the file leaves out keeps the ADR default.
        Assert.Equal(TimeSpan.FromMinutes(5), c.Parameters.DepartureNothingLoaded);
        Assert.Equal(99, c.Parameters.FullLevel);
    }

    [Fact]
    public void AFileHasNoTaskTypeSwitches()
    {
        // A first import switches every task type off (ADR 0017); the file cannot say otherwise.
        Assert.Empty(Read(Full).TaskTypeSwitches);
        Assert.Contains("switches", Problems(Full + "\nswitches: []\n").Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsNamedWithItsLine()
    {
        var line = Full.Split('\n').ToList().FindIndex(l => l.Contains("waiting-point:", StringComparison.Ordinal)) + 1;

        var problem = Problems(Full.Replace("waiting-point:", "waiting-spot:", StringComparison.Ordinal)).Single();

        Assert.Contains($"line {line}", problem.Where, StringComparison.Ordinal);
        Assert.Contains("waiting-spot", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("riot-device-key: key-1", "", "vehicles[0].riot-device-key")]
    [InlineData("waiting-point: { map: 1, station: 11 }", "", "vehicles[0].waiting-point")]
    [InlineData("{ code: TO_OVEN, name: 送烘箱, pickup: machine, unload: fixed-station }", "{ code: TO_OVEN, pickup: machine, unload: fixed-station }", "task-types[0].name")]
    [InlineData("pickup: machine", "pickup: car", "task-types[0].pickup")]
    [InlineData("wait-cap: 20m", "wait-cap: 20 minutes", "projects[0].zones[1].wait-cap")]
    [InlineData("demand-order: nearest-first-with-wait-cap", "demand-order: closest", "projects[0].zones[1].demand-order")]
    [InlineData("unlock: DO2", "unlock: X2", "vehicles[0].io-points[1].unlock")]
    [InlineData("brake-count: 4", "brake-count: many", "parameters.brake-count")]
    [InlineData("brake-count: 4", "brake-time: 4", "parameters.brake-time")]
    [InlineData("{ map: 1, station: 9 }", "{ map: 1 }", "vehicles[0].chargers[1].station")]
    public void AMissingOrMalformedValueIsNamedByWhereItIs(string from, string to, string where)
    {
        var problems = Problems(Full.Replace(from, to, StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Where == where);
    }

    [Fact]
    public void AFileWithoutANameIsRefused()
    {
        Assert.Contains(Problems("projects: []\n"), p => p.Where == "name");
    }

    [Fact]
    public void AnEmptyFileIsRefused()
    {
        Assert.NotEmpty(Problems(""));
    }

    private static SiteConfiguration Read(string text)
    {
        var read = SiteFile.Parse(text, "site.yaml");
        Assert.Empty(read.Problems);
        return read.Configuration!;
    }

    private static IReadOnlyList<SiteProblem> Problems(string text)
    {
        var read = SiteFile.Parse(text, "site.yaml");
        Assert.Null(read.Configuration);
        return read.Problems;
    }
}
