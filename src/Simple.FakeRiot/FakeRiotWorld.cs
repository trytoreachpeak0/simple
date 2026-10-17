using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Simple.FakeRiot;

/// <summary>
/// The fake fleet manager's whole state: maps, vehicles and orders, behind
/// one lock, moved on by <see cref="Tick"/>. Every behaviour here stands for
/// something real RIoT was seen to do; the comments name the contract
/// (<c>BC-…</c> from the RIoT behaviour lab, <c>R…</c> from
/// research/riot-review.md §5.1). Where real RIoT's answer was never
/// observed the fake says so with <see cref="Answer.UnobservedCode"/>
/// instead of guessing.
/// </summary>
public sealed class FakeRiotWorld
{
    /// <summary>The longest upperId or orderName known to be accepted (BC-ORDER-022); real RIoT's limit lies somewhere from 52 to 68.</summary>
    public const int MaxUpperIdLength = 52;

    /// <summary>Act 78 is charging: parameter 1 starts it, 2 ends it (Q-033).</summary>
    public const int ChargeActionId = 78;

    /// <summary>The act result code real RIoT gave when the vehicle could not charge (round 24).</summary>
    public const int ChargeFailedResultCode = 407802;

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly FakeRiotSettings settings;
    private readonly Dictionary<int, MapState> maps;
    private readonly Dictionary<string, VehicleState> vehicles;
    private readonly List<OrderState> orders = [];
    private readonly HashSet<string> issuedTokens = new(StringComparer.Ordinal);
    private DateTimeOffset lastTick;
    private long nextId = 1_700_001;
    private long nextOrderNumber = 2_110_000_000_000_000_000;
    private long nextMissionId = 21_000_001;
    private long promotions;

    public FakeRiotWorld(Seed seed, IOptions<FakeRiotSettings> settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(settings);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.settings = settings.Value;
        maps = seed.Maps.ToDictionary(m => m.Id, m => new MapState(m));
        vehicles = seed.Vehicles.ToDictionary(
            v => v.DeviceKey,
            v => new VehicleState(v, maps[v.MapId]),
            StringComparer.Ordinal);
        lastTick = time.GetUtcNow();
    }

    private FakeRiotTiming Timing => settings.Timing;

    // ---- Authentication (BC-AUTH-001, BC-AUTH-002) ----

    /// <summary>The call key, or a token the admin login issued.</summary>
    public bool IsAuthorized(string? bearer)
    {
        if (string.IsNullOrEmpty(bearer))
        {
            return false;
        }

        lock (gate)
        {
            return string.Equals(bearer, settings.CallApiKey, StringComparison.Ordinal) || issuedTokens.Contains(bearer);
        }
    }

    /// <summary>A wrong password is HTTP 200 with business code 2009, not a 401 (BC-AUTH-001).</summary>
    public Answer Login(string? username, string? password)
    {
        if (string.IsNullOrEmpty(settings.Username)
            || !string.Equals(username, settings.Username, StringComparison.Ordinal)
            || !string.Equals(password, settings.Password, StringComparison.Ordinal))
        {
            return Answer.Fail("2009", "密码不正确");
        }

        return Answer.Ok(new Dictionary<string, object?> { ["tokenHead"] = "Bearer ", ["token"] = IssueToken() });
    }

    public Answer RefreshToken(string? bearer) =>
        IsAuthorized(bearer)
            ? Answer.Ok(new Dictionary<string, object?> { ["tokenHead"] = "Bearer ", ["token"] = IssueToken() })
            : Answer.Fail("401", "暂未登录或token已经过期");

    private string IssueToken()
    {
        var token = $"fake-riot-token-{Guid.NewGuid():N}";
        lock (gate)
        {
            issuedTokens.Add(token);
        }

        return token;
    }

    // ---- Maps (BC-MAP-001, BC-MAP-002, BC-MAP-003) ----

    public Answer Maps() =>
        Answer.Ok(maps.Values.Select(m => new Dictionary<string, object?> { ["id"] = m.Seed.Id, ["name"] = m.Seed.Name }).ToList());

    /// <summary>An unknown map is <c>code=0</c> with an empty list, not an error (BC-MAP-002).</summary>
    public Answer Stations(int mapId) =>
        Answer.Ok(maps.TryGetValue(mapId, out var map) ? map.Seed.Stations.Select(s => StationWire(map, s)).ToList() : []);

    /// <summary>snake_case keys, which the generated client cannot read (BC-MAP-003).</summary>
    public Answer Edges(int mapId) =>
        Answer.Ok(maps.TryGetValue(mapId, out var map) ? map.Seed.Edges.Select(e => EdgeWire(map, e)).ToList() : []);

