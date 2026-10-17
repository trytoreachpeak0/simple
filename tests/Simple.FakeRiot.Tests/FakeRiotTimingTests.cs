using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using static Simple.FakeRiot.Tests.FakeRiotHttp;

namespace Simple.FakeRiot.Tests;

/// <summary>
/// The fake's own clock: time compression, read delay and call counters.
/// None of this exists on real RIoT, so it is tested here and not in
/// Simple.Contracts.Tests.
/// </summary>
public sealed class FakeRiotTimingTests : IDisposable
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
        // Warm up: the first create and read pay for starting the host and compiling, not for the fake's clock.
        await CreateMoveAsync(client, "warm-up", station: 2, Vehicle3, mapId: 2);
        await StateWithinAsync(client, "warm-up", TimeSpan.Zero, 5);
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

    /// <summary>The three route queries are POSTs that only read; they wait too, whatever the letter case of the path routing matched.</summary>
    [Fact]
    public async Task ARouteQueryWaitsTheReadDelay()
    {
        await using var factory = Fake(("FakeRiot:ReadDelay", "00:00:00.300"));
        using var client = Client(factory);
        var query = new { mapId = 1, stationId = 3, deviceKeys = new[] { Vehicle1 } };
        (await client.PostAsJsonAsync(new Uri("/api/task/v1/route/getRouteCostsBy", UriKind.Relative), query, Cancel)).Dispose();
        var clock = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync(new Uri("/api/task/v1/route/GETROUTECOSTSBY", UriKind.Relative), query, Cancel);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(290), $"路线查询只用了 {clock.ElapsedMilliseconds} ms。");
    }

    /// <summary>
    /// A hold and continue with no emergency stop in between starts again at
    /// once: the 16-second start was seen only after a stop while held
    /// (BC-ORDER-020 item 3), and round 10 continued a plain hold without it.
    /// A fake choice, not a contract: real RIoT has no control experiment.
    /// </summary>
    [Fact]
    public async Task APlainHoldAndContinueStartsAgainWithoutTheLongDelay()
    {
        // Four times as fast: it executes after half a second and travels for five; the long delay would be four.
        await using var factory = Fake(("FakeRiot:Timing:Speedup", "4"));
        using var client = Client(factory);
        var orderId = await CreateMoveAsync(client, "plain-hold", station: 3);
        Assert.Equal(3, await StateWithinAsync(client, "plain-hold", TimeSpan.FromSeconds(5), 3));
        await CommandAsync(client, orderId, "CMD_ORDER_HELD");
        Assert.Equal(7, await StateWithinAsync(client, "plain-hold", TimeSpan.FromSeconds(5), 7));

        await CommandAsync(client, orderId, "CMD_ORDER_CONTINUE_FROM_HELD");
        var clock = Stopwatch.StartNew();
        var speed = 0d;
        while (speed == 0 && clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            using var info = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/task/v1/task/getVehicleInfo/{Vehicle1}", UriKind.Relative), Cancel));
            speed = info.RootElement.GetProperty("vehicle").GetProperty("speed").GetDouble();
            await Task.Delay(20, Cancel);
        }

        Assert.True(speed > 0, $"普通暂停后继续，{clock.ElapsedMilliseconds} ms 内车还没起步。");
    }

    private static async Task CommandAsync(HttpClient client, string orderId, string commandType)
    {
        using var response = await client.PostAsJsonAsync(new Uri($"/api/task/v1/order/command/{orderId}", UriKind.Relative), new { commandType }, Cancel);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("0", body.RootElement.GetProperty("code").GetString());
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
}
