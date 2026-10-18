using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Simple.Server.Events;
using Simple.Server.Sites;
using static Simple.Server.Tests.SiteExamples;

namespace Simple.Server.Tests;

/// <summary>
/// The first-install import of a site file (ADR 0017): checked as a whole
/// against the fleet manager's maps, stored as version 1 with every task type
/// switched off, recorded in the event record, and read back through the API.
/// </summary>
public sealed class SiteImportTests : IAsyncDisposable
{
    private readonly ServerInstance server = new();
    private readonly string directory = Directory.CreateTempSubdirectory("simple-site-import-").FullName;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeTheFirstImportThereIsNoConfiguration()
    {
        var admin = server.Start();

        var response = await admin.GetAsync(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AValidFileIsStoredAsVersionOneWithEveryTaskTypeSwitchedOff()
    {
        var admin = server.Start();
        server.Riot.SetMaps(Map);

        var (exit, output) = await ImportAsync(ValidFile);

        Assert.Equal(SiteImportCommand.Imported, exit);
        Assert.Contains("version 1", output, StringComparison.Ordinal);
        var read = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel);
        Assert.Equal(1, read.GetProperty("version").GetInt32());
        Assert.Equal(SiteVersionSources.Import, read.GetProperty("source").GetString());
        var expected = JsonSerializer.SerializeToElement(Valid.WithEverySwitchOff(), SiteConfigurationEndpoints.JsonOptions);
        Assert.Equal(expected.GetRawText(), read.GetProperty("configuration").GetRawText());
        var switches = read.GetProperty("configuration").GetProperty("taskTypeSwitches").EnumerateArray().ToList();
        Assert.Equal(2, switches.Count);
        Assert.All(switches, s => Assert.False(s.GetProperty("enabled").GetBoolean()));
    }

    [Fact]
    public async Task TheConfigurationReadsBackAsItWasWritten()
    {
        server.Start();
        server.Riot.SetMaps(Map);
        await ImportAsync(ValidFile);

        await using var scope = server.Services.CreateAsyncScope();
        var current = await scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>().CurrentAsync(Cancel);

        Assert.Equal(Json(Valid.WithEverySwitchOff()), Json(current!.Configuration));
    }

    [Fact]
    public async Task TheStoreAnswersWhetherAVehicleIsInTheCurrentConfiguration()
    {
        server.Start();
        server.Riot.SetMaps(Map);
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>();
        Assert.False(await store.HasVehicleAsync("v1", Cancel));

        await ImportAsync(ValidFile);

        Assert.True(await store.HasVehicleAsync("v1", Cancel));
        Assert.False(await store.HasVehicleAsync("v9", Cancel));
    }

    [Fact]
    public async Task IdsThatDifferOnlyInCaseAreTwoAndTheVehicleLookupIsExact()
    {
        // Validation compares exactly; so does the database, so the import lists no problem and stores both.
        server.Start();
        server.Riot.SetMaps(Map);

        var (exit, output) = await ImportAsync(ValidFile.Replace("id: v2", "id: V1", StringComparison.Ordinal));

        Assert.Equal(SiteImportCommand.Imported, exit);
        Assert.Contains("version 1", output, StringComparison.Ordinal);
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>();
        Assert.True(await store.HasVehicleAsync("v1", Cancel));
        Assert.True(await store.HasVehicleAsync("V1", Cancel));
        Assert.False(await store.HasVehicleAsync("v2", Cancel));
    }

    [Fact]
    public async Task TheVehicleLookupDoesNotIgnoreCase()
    {
        server.Start();
        server.Riot.SetMaps(Map);
        await ImportAsync(ValidFile);

        await using var scope = server.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>().HasVehicleAsync("V1", Cancel));
    }

    [Fact]
    public async Task VersionsCountOneTwoAndAWriteOnAStaleVersionStoresNothing()
    {
        server.Start();
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>();
        var at = DateTimeOffset.UtcNow;

        var first = await store.AddAsync(Valid, SiteVersionSources.Import, at, basedOn: 0, null, Cancel);
        var stale = await store.AddAsync(Valid, SiteVersionSources.Import, at, basedOn: 0, null, Cancel);
        var second = await store.AddAsync(Valid, SiteVersionSources.Import, at, basedOn: 1, null, Cancel);

        Assert.Equal(1, first!.Version);
        Assert.Null(stale);
        Assert.Equal(2, second!.Version);
        Assert.Equal(2, (await store.CurrentAsync(Cancel))!.Version);
        // The store tells the in-memory current version itself.
        Assert.Equal(2, server.Services.GetRequiredService<CurrentSiteVersion>().Summary!.Version);
    }