    private static Dictionary<string, object?> StationWire(MapState map, SeedStation station) => new()
    {
        ["id"] = station.Id,
        ["name"] = station.Name,
        ["type"] = station.Type,
        ["desc"] = "",
        ["edge_id"] = map.Seed.Edges.FirstOrDefault(e => e.To == station.Id)?.Id
            ?? map.Seed.Edges.FirstOrDefault(e => e.From == station.Id)?.Id ?? 0,
        ["pos.x"] = station.X,
        ["pos.y"] = station.Y,
        ["pos.yaw"] = 0.0,
        ["station_offset"] = 0,
        ["user_define_properties"] = new Dictionary<string, object?>(),
    };

    private static Dictionary<string, object?> EdgeWire(MapState map, SeedEdge edge)
    {
        var from = map.Station(edge.From)!;
        var to = map.Station(edge.To)!;
        return new()
        {
            ["id"] = edge.Id,
            ["s_node"] = edge.From,
            ["e_node"] = edge.To,
            ["cost"] = RouteGraph.Cost(from, to),
            ["sx"] = (int)from.X,
            ["sy"] = (int)from.Y,
            ["ex"] = (int)to.X,
            ["ey"] = (int)to.Y,
            ["s_facing"] = 0.0,
            ["e_facing"] = 0.0,
            ["direction"] = 1,
            ["is_back_edge"] = false,
            ["type"] = 1,
            ["desc"] = "",
            ["limit_v"] = 0,
            ["limit_w"] = 0,
            ["user_define_properties"] = new Dictionary<string, object?>(),
        };
    }

    // ---- Vehicles ----

    public Answer SimpleInfo()
    {
        lock (gate)
        {
            return Answer.Ok(vehicles.Values
                .Select(v => new Dictionary<string, object?> { ["deviceKey"] = v.Key, ["deviceName"] = v.Name })
                .ToList());
        }
    }

    /// <summary>The vehicle card; <c>currentMap</c> is the map's name, <c>currentPosition</c> 0 off a station (BC-STATE-002).</summary>
    public Answer VehicleCard(string key)
    {
        lock (gate)
        {
            if (!vehicles.TryGetValue(key, out var v))
            {
                return Answer.Unobserved("getVehicleInfoByDeviceKey for a key RIoT does not know");
            }

            return Answer.Ok(new Dictionary<string, object?>
            {
                ["deviceKey"] = v.Key,
                ["deviceName"] = v.Name,
                ["deviceId"] = v.VehicleId,
                ["enable"] = v.Online,
                ["status"] = 1,
                ["procState"] = ProcState(v),
                ["currentMap"] = v.Map.Seed.Name,
                ["currentPosition"] = v.Station,
                ["battery"] = v.Battery,
                ["batteryState"] = v.Charging ? "CHARGING" : "NO_CHARGE",
                ["breakSwitchState"] = "MOVABLE",
                ["emergencyState"] = v.Emergency,
                ["locationState"] = "LOCATION_STATE_RUNNING",
                ["mode"] = "MODE_AUTO",
                ["movementState"] = MovementState(v),
                ["speed"] = Speed(v),
                ["lockStatus"] = 0,
                ["orderTaskId"] = v.Current?.OrderId,
            });
        }
    }

    /// <summary>
    /// The diagnostic vehicle and task halves, without the <c>{code,result}</c>
    /// envelope (R22); null for a key the fake does not know.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? VehicleInfo(string key)
    {
        lock (gate)
        {
            if (!vehicles.TryGetValue(key, out var v))
            {
                return null;
            }

            var (x, y) = Position(v);
            return new Dictionary<string, object?>
            {
                ["vehicle"] = new Dictionary<string, object?>
                {
                    ["key"] = v.Key,
                    ["name"] = v.Name,
                    ["vehicleId"] = v.VehicleId,
                    ["battery"] = v.Battery,
                    ["batteryState"] = v.Charging ? "CHARGING" : "NO_CHARGE",
                    ["breakSwitchState"] = "MOVABLE",
                    // Every executing order shows CONTROL_STATE_ERR from start to
                    // finish, emergency stop or not (BC-VEH-005 item 8).
                    ["controlState"] = v.Current is not null || v.Emergency != EmergencyStates.Ok ? "CONTROL_STATE_ERR" : "CONTROL_STATE_OK",
                    ["emergencyState"] = v.Emergency,
                    // Non-empty while stopped; real RIoT leaves some codes for 3 to 4 s after OK (BC-ORDER-020 item 7).
                    ["faultCodesList"] = v.Emergency == EmergencyStates.Ok ? Array.Empty<int>() : [11323, 11327, 11329],
                    ["locationState"] = "LOCATION_STATE_RUNNING",
                    ["mode"] = "MODE_AUTO",
                    ["movementState"] = MovementState(v),
                    ["speed"] = Speed(v),
                    ["connected"] = true,
                    ["currentStation"] = v.Station,
                    ["currentNode"] = v.Station,
                    ["noStation"] = v.Station == 0,
                    ["noNode"] = v.Station == 0,
                    ["plantModel"] = v.Map.Seed.Name,
                    ["precisePosition"] = new Dictionary<string, object?> { ["x"] = x, ["y"] = y },
                    // The vehicle's map is read from here (BC-STATE-001).
                    ["previousState"] = new Dictionary<string, object?> { ["mapName"] = v.Map.Seed.Name, ["stationNo"] = v.Station },
                },
                ["vehicleTaskInfo"] = new Dictionary<string, object?>
                {
                    ["key"] = v.Key,
                    ["name"] = v.Name,
                    ["enable"] = v.Online,
                    ["integrationLevel"] = v.Online ? "ON_LINE" : "OFF_LINE",
                    ["procState"] = ProcState(v),
                    ["processingOrder"] = v.Current is not null,
                    ["routeProgressIndex"] = -1,
                },
            };
        }
    }

