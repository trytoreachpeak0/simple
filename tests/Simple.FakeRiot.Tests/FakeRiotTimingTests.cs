using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Simple.FakeRiot.Tests;

/// <summary>
/// The fake's own clock: time compression, read delay and call counters.
/// None of this exists on real RIoT, so it is tested here and not in
/// Simple.Contracts.Tests.
/// </summary>
public sealed class FakeRiotTimingTests : IDisposable
{
    private const string CallKey = "fake-riot-call-api-key-not-a-secret";
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), "simple-fakeriot-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(logDirectory))
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The shipped timings are near real RIoT's: an order from station 1 to 3
    /// (20 m) takes over 20 seconds from creation to SUCCESS. Compressed 100
    /// times, the same order is done within a second.
    /// </summary>
    [Fact]
    public async Task CompressedAHundredTimesAnOrderRunsFromCreationToSuccessWithinASecond()
    {
        await using var factory = Fake(("FakeRiot:Timing:Speedup", "100"));
        using var client = Client(factory);
        var clock = Stopwatch.StartNew();

        await CreateMoveAsync(client, "speedup-100", station: 3);
        var state = await StateWithinAsync(client, "speedup-100", TimeSpan.FromSeconds(1) - clock.Elapsed, 5);

        Assert.True(state == 5, $"倍速 100 下 1 秒内订单没有成功，最后读到状态 {state}，用时 {clock.ElapsedMilliseconds} ms。");
    }

    /// <summary>The control: without compression the same order is still on its way after a second.</summary>
    [Fact]
    public async Task UncompressedTheSameOrderIsNotDoneWithinASecond()
    {
        await using var factory = Fake();
        using var client = Client(factory);

        await CreateMoveAsync(client, "speedup-1", station: 3);
        var state = await StateWithinAsync(client, "speedup-1", TimeSpan.FromSeconds(1), 5);

        Assert.NotEqual(5, state);
    }

    /// <summary>
    /// Real RIoT's reads took about 168 ms at p95 (research/riot-review.md
    /// R39); with a read delay set, every read under /api waits that long, so a
    /// caller that reads too often is slow here too.
    /// </summary>
    [Fact]
    public async Task AReadWaitsTheReadDelay()
    {
        await using var factory = Fake(("FakeRiot:ReadDelay", "00:00:00.300"));
        using var client = Client(factory);
        await client.GetStringAsync(new Uri("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);
        var clock = Stopwatch.StartNew();

        await client.GetStringAsync(new Uri("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(290), $"读只用了 {clock.ElapsedMilliseconds} ms。");
    }

    [Fact]
    public async Task TheReadDelayCanBeChangedWhileTheFakeRuns()
    {
        await using var factory = Fake();
        using var client = Client(factory);
        using var set = await client.PutAsJsonAsync(new Uri("/fake-riot/read-delay", UriKind.Relative), new { milliseconds = 300 }, Cancel);
        var clock = Stopwatch.StartNew();

        await client.GetStringAsync(new Uri("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, set.StatusCode);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(290), $"读只用了 {clock.ElapsedMilliseconds} ms。");
    }

    /// <summary>Every call to the RIoT API is counted by endpoint, the path's variable parts left as their names.</summary>
    [Fact]
    public async Task CallsAreCountedByEndpointUntilReset()
    {
        await using var factory = Fake();
        using var client = Client(factory);
        using var anonymous = factory.CreateClient();

        foreach (var id in new[] { "a", "b", "c" })
        {
            await client.GetStringAsync(new Uri($"/api/order/v1/orderRecord/detailByUpperId/{id}", UriKind.Relative), Cancel);
        }

        await client.GetStringAsync(new Uri("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);
        using var refused = await anonymous.GetAsync(new Uri("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);
        var counted = await CallsAsync(client);
        using var reset = await client.DeleteAsync(new Uri("/fake-riot/calls", UriKind.Relative), Cancel);
        var afterReset = await CallsAsync(client);

        Assert.Equal(3, counted["GET /api/order/v1/orderRecord/detailByUpperId/{upperId}"]);
        Assert.Equal(2, counted["GET /api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson"]);
        Assert.Equal(5, counted.Values.Sum());
        Assert.Empty(afterReset);
    }

    private static async Task<Dictionary<string, long>> CallsAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync(new Uri("/fake-riot/calls", UriKind.Relative), Cancel));
        return body.RootElement.GetProperty("calls").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
    }

    private WebApplicationFactory<Program> Fake(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Simple:Logs:Directory", logDirectory);
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CallKey);
        return client;
    }

    private static async Task CreateMoveAsync(HttpClient client, string upperId, int station)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/order/v1/add/byDefaultMissions", UriKind.Relative),
            new
            {
                upperId,
                orderName = upperId,
                appointVehicleKey = "BROKERX-0000000000000000000000000000fa01",
                mission = new[] { new { type = "move", mapId = 1, destination = station } },
            },
            Cancel);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("0", body.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Reads the order's state until it is <paramref name="wanted"/> or the time is up; returns the last state read.</summary>
    private static async Task<int> StateWithinAsync(HttpClient client, string upperId, TimeSpan within, int wanted)
    {
        var deadline = Stopwatch.StartNew();
        var state = 0;
        while (deadline.Elapsed < within)
        {
            using var body = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/order/v1/orderRecord/detailByUpperId/{upperId}", UriKind.Relative), Cancel));
            state = body.RootElement.GetProperty("result").GetProperty("orderState").GetInt32();
            if (state == wanted)
            {
                break;
            }

            await Task.Delay(20, Cancel);
        }

        return state;
    }
}
