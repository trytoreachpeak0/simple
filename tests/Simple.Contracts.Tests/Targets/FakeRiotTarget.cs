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
    private readonly string logDirectory;
    private readonly RiotSession session;

    private FakeRiotTarget(WebApplicationFactory<Program> factory, string logDirectory)
    {
        this.factory = factory;
        this.logDirectory = logDirectory;
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

    /// <summary>Where the fake writes its program log; deleted, if it can be, when the target is disposed.</summary>
    public string LogDirectory => logDirectory;

    public override FakeRiotControl Control { get; }

    public override bool AllowsWrites => true;

    public override TimeSpan Patience => TimeSpan.FromSeconds(10);

    public override TimeSpan QuietWindow => TimeSpan.FromMilliseconds(600);

    public static Task<RiotTarget> StartAsync()
    {
        // The fake's program log goes to a folder of this test's own, not to %ProgramData%.
        var logDirectory = Path.Combine(Path.GetTempPath(), "simple-contracts-tests", Guid.NewGuid().ToString("N"));
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Simple:Logs:Directory", logDirectory);
            host.UseSetting("FakeRiot:CallApiKey", FakeCallApiKey);
            host.UseSetting("FakeRiot:Username", FakeUsername);
            host.UseSetting("FakeRiot:Password", FakePassword);
            host.UseSetting("FakeRiot:Timing:Tick", "00:00:00.010");
            host.UseSetting("FakeRiot:Timing:QueueToStart", "00:00:00.050");
            // Long enough that a contract that has just read EXECUTING can still catch the vehicle on its way.
            host.UseSetting("FakeRiot:Timing:MinimumTravel", "00:00:01");
            host.UseSetting("FakeRiot:Timing:SpeedMillimetresPerSecond", "1000000");
            // As on real RIoT (BC-STATE-003): long enough that a reading between arrival and SUCCESS is not a race.
            host.UseSetting("FakeRiot:Timing:ArrivalToSuccess", "00:00:01");
            host.UseSetting("FakeRiot:Timing:EmergencyLock", "00:00:00.050");
            host.UseSetting("FakeRiot:Timing:EmergencyRelease", "00:00:00.200");
            host.UseSetting("FakeRiot:Timing:FaultCodeResidue", "00:00:00.300");
            host.UseSetting("FakeRiot:Timing:ChargingTimePerPercent", "00:00:00.010");
            // Longer than the quiet window, so a contract can watch the vehicle not start.
            host.UseSetting("FakeRiot:Timing:ContinueStartDelay", "00:00:01");
        });
        return Task.FromResult<RiotTarget>(new FakeRiotTarget(factory, logDirectory));
    }

    public override RiotSession SessionWith(RiotOptions options) => new(options, factory.Server.CreateHandler());

    protected override string CallApiKey => FakeCallApiKey;

    protected override HttpClient CreatePlainClient() => factory.CreateClient();

    public override async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();
        await factory.DisposeAsync();
        try
        {
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Hosted in process, the fake may not have let go of FakeRiot.log
            // yet when the factory returns (#80, as in #69), or a scanner holds
            // it; a folder left in %TEMP% harms nothing. DirectoryNotFoundException
            // is an IOException too.
        }
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

    /// <summary>The next byDefaultMissions answers 00002, with or without making the order.</summary>
    public Task InjectCreate00002Async(bool orderMade) => PostAsync("/fake-riot/faults/create-00002", new { orderMade, times = 1 });

    /// <summary>
    /// What a person does to the vehicle itself: its battery level, the brake,
    /// the handset's manual mode, the manual charger, the emergency-stop
    /// button. Only the values given change.
    /// </summary>
    public Task SetVehicleAsync(
        string vehicleKey,
        int? battery = null,
        bool? brakeReleased = null,
        string? mode = null,
        bool? manualCharger = null,
        bool? emergencyButtonPressed = null)
    {
        var body = new Dictionary<string, object>();
        Add("battery", battery);
        Add("brakeReleased", brakeReleased);
        Add("mode", mode);
        Add("manualCharger", manualCharger);
        Add("emergencyButtonPressed", emergencyButtonPressed);
        return PutAsync($"/fake-riot/vehicles/{Uri.EscapeDataString(vehicleKey)}", body);

        void Add(string name, object? value)
        {
            if (value is not null)
            {
                body[name] = value;
            }
        }
    }

    /// <summary>A person drives the vehicle to a station without an order: with the handset in manual mode, or from Matrix.</summary>
    public Task DriveAsync(string vehicleKey, int station) =>
        PostAsync($"/fake-riot/vehicles/{Uri.EscapeDataString(vehicleKey)}/drive", new { station });

    /// <summary>A person adds a station to the map in RIoT's map editor; answers its number.</summary>
    public async Task<int> AddStationAsync(int mapId, string name) =>
        (await PostAsync($"/fake-riot/maps/{mapId}/stations", new { name, type = 1, x = 30000, y = 30000 })).GetProperty("id").GetInt32();

    /// <summary>A person deletes a station from the map in RIoT's map editor.</summary>
    public async Task DeleteStationAsync(int mapId, int stationId)
    {
        using var response = await client.DeleteAsync(
            new Uri($"/fake-riot/maps/{mapId}/stations/{stationId}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Before each read of the order list, other lines make this many orders, so the total changes while a caller pages through it.</summary>
    public Task SetOrderListChurnAsync(int ordersPerRead) => PutAsync("/fake-riot/order-list/churn", new { ordersPerRead });

    /// <summary>An order dispatched by hand on RIoT's web page: its upperId is empty (Q-043). Returns its orderId.</summary>
    public async Task<string> CreateWebOrderAsync(string vehicleKey, int mapId, int station) =>
        (await PostAsync("/fake-riot/orders/web", new { vehicleKey, mapId, station, orderName = "web" })).GetProperty("orderId").GetString()!;

    /// <summary>Makes a queued order FAILED, which the fake never does by itself.</summary>
    public async Task FailAsync(string orderId)
    {
        if (!await TryFailAsync(orderId))
        {
            throw new InvalidOperationException($"假 RIoT 不肯让订单 {orderId} 失败。");
        }
    }

    public async Task<bool> TryFailAsync(string orderId)
    {
        using var response = await client.PostAsync(
            new Uri($"/fake-riot/orders/{Uri.EscapeDataString(orderId)}/fail", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task PutAsync(string path, object body)
    {
        using var response = await client.PutAsync(
            new Uri(path, UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(body),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<System.Text.Json.JsonElement> PostAsync(string path, object body)
    {
        using var response = await client.PostAsync(
            new Uri(path, UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(body),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }
}