    /// <summary>
    /// Only <c>enable</c> and <c>disable</c> are serviceIds; anything else is
    /// <c>00002</c> (BC-VEH-003; on real RIoT it may also damage the vehicle's
    /// dispatch state, which the fake does not copy).
    /// </summary>
    public Answer UpdateIntegrationLevel(IReadOnlyList<string> keys, string? serviceId)
    {
        if (serviceId is not ("enable" or "disable"))
        {
            return Answer.InternalError();
        }

        lock (gate)
        {
            if (keys.Count == 0 || keys.Any(k => !vehicles.ContainsKey(k)))
            {
                return Answer.Unobserved("updateVehicleIntegrationLevel without known deviceKeys");
            }

            foreach (var key in keys)
            {
                vehicles[key].Online = serviceId == "enable";
            }

            return Answer.OkWithoutResult();
        }
    }

    /// <summary>
    /// Software emergency stop and its release. The body must carry
    /// messageId, mqCallback and thingsProperties, or real RIoT answers
    /// <c>00002</c> (BC-VEH-005 item 5). Success says nothing about the
    /// vehicle: <c>emergencyState</c> changes later (about 1 s to lock,
    /// 4 to 5 s to release).
    /// </summary>
    public Answer SendEmergency(string key, bool trigger, bool bodyComplete)
    {
        if (!bodyComplete)
        {
            return Answer.InternalError();
        }

        lock (gate)
        {
            if (!vehicles.TryGetValue(key, out var v))
            {
                return Answer.Unobserved("triggerEmergency/cancelEmergency for a key RIoT does not know");
            }

            var now = time.GetUtcNow();
            if (trigger)
            {
                if (v.Emergency == EmergencyStates.Ok && v.EmergencyTarget is null)
                {
                    (v.EmergencyTarget, v.EmergencyChangeAt) = (EmergencyStates.CanRecover, now + Timing.EmergencyLock);
                }
            }
            else if (v.Emergency == EmergencyStates.CanRecover || v.EmergencyTarget == EmergencyStates.CanRecover)
            {
                (v.EmergencyTarget, v.EmergencyChangeAt) = (EmergencyStates.Ok, now + Timing.EmergencyRelease);
            }

            return Answer.Ok(new Dictionary<string, object?>
            {
                ["client"] = new Dictionary<string, object?> { ["deviceKey"] = key, ["productKey"] = "standard.oasis.300ul" },
                ["code"] = 200,
                ["data"] = new Dictionary<string, object?> { ["reason"] = "RESPONSE_OK", ["responseState"] = "RESPONSE_OK", ["responseCode"] = "0" },
                ["fType"] = "SERVICE_CALL_REPLY",
            });
        }
    }

    // ---- Routes (BC-ROUTE-001, BC-ROUTE-002) ----

    /// <summary>Millimetres; an unreachable station, or a vehicle on another map, is <c>-1</c> inside a successful answer.</summary>
    public Answer RouteCosts(int mapId, int stationId, IReadOnlyList<string> keys)
    {
        lock (gate)
        {
            var costs = keys.Select(key =>
            {
                var cost = vehicles.TryGetValue(key, out var v) && v.Map.Seed.Id == mapId
                    ? v.Map.Graph.CostBetween(v.LastStation, stationId)
                    : null;
                return new Dictionary<string, object?>
                {
                    ["deviceKey"] = key,
                    ["costs"] = cost is { } c ? (long)c : -1L,
                    ["message"] = cost is null ? "unreachable" : "ok",
                };
            }).ToList();
            return Answer.Ok(new Dictionary<string, object?> { ["mapId"] = mapId, ["stationId"] = stationId, ["deviceCostsList"] = costs });
        }
    }

