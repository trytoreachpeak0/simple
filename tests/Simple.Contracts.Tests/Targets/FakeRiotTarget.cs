using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// The fake fleet manager in this test process, one per test, on its shipped
/// seed with short timings. Nothing listens on a port: the SDK reaches it
/// through the test server's handler.
/// </summary>
public sealed class FakeRiotTarget : RiotTarget
{
    public const string FakeCallApiKey = "fake-riot-call-api-key-not-a-secret";
    public const string FakeUsername = "fake-admin";
    public const string FakePassword = "fake-admin-password";

    private readonly WebApplicationFactory<Program> factory;
    private readonly RiotSession session;

    private FakeRiotTarget(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
        session = SessionWith(new RiotOptions { BaseUrl = BaseUrl, CallApiKey = FakeCallApiKey });
        Control = new FakeRiotControl(factory.CreateClient());
    }

    public override string Description => "假 RIoT（本进程内）";

    public override string BaseUrl => "http://fake-riot.test";

    /// <summary>The fake's seed: fake-vehicle-1 on map 1 at station 1; 6 is unreachable, 5 is the charger; both maps have a station 1.</summary>
    public override RiotTargetFacts Facts { get; } = new(
        VehicleKey: "BROKERX-0000000000000000000000000000fa01",
        MapId: 1,
        VehicleStation: 1,
        ReachableStation: 3,
        SecondReachableStation: 4,
        UnreachableStation: 6,
        ChargingStation: 5,
        MapsSharingAStationId: (1, 2),
        SharedStationId: 1,
        Username: FakeUsername,
        Password: FakePassword);

    public override RiotSession Session => session;

    public override FakeRiotControl Control { get; }

    public override bool AllowsWrites => true;

    public override TimeSpan Patience => TimeSpan.FromSeconds(10);

    public override TimeSpan QuietWindow => TimeSpan.FromMilliseconds(600);

    public static Task<RiotTarget> StartAsync()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("FakeRiot:CallApiKey", FakeCallApiKey);
            host.UseSetting("FakeRiot:Username", FakeUsername);
            host.UseSetting("FakeRiot:Password", FakePassword);
            host.UseSetting("FakeRiot:Timing:Tick", "00:00:00.010");
            host.UseSetting("FakeRiot:Timing:QueueToStart", "00:00:00.050");
            host.UseSetting("FakeRiot:Timing:MinimumTravel", "00:00:00.300");
            host.UseSetting("FakeRiot:Timing:SpeedMillimetresPerSecond", "1000000");
            // As on real RIoT (BC-STATE-003): long enough that a reading between arrival and SUCCESS is not a race.
            host.UseSetting("FakeRiot:Timing:ArrivalToSuccess", "00:00:01");
            host.UseSetting("FakeRiot:Timing:EmergencyLock", "00:00:00.050");
            host.UseSetting("FakeRiot:Timing:EmergencyRelease", "00:00:00.200");
        });
        return Task.FromResult<RiotTarget>(new FakeRiotTarget(factory));
    }

    public override RiotSession SessionWith(RiotOptions options) => new(options, factory.Server.CreateHandler());

    protected override string CallApiKey => FakeCallApiKey;

    protected override HttpClient CreatePlainClient() => factory.CreateClient();

    public override async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();
        await factory.DisposeAsync();
    }
}

/// <summary>The fake's control API (<c>/fake-riot</c>), for what a person would do at the vehicle.</summary>
public sealed class FakeRiotControl(HttpClient client)
{
    public async Task HangAsync(string orderId, int resultCode = 0)
    {
        using var response = await client.PostAsync(
            new Uri($"/fake-riot/orders/{Uri.EscapeDataString(orderId)}/hang?resultCode={resultCode}", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetChargerConnectedAsync(int mapId, int stationId, bool connected)
    {
        using var response = await client.PutAsync(
            new Uri($"/fake-riot/maps/{mapId}/chargers/{stationId}", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { connected }),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
