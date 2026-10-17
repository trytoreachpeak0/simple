using System.Text.Json;

namespace Simple.FakeRiot;

/// <summary>
/// The part of RIoT's HTTP API that SIMPLE uses, at the paths and in the
/// shapes riot-sdk 0.3.1 sends and reads.
/// </summary>
public static class RiotEndpoints
{
    public const string LoginPath = "/api/auth/v1/admin/login";

    public static void MapRiotEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Authentication (BC-AUTH-001).
        app.MapPost(LoginPath, async (FakeRiotWorld world, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            return Envelope(world.Login(Text(body, "username"), Text(body, "password")));
        });
        app.MapPut("/api/auth/v1/admin/refreshToken", (FakeRiotWorld world, HttpRequest request) =>
            Envelope(world.RefreshToken(Bearer(request))));

        // Maps.
        app.MapGet("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", (FakeRiotWorld world) => Envelope(world.Maps()));
        app.MapGet("/api/imap/v1/mapInfo/stations/{mapId:int}", (FakeRiotWorld world, int mapId) => Envelope(world.Stations(mapId)));
        app.MapGet("/api/imap/v1/mapInfo/edges/{mapId:int}", (FakeRiotWorld world, int mapId) => Envelope(world.Edges(mapId)));
        app.MapGet("/api/imap/v1/mapResource/removedEdge/{mapId:int}", (int mapId) => Envelope(Answer.Ok(Array.Empty<object>())));
        app.MapGet("/api/imap/v1/mapResource/removedStation/{mapId:int}", (int mapId) => Envelope(Answer.Ok(Array.Empty<object>())));
        // Round 43 saw groups keyed by name; the fake's maps have none.
        app.MapGet("/api/imap/v1/mapEdgeGroup/all", () => Envelope(Answer.Ok(new Dictionary<string, object?>())));

        // Vehicles.
        app.MapGet("/api/task/vehicles/getAllVehicleSimpleInfo", (FakeRiotWorld world) => Envelope(world.SimpleInfo()));
        app.MapGet("/api/task/vehicles/getVehicleInfoByDeviceKey", (FakeRiotWorld world, string? key) => Envelope(world.VehicleCard(key ?? "")));
        app.MapGet("/api/task/v1/task/getVehicleInfo/{deviceKey}", (FakeRiotWorld world, string deviceKey) =>
            world.VehicleInfo(deviceKey) is { } info
                ? Results.Json(info)
                : Envelope(Answer.Unobserved("getVehicleInfo for a key RIoT does not know")));
        app.MapPost("/api/task/vehicles/updateVehicleIntegrationLevel", async (FakeRiotWorld world, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            return Envelope(world.UpdateIntegrationLevel(Strings(body, "deviceKeys"), Text(body, "serviceId")));
        });
        app.MapPost("/api/device/v1/command/sync/service/{deviceKey}/{serviceId}", async (FakeRiotWorld world, HttpRequest request, string deviceKey, string serviceId) =>
        {
            if (serviceId is not ("triggerEmergency" or "cancelEmergency"))
            {
                return Envelope(Answer.Unobserved($"device service '{serviceId}'"));
            }

            var body = await ReadBodyAsync(request);
            var complete = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("messageId", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String
                && body.TryGetProperty("mqCallback", out var callback) && callback.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("thingsProperties", out var properties) && properties.ValueKind == JsonValueKind.Object;
            return Envelope(world.SendEmergency(deviceKey, serviceId == "triggerEmergency", complete));
        });

        // Routes.
        app.MapPost("/api/task/v1/route/getRouteCostsBy", async (FakeRiotWorld world, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            return Envelope(world.RouteCosts(Int(body, "mapId"), Int(body, "stationId"), Strings(body, "deviceKeys")));
        });
        app.MapPost("/api/task/v1/route/queryNearEnd", async (FakeRiotWorld world, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            return Envelope(world.Nearest(Int(body, "mapId"), Int(body, "startStationId"), Ints(body, "endStationIds"), fromFixed: true));
        });
        app.MapPost("/api/task/v1/route/queryNearestStart", async (FakeRiotWorld world, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            return Envelope(world.Nearest(Int(body, "mapId"), Int(body, "endStationId"), Ints(body, "startStationIds"), fromFixed: false));
        });
        // Dynamic route costs: every observation answered an empty object (BC-ROUTE-001 item 3).
        app.MapGet("/api/task/v1/route/", () => Envelope(Answer.Ok(new Dictionary<string, object?>())));

        // Orders.
        app.MapPost("/api/order/v1/add/byDefaultMissions", async (FakeRiotWorld world, HttpRequest request) =>
            Envelope(world.Create(await ReadBodyAsync(request))));
        app.MapGet("/api/order/v1/orderRecord/detailByUpperId/{upperId}", (FakeRiotWorld world, string upperId) => Envelope(world.DetailByUpperId(upperId)));
        app.MapGet("/api/order/v1/orderRecord/detailByOrderId/{orderId}", (FakeRiotWorld world, string orderId) => Envelope(world.DetailByOrderId(orderId)));
        app.MapGet("/api/order/v1/orderRecord", (FakeRiotWorld world, int? pageNum, int? pageSize, int[]? filterByState) =>
            Envelope(world.List(filterByState ?? [], pageNum ?? 1, pageSize ?? 10)));
        app.MapPost("/api/order/v1/orderRecordPriorityExec", (FakeRiotWorld world, string? orderTaskKey) => Envelope(world.PriorityExec(orderTaskKey ?? "")));
        app.MapPost("/api/task/v1/order/command/{orderId}", async (FakeRiotWorld world, HttpRequest request, string orderId) =>
            Envelope(world.Command(orderId, Text(await ReadBodyAsync(request), "commandType"))));
    }

    /// <summary>
    /// Every business call needs the call key or a login token as a Bearer;
    /// without one real RIoT answers HTTP 401 (BC-AUTH-002).
    /// </summary>
    public static IApplicationBuilder UseRiotAuthentication(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.Ordinal)
                && !path.Equals(LoginPath, StringComparison.Ordinal)
                && !context.RequestServices.GetRequiredService<FakeRiotWorld>().IsAuthorized(Bearer(context.Request)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(Answer.Fail("401", "暂未登录或token已经过期").ToWire());
                return;
            }

            await next(context);
        });

    private static IResult Envelope(Answer answer) => Results.Json(answer.ToWire());

    private static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpRequest request)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.TryGetInt32(out var i) ? i : 0;

    private static List<string> Strings(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    private static List<int> Ints(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList()
            : [];
}
