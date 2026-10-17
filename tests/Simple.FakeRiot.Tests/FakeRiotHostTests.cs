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
        if (Directory.Exists(logDirectory))
        {
            Directory.Delete(logDirectory, recursive: true);
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