    /// <summary>One unreachable candidate makes the whole query a NullPointerException (BC-ROUTE-002); so does an empty list (BC-ROUTE-001).</summary>
    public Answer Nearest(int mapId, int fixedStation, IReadOnlyList<int> candidates, bool fromFixed)
    {
        if (!maps.TryGetValue(mapId, out var map) || candidates.Count == 0)
        {
            return Answer.InternalError();
        }

        var ranked = candidates
            .Select(c => (Station: c, Cost: fromFixed ? map.Graph.CostBetween(fixedStation, c) : map.Graph.CostBetween(c, fixedStation)))
            .ToList();
        return ranked.Any(r => r.Cost is null)
            ? Answer.InternalError()
            : Answer.Ok(ranked.OrderBy(r => r.Cost).First().Station);
    }

    // ---- Orders ----

    /// <summary>
    /// <c>byDefaultMissions</c> (BC-ORDER-001). The order is created whatever
    /// the vehicle's state, and stays queued while it cannot start (R2).
    /// </summary>
    public Answer Create(JsonElement body)
    {
        var upperId = Text(body, "upperId");
        var orderName = Text(body, "orderName") ?? upperId;
        var appoint = Text(body, "appointVehicleKey") ?? "";
        if (string.IsNullOrEmpty(upperId) || !body.TryGetProperty("mission", out var missionArray) || missionArray.ValueKind != JsonValueKind.Array)
        {
            return Answer.Unobserved("byDefaultMissions without upperId or mission");
        }

        // 69 and 71 characters were refused with 00002 and no order (BC-ORDER-022).
        if (upperId.Length > MaxUpperIdLength || orderName!.Length > MaxUpperIdLength)
        {
            return Answer.InternalError();
        }

        var missions = new List<Mission>();
        foreach (var m in missionArray.EnumerateArray())
        {
            missions.Add(new Mission(
                Text(m, "type") ?? "",
                Int(m, "mapId") ?? 0,
                Int(m, "destination") ?? 0,
                Int(m, "actionId") ?? 0,
                Int(m, "actionParam1") ?? 0,
                Int(m, "actionParam2") ?? 0,
                m.TryGetProperty("speed", out var speed) && speed.TryGetDouble(out var s) ? s : null));
        }

        lock (gate)
        {
            // A repeated upperId is refused and the first order is not returned (BC-ORDER-004).
            if (orders.Any(o => o.UpperId == upperId))
            {
                return Answer.Fail("0610008", "订单已存在");
            }

            SeedStation? lastStop = null;
            foreach (var m in missions)
            {
                if (m.Type == "move")
                {
                    lastStop = maps.TryGetValue(m.MapId, out var map) ? map.Station(m.Destination) : null;
                    if (lastStop is null)
                    {
                        return Answer.Fail("0660003", "目的地站点不存在");
                    }
                }
                else if (m.Type == "act" && m.ActionId == ChargeActionId && m.ActionParam1 == 1 && lastStop is { Type: not 2 })
                {
                    // A charge at a station without an entry and exit point is
                    // refused with 10008 (Q-033); real RIoT's message text was not recorded.
                    return Answer.Fail("10008", "站点没有配置进入退出点");
                }
                else if (m.Type is not ("move" or "act"))
                {
                    return Answer.Unobserved($"a mission of type '{m.Type}'");
                }
            }

            if (missions.Count == 0)
            {
                return Answer.Unobserved("byDefaultMissions with no mission");
            }

            var now = time.GetUtcNow();
            var order = new OrderState(nextId++, $"order-{(nextOrderNumber += 4096).ToString(CultureInfo.InvariantCulture)}", upperId, orderName!, appoint, missions, now);
            foreach (var mission in missions)
            {
                mission.Id = nextMissionId++;
            }

            orders.Add(order);
            return Answer.Ok(OrderWire(order));
        }
    }

    /// <summary>An upperId RIoT does not know is HTTP 200, <c>code=0</c> and no <c>result</c>, never 404 (R10).</summary>
    public Answer DetailByUpperId(string upperId)
    {
        lock (gate)
        {
            return orders.FirstOrDefault(o => o.UpperId == upperId) is { } order ? Answer.Ok(OrderWire(order)) : Answer.OkWithoutResult();
        }
    }

    /// <summary>The same for an orderId (R11).</summary>
    public Answer DetailByOrderId(string orderId)
    {
        lock (gate)
        {
            return Find(orderId) is { } order ? Answer.Ok(OrderWire(order)) : Answer.OkWithoutResult();
        }
    }

