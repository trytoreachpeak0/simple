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
    }

    public sealed record ChargerRequest(bool Connected);
}
