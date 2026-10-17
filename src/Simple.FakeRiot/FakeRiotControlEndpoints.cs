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

        group.MapPost("/orders/web", (FakeRiotWorld world, WebOrderRequest request) =>
                world.CreateWebOrder(request.VehicleKey, request.MapId, request.Station, request.OrderName ?? "web") is { } orderId
                    ? Results.Json(new { orderId })
                    : Results.NotFound($"No vehicle '{request.VehicleKey}' or no station {request.Station} on map {request.MapId}."))
            .WithName("CreateFakeRiotWebOrder")
            .WithSummary("Dispatches an order by hand as on RIoT's web page: its upperId is the empty string (Q-043). Answers its orderId.");

        group.MapPost("/orders/{orderId}/fail", (FakeRiotWorld world, string orderId) =>
                world.Fail(orderId)
                    ? Results.NoContent()
                    : Results.NotFound($"No queued order '{orderId}'."))
            .WithName("FailFakeRiotOrder")
            .WithSummary("Makes a queued order FAILED (4), which the fake never does by itself; real RIoT is said to fail only a just-made order (ADR 0010).");

        group.MapPost("/maps/{mapId:int}/stations", (FakeRiotWorld world, int mapId, StationRequest request) =>
                world.AddStation(mapId, request.Name, request.Type, request.X, request.Y) is { } id
                    ? Results.Json(new { id })
                    : Results.NotFound($"No map {mapId}."))
            .WithName("AddFakeRiotStation")
            .WithSummary("Adds a station, as in RIoT's map editor: it gets the number after the highest the map ever had (ADR 0015). Answers its id.");

        group.MapDelete("/maps/{mapId:int}/stations/{stationId:int}", (FakeRiotWorld world, int mapId, int stationId) =>
                world.DeleteStation(mapId, stationId) switch
                {
                    StationChange.Done => Results.NoContent(),
                    StationChange.InUse => Results.Conflict($"A vehicle stands on, starts from or heads for station {stationId}."),
                    _ => Results.NotFound($"Map {mapId} has no station {stationId}."),
                })
            .WithName("DeleteFakeRiotStation")
            .WithSummary("Deletes a station and its edges, as in RIoT's map editor; its number is never given again.");

        group.MapPut("/vehicles/{key}", (FakeRiotWorld world, string key, VehicleRequest request) =>
                world.SetVehicle(key, request.Battery, request.BrakeReleased, request.Mode, request.ManualCharger, request.EmergencyButtonPressed) switch
                {
                    VehicleChange.Done => Results.NoContent(),
                    VehicleChange.UnknownVehicle => Results.NotFound($"No vehicle '{key}'."),
                    _ => Results.BadRequest("battery is 0 to 100; mode is MODE_AUTO or MODE_MANUAL."),
                })
            .WithName("SetFakeRiotVehicle")
            .WithSummary("What a person does to the vehicle: battery level, brake released, handset mode, manual charger, emergency-stop button. Only the values given change.");

        group.MapPost("/vehicles/{key}/drive", (FakeRiotWorld world, string key, DriveRequest request) =>
                !world.HasVehicle(key) ? Results.NotFound($"No vehicle '{key}'.")
                : world.Drive(key, request.Station) is { } refusal ? Results.Conflict(refusal)
                : Results.NoContent())
            .WithName("DriveFakeRiotVehicle")
            .WithSummary("A person drives the vehicle to a station without an order (handset in MODE_MANUAL, or Matrix in MODE_AUTO).");

        group.MapPut("/order-list/churn", (FakeRiotWorld world, ChurnRequest request) =>
            {
                if (request.OrdersPerRead < 0)
                {
                    return Results.BadRequest("ordersPerRead must not be negative.");
                }

                world.SetOrderListChurn(request.OrdersPerRead);
                return Results.NoContent();
            })
            .WithName("SetFakeRiotOrderListChurn")
            .WithSummary("Before every read of the order list, other lines make this many orders, so its total changes between pages (cs#573). 0 stops it.");

        group.MapPost("/faults/create-00002", (FakeRiotWorld world, Create00002Request request) =>
            {
                if (request.Times < 0)
                {
                    return Results.BadRequest("times must not be negative.");
                }

                world.InjectCreate00002(request.OrderMade, request.Times);
                return Results.NoContent();
            })
            .WithName("InjectFakeRiotCreate00002")
            .WithSummary("The next `times` creates (byDefaultMissions) answer 00002; with orderMade the order is made all the same. 0 stops it.");

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

    public sealed record StationRequest(string Name, int Type, double X, double Y);

    public sealed record VehicleRequest(int? Battery, bool? BrakeReleased, string? Mode, bool? ManualCharger, bool? EmergencyButtonPressed);

    public sealed record DriveRequest(int Station);

    public sealed record ChurnRequest(int OrdersPerRead);

    public sealed record WebOrderRequest(string VehicleKey, int MapId, int Station, string? OrderName);

    public sealed record Create00002Request(bool OrderMade, int Times = 1);
}