    /// <summary>
    /// The order list, newest first, filtered by state. Real RIoT ignores an
    /// <c>appointVehicleKey</c> query parameter, so the fake does not read one (BC-ORDER-013).
    /// </summary>
    public Answer List(IReadOnlyCollection<int> states, int pageNum, int pageSize)
    {
        lock (gate)
        {
            var matching = orders.Where(o => states.Count == 0 || states.Contains(o.State)).OrderByDescending(o => o.Id).ToList();
            var page = Math.Max(pageNum, 1);
            var size = Math.Max(pageSize, 1);
            return Answer.Ok(new Dictionary<string, object?>
            {
                ["current"] = page,
                ["size"] = size,
                ["total"] = matching.Count,
                ["pages"] = (matching.Count + size - 1) / size,
                ["records"] = matching.Skip((page - 1) * size).Take(size).Select(OrderWire).ToList(),
            });
        }
    }

    /// <summary>
    /// Order commands (BC-ORDER-003, -006, -010, -015, -020). A command's
    /// success says only that it was accepted; callers read the order back (R24).
    /// </summary>
    public Answer Command(string orderId, string? commandType)
    {
        lock (gate)
        {
            if (Find(orderId) is not { } order)
            {
                return Answer.Unobserved("an order command for an orderId RIoT does not know");
            }

            var vehicle = order.State is OrderStates.Executing or OrderStates.Held or OrderStates.Hang ? vehicles[order.AppointVehicleKey] : null;
            switch (commandType, order.State)
            {
                case ("CMD_ORDER_CANCEL", OrderStates.Queueing or OrderStates.Executing or OrderStates.Held or OrderStates.Hang):
                    // HANG -> CANCELLED is the product owner's statement, not
                    // observed (ADR 0010; U3 in research/riot-review.md).
                    Finish(order, OrderStates.Cancelled);
                    return Answer.OkWithoutResult();
                case ("CMD_ORDER_HELD", OrderStates.Executing):
                    order.State = OrderStates.Held;
                    order.Touch(time.GetUtcNow());
                    return Answer.OkWithoutResult();
                case ("CMD_ORDER_CONTINUE_FROM_HELD", OrderStates.Held):
                    // Accepted even while the vehicle is emergency stopped; the
                    // order is executing again but the stop holds the vehicle (BC-ORDER-020 item 4).
                    order.State = OrderStates.Executing;
                    order.Touch(time.GetUtcNow());
                    return Answer.OkWithoutResult();
                case ("CMD_ORDER_CONTINUE_FROM_HELD", OrderStates.Executing):
                    return Answer.Fail("100020", "订单不是暂停状态");
                case ("CMD_ORDER_CONTINUE_FROM_HANG" or "CMD_ORDER_JUMP_FROM_HANG", OrderStates.Hang) when vehicle!.Emergency == EmergencyStates.Ok:
                    order.State = OrderStates.Executing;
                    order.Touch(time.GetUtcNow());
                    return Answer.OkWithoutResult();
                case ("CMD_ORDER_CONTINUE_FROM_HANG" or "CMD_ORDER_JUMP_FROM_HANG", OrderStates.Hang or OrderStates.Executing or OrderStates.Held):
                    // Refused outside HANG, and while the vehicle cannot go on (Q-035 item 4).
                    return Answer.Fail("100021", "订单不是挂起状态或车辆不能继续");
                case ("CMD_ORDER_REJECTED", OrderStates.Executing):
                    return Answer.Fail("10015", "订单无法移出队列");
                case ("CMD_ORDER_CONTINUE_FROM_REJECTED", OrderStates.Executing):
                    return Answer.Fail("10016", "订单不是拒绝状态");
                default:
                    return Answer.Unobserved($"{commandType} on an order in state {order.State}");
            }
        }
    }

    /// <summary>Moves a queued order ahead of the other queued orders of its vehicle (BC-ORDER-014).</summary>
    public Answer PriorityExec(string orderId)
    {
        lock (gate)
        {
            if (Find(orderId) is not { State: OrderStates.Queueing } order)
            {
                return Answer.Unobserved("orderRecordPriorityExec on an order that is unknown or not queued");
            }

            order.Promotion = ++promotions;
            return Answer.OkWithoutResult();
        }
    }

    // ---- What only the fake can do: things a person does at the vehicle ----

    /// <summary>Puts an executing or held order into HANG, as a cancel on the vehicle's own screen or a brake release would (BC-ORDER-015).</summary>
    public bool Hang(string orderId, int resultCode)
    {
        lock (gate)
        {
            if (Find(orderId) is not { State: OrderStates.Executing or OrderStates.Held } order)
            {
                return false;
            }

            order.State = OrderStates.Hang;
            if (order.ExecutingIndex >= 0 && order.ExecutingIndex < order.Missions.Count)
            {
                order.Missions[order.ExecutingIndex].ResultCode = resultCode;
            }

            order.Touch(time.GetUtcNow());
            return true;
        }
    }

