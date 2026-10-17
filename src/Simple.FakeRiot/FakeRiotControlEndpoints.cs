namespace Simple.FakeRiot;

/// <summary>
/// What only the fake can do: the things that on real RIoT take a person at
/// the vehicle. Tests and AI sessions use these to bring about a situation;
/// nothing here exists on real RIoT.
/// </summary>
public static class FakeRiotControlEndpoints
{
    public const string Prefix = "/fake-riot";

    public static void MapFakeRiotControlEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(Prefix).WithTags("Fake RIoT control");

        group.MapPost("/orders/{orderId}/hang", (FakeRiotWorld world, string orderId, int? resultCode) =>
                world.Hang(orderId, resultCode ?? 0)
                    ? Results.NoContent()
                    : Results.NotFound($"No executing or held order '{orderId}'."))
            .WithName("HangFakeRiotOrder")
            .WithSummary("Puts an executing or held order into HANG (9), as a cancel on the vehicle's screen or a brake release does.");

        group.MapPut("/maps/{mapId:int}/chargers/{stationId:int}", (FakeRiotWorld world, int mapId, int stationId, ChargerRequest request) =>
                world.SetChargerConnected(mapId, stationId, request.Connected)
                    ? Results.NoContent()
                    : Results.NotFound($"Map {mapId} has no charging station {stationId}."))
            .WithName("SetFakeRiotCharger")
            .WithSummary("Plugs a charging station's charger in or out; at an unplugged charger a charge order hangs with 407802.");

        group.MapGet("/calls", (FakeRiotLoad load) => Results.Json(new { calls = load.Calls }))
            .WithName("GetFakeRiotCalls")
            .WithSummary("Calls to the RIoT API so far, by method and route pattern; unauthorised calls included.");

        group.MapDelete("/calls", (FakeRiotLoad load) =>
            {
                load.Reset();
                return Results.NoContent();
            })
            .WithName("ResetFakeRiotCalls")
            .WithSummary("Starts the call counts again from zero.");

        group.MapPut("/read-delay", (FakeRiotLoad load, ReadDelayRequest request) =>
            {
                if (request.Milliseconds < 0)
                {
                    return Results.BadRequest("The read delay must not be negative.");
                }

                load.ReadDelay = TimeSpan.FromMilliseconds(request.Milliseconds);
                return Results.NoContent();
            })
            .WithName("SetFakeRiotReadDelay")
            .WithSummary("Sets how long every read under /api waits before it is answered (real RIoT: about 168 ms at p95).");
    }

    public sealed record ChargerRequest(bool Connected);

    public sealed record ReadDelayRequest(int Milliseconds);
}
