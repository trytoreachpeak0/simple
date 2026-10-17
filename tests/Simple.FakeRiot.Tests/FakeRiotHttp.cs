using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Simple.FakeRiot.Tests;

/// <summary>Plain HTTP calls to a fake in this process, for the fake's own tests.</summary>
internal static class FakeRiotHttp
{
    public const string CallKey = "fake-riot-call-api-key-not-a-secret";

    public const string Vehicle1 = "BROKERX-0000000000000000000000000000fa01";

    public const string Vehicle3 = "BROKERX-0000000000000000000000000000fa03";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CallKey);
        return client;
    }

    public static async Task<string> CreateMoveAsync(HttpClient client, string upperId, int station, string vehicleKey = Vehicle1, int mapId = 1)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/order/v1/add/byDefaultMissions", UriKind.Relative),
            new
            {
                upperId,
                orderName = upperId,
                appointVehicleKey = vehicleKey,
                mission = new[] { new { type = "move", mapId, destination = station } },
            },
            Cancel);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("0", body.RootElement.GetProperty("code").GetString());
        return body.RootElement.GetProperty("result").GetProperty("orderId").GetString()!;
    }

    /// <summary>Reads the order's state, at least once, until it is <paramref name="wanted"/> or the time is up; returns the last state read.</summary>
    public static async Task<int> StateWithinAsync(HttpClient client, string upperId, TimeSpan within, int wanted)
    {
        var deadline = Stopwatch.StartNew();
        var state = 0;
        do
        {
            using var body = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/order/v1/orderRecord/detailByUpperId/{upperId}", UriKind.Relative), Cancel));
            state = body.RootElement.GetProperty("result").GetProperty("orderState").GetInt32();
            if (state == wanted)
            {
                break;
            }

            await Task.Delay(20, Cancel);
        }
        while (deadline.Elapsed < within);

        return state;
    }
}