    /// <summary>Plugs a charging station's charger in or out; a vehicle at an unplugged charger cannot charge (round 24).</summary>
    public bool SetChargerConnected(int mapId, int stationId, bool connected)
    {
        lock (gate)
        {
            if (!maps.TryGetValue(mapId, out var map) || map.Station(stationId) is not { Type: 2 })
            {
                return false;
            }

            if (connected)
            {
                map.DisconnectedChargers.Remove(stationId);
            }
            else
            {
                map.DisconnectedChargers.Add(stationId);
            }

            return true;
        }
    }

    // ---- Time ----

    /// <summary>Moves every vehicle and order on by the time since the last tick.</summary>
    public void Tick()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var elapsed = now - lastTick;
            lastTick = now;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            foreach (var v in vehicles.Values)
            {
                if (v.EmergencyTarget is { } target && now >= v.EmergencyChangeAt)
                {
                    v.Emergency = target;
                    v.EmergencyTarget = null;
                }

                // An emergency stop holds the vehicle; it does not put the
                // order into HANG (BC-ORDER-015 item 1).
                if (v.Current is { State: OrderStates.Executing } order && v.Emergency == EmergencyStates.Ok)
                {
                    Advance(v, order, elapsed, now);
                }
            }

            foreach (var v in vehicles.Values.Where(v => v.Current is null))
            {
                StartNext(v, now);
            }
        }
    }

    /// <summary>
    /// Starts the vehicle's first queued order if the vehicle may take it.
    /// Offline, emergency stopped or with no route to the first destination,
    /// it stays queued and never fails (BC-ORDER-007, -012, -016, BC-VEH-004).
    /// </summary>
    private void StartNext(VehicleState v, DateTimeOffset now)
    {
        if (!v.Online || v.Emergency != EmergencyStates.Ok || v.EmergencyTarget is not null)
        {
            return;
        }

        var next = orders
            .Where(o => o.State == OrderStates.Queueing && o.AppointVehicleKey == v.Key && now - o.CreatedAt >= Timing.QueueToStart)
            .OrderByDescending(o => o.Promotion)
            .ThenBy(o => o.Id)
            .FirstOrDefault();
        if (next is null)
        {
            return;
        }

        if (next.Missions.FirstOrDefault(m => m.Type == "move") is { } firstMove
            && (firstMove.MapId != v.Map.Seed.Id || v.Map.Graph.CostBetween(v.LastStation, firstMove.Destination) is null))
        {
            return;
        }

        // Leaving a charger: RIoT puts act(78,2,0) first in the next order (Q-033).
        if (v.Charging)
        {
            next.Missions.Insert(0, new Mission("act", 0, 0, ChargeActionId, 2, 0, null) { Id = nextMissionId++ });
        }

        next.State = OrderStates.Executing;
        next.ExecuteVehicleKey = v.Key;
        next.ExecutingIndex = 0;
        next.ExecutedAt = now;
        next.Touch(now);
        v.Current = next;
    }

    private void Advance(VehicleState v, OrderState order, TimeSpan elapsed, DateTimeOffset now)
    {
        var budget = elapsed;
        while (order.State == OrderStates.Executing)
        {
            if (order.ExecutingIndex >= order.Missions.Count)
            {
                // Arrived; the order turns SUCCESS about a second later (BC-STATE-003).
                order.Finishing += budget;
                if (order.Finishing >= Timing.ArrivalToSuccess)
                {
                    Finish(order, OrderStates.Success);
                }

                return;
            }

            var mission = order.Missions[order.ExecutingIndex];
            if (mission.Type == "move")
            {
                if (mission.MissionState == 0)
                {
                    var cost = v.Map.Graph.CostBetween(v.LastStation, mission.Destination);
                    if (cost is null)
                    {
                        return; // no route from here: the vehicle waits
                    }

                    mission.MissionState = 1;
                    v.TravelFrom = v.LastStation;
                    v.TravelNeeded = cost == 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(Math.Max(Timing.MinimumTravel.TotalSeconds, cost.Value / Timing.SpeedMillimetresPerSecond));
                    v.Travelled = TimeSpan.Zero;
                    // Off any station while it moves (BC-STATE-002).
                    v.Station = cost == 0 ? mission.Destination : 0;
                }

                v.Travelled += budget;
                if (v.Travelled < v.TravelNeeded)
                {
                    return;
                }

                budget = v.Travelled - v.TravelNeeded;
                v.Station = v.LastStation = mission.Destination;
                v.TravelNeeded = v.Travelled = TimeSpan.Zero;
                CompleteMission(order, mission, 900);
            }
            else if (mission is { Type: "act", ActionId: ChargeActionId, ActionParam1: 1 })
            {
                var station = v.Map.Station(v.Station);
                if (station is not { Type: 2 } || v.Map.DisconnectedChargers.Contains(station.Id))
                {
                    // Real RIoT retried a few times before HANG (round 24); the fake goes straight there.
                    mission.ResultCode = ChargeFailedResultCode;
                    order.State = OrderStates.Hang;
                    order.Touch(now);
                    return;
                }

                v.Charging = true;
                CompleteMission(order, mission, 0);
            }
            else
            {
                if (mission is { Type: "act", ActionId: ChargeActionId, ActionParam1: 2 })
                {
                    v.Charging = false;
                }

                CompleteMission(order, mission, 0);
            }
        }
    }

    private static void CompleteMission(OrderState order, Mission mission, int resultCode)
    {
        mission.MissionState = 2;
        mission.ResultCode = resultCode;
        order.ExecutingIndex++;
    }

    private void Finish(OrderState order, int state)
    {
        order.State = state;
        order.DoneAt = time.GetUtcNow();
        order.Touch(order.DoneAt.Value);
        foreach (var m in order.Missions.Where(m => m.MissionState is 0 or 1))
        {
            m.MissionState = state == OrderStates.Cancelled ? 4 : m.MissionState;
        }

        // Cancelled midway, the vehicle stays where it is: off a station if it was moving.
        foreach (var v in vehicles.Values.Where(v => ReferenceEquals(v.Current, order)))
        {
            v.Current = null;
            v.TravelNeeded = v.Travelled = TimeSpan.Zero;
        }
    }

    // ---- Derived vehicle facts ----

    private static string ProcState(VehicleState v) => v.Current switch
    {
        null => "IDLE",
        { State: OrderStates.Held } => "USER_FORCE_IDLE",
        { State: OrderStates.Hang } => "INNER_FORCE_IDLE",
        var o when o.ExecutingIndex >= o.Missions.Count => "AWAITING_ORDER",
        _ => "PROCESSING_ORDER",
    };

    /// <summary>A stopped vehicle with an executing order still reads MT_RUNNING, at speed 0 (R28).</summary>
    private static string MovementState(VehicleState v) => v.Current switch
    {
        { State: OrderStates.Held } => "MT_PAUSED",
        { State: OrderStates.Executing } when v.TravelNeeded > TimeSpan.Zero => "MT_RUNNING",
        _ => "MT_FINISHED",
    };

    private double Speed(VehicleState v) =>
        v.Current is { State: OrderStates.Executing } && v.TravelNeeded > TimeSpan.Zero && v.Emergency == EmergencyStates.Ok
            ? Timing.SpeedMillimetresPerSecond / 1000
            : 0;

    private static (double X, double Y) Position(VehicleState v)
    {
        var at = v.Map.Station(v.Station != 0 ? v.Station : v.LastStation) ?? v.Map.Seed.Stations[0];
        if (v.Station != 0 || v.TravelNeeded <= TimeSpan.Zero || v.Current?.CurrentMission is not { Type: "move" } move
            || v.Map.Station(move.Destination) is not { } to || v.Map.Station(v.TravelFrom) is not { } from)
        {
            return (at.X, at.Y);
        }

        var share = Math.Clamp(v.Travelled / v.TravelNeeded, 0, 1);
        return (Math.Round(from.X + ((to.X - from.X) * share)), Math.Round(from.Y + ((to.Y - from.Y) * share)));
    }

    // ---- Wire shapes ----

    private Dictionary<string, object?> OrderWire(OrderState o)
    {
        var lastMove = o.Missions.LastOrDefault(m => m.Type == "move");
        var endStation = lastMove is null ? null : maps.GetValueOrDefault(lastMove.MapId)?.Station(lastMove.Destination);
        return new()
        {
            ["id"] = o.Id,
            ["orderId"] = o.OrderId,
            ["upperId"] = o.UpperId,
            ["orderName"] = o.OrderName,
            ["orderState"] = o.State,
            ["orderType"] = 1,
            ["appointVehicleKey"] = o.AppointVehicleKey,
            // A queued order shows no executing vehicle: "--" (BC-ORDER-013).
            ["executeVehicleKey"] = o.ExecuteVehicleKey ?? "--",
            ["executeVehicleName"] = o.ExecuteVehicleKey is { } key ? vehicles[key].Name : "0",
            ["executingIndex"] = o.ExecutingIndex,
            ["endStationNo"] = lastMove?.Destination ?? 0,
            ["endStationName"] = endStation?.Name ?? "",
            ["startStationNo"] = lastMove?.Destination ?? 0,
            ["startStationName"] = endStation?.Name ?? "",
            ["isAppointEnable"] = 1,
            ["isDeleted"] = 0,
            ["lockStatus"] = 0,
            ["priority"] = 0,
            // Jumps to 100 as soon as the order executes; useless as progress (R16).
            ["progress"] = o.State == OrderStates.Queueing ? 0 : 100,
            ["source"] = "FMS",
            ["createdBy"] = "fake-riot",
            ["failReason"] = "",
            ["createTime"] = Local(o.CreatedAt),
            ["updateTime"] = Local(o.UpdatedAt),
            ["missions"] = o.Missions.Select((m, index) => new Dictionary<string, object?>
            {
                ["id"] = m.Id,
                ["orderId"] = o.Id,
                ["executingIndex"] = index,
                ["type"] = m.Type,
                ["mapId"] = m.MapId,
                ["destination"] = m.Destination,
                ["actionId"] = m.ActionId,
                ["actionParam1"] = m.ActionParam1,
                ["actionParam2"] = m.ActionParam2,
                ["missionState"] = m.MissionState,
                ["resultCode"] = m.ResultCode,
                ["resultStr"] = "",
                ["speed"] = m.Speed ?? 0,
            }).ToList(),
        };
    }

    private OrderState? Find(string orderId) => orders.FirstOrDefault(o => o.OrderId == orderId);

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    private sealed class MapState(SeedMap seed)
    {
        public SeedMap Seed { get; } = seed;

        public RouteGraph Graph { get; } = new(seed);

        public HashSet<int> DisconnectedChargers { get; } = [];

        public SeedStation? Station(int id) => Seed.Stations.FirstOrDefault(s => s.Id == id);
    }

    private sealed class VehicleState(SeedVehicle seed, MapState map)
    {
        public string Key { get; } = seed.DeviceKey;

        public string Name { get; } = seed.Name;

        public int VehicleId { get; } = seed.VehicleId;

        public MapState Map { get; } = map;

        public int Battery { get; } = seed.Battery;

        /// <summary>The station it is on; 0 off a station (BC-STATE-002).</summary>
        public int Station { get; set; } = seed.Station;

        /// <summary>The station it last stood on, where its routes start.</summary>
        public int LastStation { get; set; } = seed.Station;

        public int TravelFrom { get; set; }

        public TimeSpan TravelNeeded { get; set; }

        public TimeSpan Travelled { get; set; }

        public bool Charging { get; set; }

        public bool Online { get; set; } = true;

        public string Emergency { get; set; } = EmergencyStates.Ok;

        public string? EmergencyTarget { get; set; }

        public DateTimeOffset EmergencyChangeAt { get; set; }

        /// <summary>Its executing, held or hung order.</summary>
        public OrderState? Current { get; set; }
    }

    private sealed class OrderState(long id, string orderId, string upperId, string orderName, string appointVehicleKey, List<Mission> missions, DateTimeOffset createdAt)
    {
        public long Id { get; } = id;

        public string OrderId { get; } = orderId;

        public string UpperId { get; } = upperId;

        public string OrderName { get; } = orderName;

        public string AppointVehicleKey { get; } = appointVehicleKey;

        public List<Mission> Missions { get; } = missions;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset UpdatedAt { get; private set; } = createdAt;

        public int State { get; set; } = OrderStates.Queueing;

        public string? ExecuteVehicleKey { get; set; }

        public int ExecutingIndex { get; set; } = -1;

        public DateTimeOffset? ExecutedAt { get; set; }

        public DateTimeOffset? DoneAt { get; set; }

        public TimeSpan Finishing { get; set; }

        public long Promotion { get; set; }

        public Mission? CurrentMission => ExecutingIndex >= 0 && ExecutingIndex < Missions.Count ? Missions[ExecutingIndex] : null;

        public void Touch(DateTimeOffset at) => UpdatedAt = at;
    }

    private sealed class Mission(string type, int mapId, int destination, int actionId, int actionParam1, int actionParam2, double? speed)
    {
        public long Id { get; set; }

        public string Type { get; } = type;

        public int MapId { get; } = mapId;

        public int Destination { get; } = destination;

        public int ActionId { get; } = actionId;

        public int ActionParam1 { get; } = actionParam1;

        public int ActionParam2 { get; } = actionParam2;

        public double? Speed { get; } = speed;

        /// <summary>0 waiting, 1 running, 2 done, 4 cancelled (BC-STATE-003, BC-ORDER-003).</summary>
        public int MissionState { get; set; }

        public int ResultCode { get; set; }
    }
}

/// <summary>RIoT's order states (R12): 8 SUSPENDED is not produced by the fake, its meaning is unknown (U1).</summary>
public static class OrderStates
{
    public const int Queueing = 1;
    public const int Cancelled = 2;
    public const int Executing = 3;
    public const int Failed = 4;
    public const int Success = 5;
    public const int Deleted = 6;
    public const int Held = 7;
    public const int Hang = 9;
}

public static class EmergencyStates
{
    public const string Ok = "OK";
    public const string CanRecover = "CAN_RECOVER";
    public const string CanNotRecover = "CAN_NOT_RECOVER";
}