    [Fact]
    public async Task TheImportIsRecordedInTheEventRecord()
    {
        var admin = server.Start();
        server.Riot.SetMaps(Map);

        await ImportAsync(ValidFile);

        var events = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        var imported = Assert.Single(events!.Items, e => e.Type == EventTypes.SiteConfigurationImported);
        Assert.Equal(EventSources.ServerCommand, imported.Source);
        Assert.Equal(1, imported.Data!.Value.GetProperty("version").GetInt32());
        Assert.Equal("example", imported.Data!.Value.GetProperty("site").GetString());
        Assert.Equal(2, imported.Data!.Value.GetProperty("vehicles").GetInt32());
    }

    [Fact]
    public async Task TheStatusNamesTheCurrentVersion()
    {
        var admin = server.Start();
        server.Riot.SetMaps(Map);
        var before = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/status", UriKind.Relative), Cancel);

        await ImportAsync(ValidFile);

        var after = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("siteConfiguration").ValueKind);
        Assert.Equal(1, after.GetProperty("siteConfiguration").GetProperty("version").GetInt32());
        Assert.Equal("example", after.GetProperty("siteConfiguration").GetProperty("site").GetString());
    }

    [Fact]
    public async Task OnlyTheFirstImportStoresAnything()
    {
        var admin = server.Start();
        server.Riot.SetMaps(Map);
        await ImportAsync(ValidFile);

        var (exit, output) = await ImportAsync(ValidFile.Replace("name: 项目 P", "name: 改过的名字", StringComparison.Ordinal));

        Assert.Equal(SiteImportCommand.Imported, exit);
        Assert.Contains("already", output, StringComparison.Ordinal);
        var read = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel);
        Assert.Equal(1, read.GetProperty("version").GetInt32());
        Assert.Equal("项目 P", read.GetProperty("configuration").GetProperty("projects")[0].GetProperty("name").GetString());
        var events = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        Assert.Single(events!.Items, e => e.Type == EventTypes.SiteConfigurationImported);
    }

    [Fact]
    public async Task AFileOfAnotherSiteIsRefusedOnceTheServerHasAConfiguration()
    {
        server.Start();
        server.Riot.SetMaps(Map);
        await ImportAsync(ValidFile);

        var (exit, output) = await ImportAsync(ValidFile.Replace("name: example", "name: elsewhere", StringComparison.Ordinal));

        Assert.Equal(SiteImportCommand.NotImported, exit);
        Assert.Contains("example", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfigurationThatFailsValidationIsNotImportedAndEveryProblemIsListed()
    {
        var admin = server.Start();
        server.Riot.SetMaps(new Riot.FleetMaps([Map.Maps[0] with { Stations = [.. Map.Maps[0].Stations.Where(s => s.Id is not (11 or 21))] }]));

        var (exit, output) = await ImportAsync(ValidFile);

        Assert.Equal(SiteImportCommand.NotImported, exit);
        Assert.Contains("vehicles[v1].waiting-point", output, StringComparison.Ordinal);
        Assert.Contains("fixed-stations[P/FROM_STAGING/1]", output, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel)).StatusCode);
        var events = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        Assert.DoesNotContain(events!.Items, e => e.Type == EventTypes.SiteConfigurationImported);
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsNotImported()
    {
        server.Start();
        server.Riot.SetMaps(Map);

        var (exit, output) = await ImportAsync(ValidFile.Replace("waiting-point:", "waiting-spot:", StringComparison.Ordinal));

        Assert.Equal(SiteImportCommand.NotImported, exit);
        Assert.Contains("waiting-spot", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheFleetManagersMapsNothingIsImported()
    {
        var admin = server.Start();
        server.Riot.BeUnreachable();

        var (exit, output) = await ImportAsync(ValidFile);

        Assert.Equal(SiteImportCommand.MapsUnreadable, exit);
        Assert.Contains("stub: RIoT refused the connection", output, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task EveryAccountCanReadTheConfigurationButNotWithoutLogin()
    {
        var admin = server.Start();
        server.Riot.SetMaps(Map);
        await ImportAsync(ValidFile);
        var created = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "p-team-leader", role = "team-leader", project = "P", password = "team-leader-password" }, Cancel);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var teamLeader = (await server.LogInAsync("p-team-leader", "team-leader-password")).Client!;
        using var anonymous = server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await teamLeader.GetAsync(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/v1/site-configuration", UriKind.Relative), Cancel)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private async Task<(int Exit, string Output)> ImportAsync(string text)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, text, Cancel);
        var output = new StringWriter();
        var exit = await SiteImportCommand.RunAsync(server.Services, path, output, Cancel);
        return (exit, output.ToString());
    }

    private static string Json(SiteConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, SiteConfigurationEndpoints.JsonOptions);
}
