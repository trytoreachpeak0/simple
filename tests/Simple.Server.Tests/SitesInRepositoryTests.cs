using System.Text.Json;
using Simple.Server.Riot;
using Simple.Server.Sites;

namespace Simple.Server.Tests;

/// <summary>
/// The site files in the repository pass the whole-configuration checks: the
/// sample against the fake RIoT's map (what scenarios import), D8 against a
/// map with D8's station numbers and names (map 26 as RIoT gave it on
/// 2026-10-17, TestData/d8-map-26-stations.csv).
/// </summary>
public sealed class SitesInRepositoryTests
{
    [Fact]
    public void TheSampleSiteIsValidAgainstTheFakeRiotsMap()
    {
        var sample = Read("sample");

        Assert.Empty(SiteValidator.Validate(sample, FakeRiotMaps()));
    }

    [Fact]
    public void TheSampleSiteHasTheFakesAreasTaskTypesAndVehicles()
    {
        var sample = Read("sample");
        using var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Simple.MesGateway", "Fake", "seed-rows.json")));
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Simple.FakeRiot", "seed.json")));

        Assert.All(rows.RootElement.EnumerateArray(), row =>
        {
            Assert.Contains(sample.Areas, a => a.Area == row.GetProperty("area").GetString());
            Assert.Contains(sample.TaskTypes, t => t.Code == row.GetProperty("taskType").GetString());
        });
        Assert.Equal(
            seed.RootElement.GetProperty("vehicles").EnumerateArray().Select(v => v.GetProperty("deviceKey").GetString()),
            sample.Vehicles.Select(v => v.RiotDeviceKey));
    }

    [Fact]
    public void TheD8SiteIsValidAgainstAMapWithD8sStations()
    {
        var d8 = Read("d8");
        var stations = File.ReadLines(Path.Combine(Root, "tests", "Simple.Server.Tests", "TestData", "d8-map-26-stations.csv"))
            .Skip(1)
            .Select(line => line.Split(',', 2))
            .Select(x => new FleetStation(int.Parse(x[0], System.Globalization.CultureInfo.InvariantCulture), x[1], StationAreas.Of(x[1])))
            .ToList();

        Assert.Empty(SiteValidator.Validate(d8, new FleetMaps([new FleetMap(26, "老厂前线new_wk", stations)])));
        // Every area of the configuration is on the map, and every area on the map is configured (316 = 316, as checked on 2026-10-17).
        Assert.Equal(
            d8.Areas.Select(a => a.Area).Order(StringComparer.Ordinal),
            stations.SelectMany(s => s.Areas).Order(StringComparer.Ordinal));
    }

    private static SiteConfiguration Read(string site)
    {
        var read = SiteFile.Read(Path.Combine(Root, "sites", site, SiteFile.FileName));
        Assert.Empty(read.Problems);
        return read.Configuration!;
    }

    /// <summary>The fake RIoT's starting maps, the areas read from station names as the server reads them.</summary>
    private static FleetMaps FakeRiotMaps()
    {
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Simple.FakeRiot", "seed.json")));
        return new FleetMaps(
        [
            .. seed.RootElement.GetProperty("maps").EnumerateArray().Select(m => new FleetMap(
                m.GetProperty("id").GetInt32(),
                m.GetProperty("name").GetString()!,
                [.. m.GetProperty("stations").EnumerateArray().Select(s => new FleetStation(s.GetProperty("id").GetInt32(), s.GetProperty("name").GetString()!, StationAreas.Of(s.GetProperty("name").GetString()!)))])),
        ]);
    }

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "simple.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No simple.slnx above {AppContext.BaseDirectory}.");
    }
}
