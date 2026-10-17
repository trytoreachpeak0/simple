using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Simple.FakeRiot.Tests;

/// <summary>
/// What belongs to the fake itself, not to RIoT: where it may listen, what it
/// needs to start, its control API and its seed. What it answers as RIoT is
/// tested by Simple.Contracts.Tests.
/// </summary>
public sealed class FakeRiotHostTests : IDisposable
{
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), "simple-fakeriot-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not created, or the fake hosted in process has not let go of its
            // log yet when the factory returns (#80, as in #69), or a scanner
            // holds it; a folder left in %TEMP% harms nothing.
        }
    }

    /// <summary>The fake on its shipped settings, logging to this test's own folder.</summary>
    private WebApplicationFactory<Program> Fake() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseSetting("Simple:Logs:Directory", logDirectory));

    /// <summary>Like every SIMPLE program it writes a program log (ADR 0034).</summary>
    [Fact]
    public async Task TheFakeWritesAProgramLog()
    {
        await using (var factory = Fake())
        {
            using var client = factory.CreateClient();
            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancel);
        }

        Assert.NotEmpty(Simple.Logging.ProgramLog.Files(logDirectory));
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5200")]
    [InlineData("urls", "http://10.20.30.40:5200")]
    [InlineData("urls", "http://*:5200")]
    [InlineData("http_ports", "5200")]
    public void TheFakeRefusesToListenBeyondLoopback(string key, string value)
    {
        var failures = Validate(new Dictionary<string, string?> { [key] = value });

        Assert.Contains(failures, f => f.Contains(value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5200")]
    [InlineData("http://[::1]:5200")]
    [InlineData("http://localhost:5200")]
    public void LoopbackIsAccepted(string url) => Assert.Empty(Validate(new Dictionary<string, string?> { ["urls"] = url }));

    [Fact]
    public void TheFakeRefusesToStartWithoutACallKey()
    {
        var failures = Validate(new Dictionary<string, string?> { ["FakeRiot:CallApiKey"] = "" });

        Assert.Contains(failures, f => f.Contains("CallApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public void AUsernameWithoutAPasswordIsRefused()
    {
        var failures = Validate(new Dictionary<string, string?> { ["FakeRiot:Username"] = "admin" });

        Assert.Contains(failures, f => f.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheShippedSettingsStartTheFakeAndItIsReady()
    {
        await using var factory = Fake();
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task TheControlApiAnswersNotFoundForAnOrderItDoesNotHave()
    {
        await using var factory = Fake();
        using var client = factory.CreateClient();

        using var hang = await client.PostAsync(new Uri("/fake-riot/orders/order-1/hang", UriKind.Relative), content: null, Cancel);
        using var charger = await client.PutAsJsonAsync(new Uri("/fake-riot/maps/1/chargers/1", UriKind.Relative), new { connected = false }, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, hang.StatusCode);
        // Station 1 of map 1 is not a charging station.
        Assert.Equal(HttpStatusCode.NotFound, charger.StatusCode);
    }

    [Fact]
    public async Task TheControlApiAnswersNotFoundForAVehicleOrMapItDoesNotHave()
    {
        await using var factory = Fake();
        using var client = factory.CreateClient();

        using var vehicle = await client.PutAsJsonAsync(new Uri("/fake-riot/vehicles/BROKERX-none", UriKind.Relative), new { battery = 50 }, Cancel);
        using var drive = await client.PostAsJsonAsync(new Uri("/fake-riot/vehicles/BROKERX-none/drive", UriKind.Relative), new { station = 3 }, Cancel);
        using var station = await client.PostAsJsonAsync(new Uri("/fake-riot/maps/99/stations", UriKind.Relative), new { name = "x", type = 1, x = 0, y = 0 }, Cancel);
        using var web = await client.PostAsJsonAsync(new Uri("/fake-riot/orders/web", UriKind.Relative), new { vehicleKey = "BROKERX-none", mapId = 1, station = 3 }, Cancel);
        using var fail = await client.PostAsync(new Uri("/fake-riot/orders/order-1/fail", UriKind.Relative), content: null, Cancel);

        Assert.All(new[] { vehicle, drive, station, web, fail }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Theory]
    [InlineData("PUT", "/fake-riot/vehicles/BROKERX-0000000000000000000000000000fa01", """{"battery":101}""")]
    [InlineData("PUT", "/fake-riot/vehicles/BROKERX-0000000000000000000000000000fa01", """{"mode":"MODE_ROBOT"}""")]
    [InlineData("PUT", "/fake-riot/order-list/churn", """{"ordersPerRead":-1}""")]
    [InlineData("PUT", "/fake-riot/read-delay", """{"milliseconds":-1}""")]
    [InlineData("POST", "/fake-riot/faults/create-00002", """{"orderMade":false,"times":-1}""")]
    public async Task TheControlApiRefusesValuesThatMakeNoSense(string method, string path, string body)
    {
        await using var factory = Fake();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The control API fails only a queued order: real RIoT is said to fail
    /// only an order just made (ADR 0010), so an executing one is refused.
    /// </summary>
    [Fact]
    public async Task TheControlApiFailsOnlyAQueuedOrder()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Simple:Logs:Directory", logDirectory);
            // Twice as fast: it executes after a second and travels for ten.
            host.UseSetting("FakeRiot:Timing:Speedup", "2");
        });
        using var client = FakeRiotHttp.Client(factory);
        var orderId = await FakeRiotHttp.CreateMoveAsync(client, "executing", station: 3);
        Assert.Equal(3, await FakeRiotHttp.StateWithinAsync(client, "executing", TimeSpan.FromSeconds(10), 3));

        using var fail = await client.PostAsync(new Uri($"/fake-riot/orders/{orderId}/fail", UriKind.Relative), content: null, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, fail.StatusCode);
    }

    /// <summary>A station a vehicle stands on cannot be deleted; a vehicle with the manual charger plugged in cannot be driven.</summary>
    [Fact]
    public async Task TheControlApiRefusesWhatThePlantWouldNotAllow()
    {
        await using var factory = Fake();
        using var client = factory.CreateClient();

        using var delete = await client.DeleteAsync(new Uri("/fake-riot/maps/1/stations/1", UriKind.Relative), Cancel);
        using var plug = await client.PutAsJsonAsync(new Uri("/fake-riot/vehicles/BROKERX-0000000000000000000000000000fa01", UriKind.Relative), new { manualCharger = true }, Cancel);
        using var drive = await client.PostAsJsonAsync(new Uri("/fake-riot/vehicles/BROKERX-0000000000000000000000000000fa01/drive", UriKind.Relative), new { station = 3 }, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, plug.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, drive.StatusCode);
    }

    [Theory]
    [InlineData("FakeRiot:Timing:Speedup", "0", "Speedup")]
    [InlineData("FakeRiot:Timing:ChargingTimePerPercent", "00:00:00", "ChargingTimePerPercent")]
    [InlineData("FakeRiot:Timing:ContinueStartDelay", "-00:00:01", "ContinueStartDelay")]
    [InlineData("FakeRiot:ReadDelay", "-00:00:01", "ReadDelay")]
    [InlineData("FakeRiot:NoOrdersAtOrBelowBattery", "101", "NoOrdersAtOrBelowBattery")]
    public void TimingsAndLevelsOutOfRangeAreRefused(string key, string value, string named)
    {
        var failures = Validate(new Dictionary<string, string?> { [key] = value });

        Assert.Contains(failures, f => f.Contains(named, StringComparison.Ordinal));
    }

    /// <summary>The seed's edges join stations of their own map, and every vehicle stands on a station of its map.</summary>
    [Fact]
    public void TheShippedSeedIsConsistent()
    {
        var seed = Seed.Read(Path.Combine(AppContext.BaseDirectory, "seed.json"));

        Assert.All(seed.Maps, map => Assert.All(map.Edges, edge =>
        {
            Assert.Contains(map.Stations, s => s.Id == edge.From);
            Assert.Contains(map.Stations, s => s.Id == edge.To);
        }));
        Assert.All(seed.Vehicles, v => Assert.Contains(seed.Maps.Single(m => m.Id == v.MapId).Stations, s => s.Id == v.Station));
        Assert.Equal(seed.Vehicles.Count, seed.Vehicles.Select(v => v.DeviceKey).Distinct().Count());
    }

    private static List<string> Validate(Dictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FakeRiot:CallApiKey"] = "key" })
            .AddInMemoryCollection(overrides)
            .Build();
        var settings = new FakeRiotSettings();
        settings.ReadFrom(configuration);
        var result = new FakeRiotSettingsValidator().Validate(null, settings);
        return result.Failed ? [.. result.Failures] : [];
    }
}
