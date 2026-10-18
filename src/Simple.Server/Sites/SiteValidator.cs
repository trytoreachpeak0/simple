using System.Globalization;
using Simple.Server.Riot;

namespace Simple.Server.Sites;

/// <summary>One thing wrong with a site configuration: where, and what, in Chinese for the person who fixes it.</summary>
/// <param name="Where">For example <c>vehicles[agv01].chargers[0]</c>, or a file position.</param>
public sealed record SiteProblem(string Where, string Message)
{
    public override string ToString() => $"{Where}: {Message}";
}

/// <summary>
/// Checks a whole site configuration against the rules of
/// docs/rules-overview.md §13 and the fleet manager's maps, and lists every
/// problem, not only the first. A configuration with any problem is not taken
/// (ADR 0017). Besides the ADRs' rules it checks that every name it refers to
/// exists and is defined once, and that a vehicle's slots and IO point map fit
/// together, since a configuration that breaks these cannot be read the way it
/// was meant.
/// </summary>
public static class SiteValidator
{
    public static IReadOnlyList<SiteProblem> Validate(SiteConfiguration configuration, FleetMaps maps)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(maps);
        var check = new Check(configuration, maps);
        check.Codes();
        check.TaskTypes();
        check.Zones();
        check.Areas();
        check.FixedStations();
        check.Vehicles();
        check.Switches();
        check.StationUses();
        check.Parameters();
        return check.Problems;
    }

    private sealed class Check(SiteConfiguration c, FleetMaps maps)
    {
        public List<SiteProblem> Problems { get; } = [];

        private HashSet<string> ProjectCodes { get; } = [.. c.Projects.Select(p => p.Code)];

        private Dictionary<string, DispatchZone> ZonesByCode { get; } = c.Zones.DistinctBy(z => z.Code).ToDictionary(z => z.Code);

        private HashSet<string> TaskTypeCodes { get; } = [.. c.TaskTypes.Select(t => t.Code)];

        public void Codes()
        {
            Once(c.Projects.Select(p => p.Code), "projects", "项目");
            Once(c.Zones.Select(z => z.Code), "zones", "派车区");
            Once(c.TaskTypes.Select(t => t.Code), "task-types", "任务类型");
            Once(c.Vehicles.Select(v => v.Id), "vehicles", "车辆标识");
        }

        // A task type has a machine at one end and a fixed station at the other (ADR 0006).
        public void TaskTypes()
        {
            foreach (var t in c.TaskTypes.Where(t => t.Pickup == t.Unload))
            {
                Add(
                    $"task-types[{t.Code}]",
                    $"第一版只支持一端是机台、一端是固定站点的任务类型；它两端都是{(t.Pickup == TaskEnd.Machine ? "机台" : "固定站点")}（ADR 0006）。");
            }
        }

        public void Zones()
        {
            foreach (var z in c.Zones)
            {
                if (!ProjectCodes.Contains(z.Project))
                {
                    Add($"zones[{z.Code}].project", $"派车区归属的项目 {z.Project} 不在项目清单里。");
                }

                if (z.WaitCap <= TimeSpan.Zero)
                {
                    Add($"zones[{z.Code}].wait-cap", "等待封顶的时长 T 要大于 0（ADR 0036）。");
                }
            }
        }

        // An area belongs to one zone in the whole of SIMPLE (ADR 0015); areas
        // compare after trimming only (ADR 0002).
        public void Areas()
        {
            var slotGroupNames = c.Vehicles.SelectMany(v => v.SlotGroups).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
            var seen = new Dictionary<string, AreaSetting>(StringComparer.Ordinal);
            foreach (var a in c.Areas)
            {
                var area = a.Area.Trim();
                if (seen.TryGetValue(area, out var first))
                {
                    Add(
                        $"areas[{area}]",
                        $"区域号 {area} 在配置里出现了两次，分别归派车区 {first.Zone ?? "（没写）"} 和 {a.Zone ?? "（没写）"}；区域号在整套 SIMPLE 里只能归一个派车区（ADR 0015）。");
                    continue;
                }

                seen[area] = a;
                if (a.Zone is { } zone && !ZonesByCode.ContainsKey(zone))
                {
                    Add($"areas[{area}].zone", $"派车区 {zone} 不在派车区清单里。");
                }

                if (a.DoorSide is { } side && !slotGroupNames.Contains(side))
                {
                    Add($"areas[{area}].door-side", $"开门侧 {side} 不是车辆清单里任何一台车的仓位分组（现有：{Join(slotGroupNames)}）。");
                }
            }
        }

        // A project has at most one fixed station per map and task type, and
        // a station is the fixed station of one project and task type (ADR 0015).
        public void FixedStations()
        {
            var byPlace = new HashSet<(string, string, int)>();
            var byStation = new Dictionary<StationRef, FixedStation>();
            foreach (var f in c.FixedStations)
            {
                var at = $"fixed-stations[{f.Project}/{f.TaskType}/{f.Station.Map.ToString(CultureInfo.InvariantCulture)}]";
                if (!ProjectCodes.Contains(f.Project))
                {
                    Add($"{at}.project", $"项目 {f.Project} 不在项目清单里。");
                }

                if (!TaskTypeCodes.Contains(f.TaskType))
                {
                    Add($"{at}.task-type", $"任务类型 {f.TaskType} 不在任务类型清单里。");
                }

                if (!byPlace.Add((f.Project, f.TaskType, f.Station.Map)))
                {
                    Add(at, $"项目 {f.Project} 的任务类型 {f.TaskType} 在地图 {f.Station.Map} 上配了不止一个固定站点；每个项目、每张地图、每个任务类型最多一个（ADR 0015）。");
                }
                else if (byStation.TryGetValue(f.Station, out var other))
                {
                    Add(at, $"{f.Station} 已经是项目 {other.Project} 任务类型 {other.TaskType} 的固定站点；一个站点只做一个项目、一个任务类型的固定站点（ADR 0015）。");
                }
                else
                {
                    byStation[f.Station] = f;
                }

                OnMap(at, f.Station);
                if (f.AllLoaded is { } allLoaded && allLoaded <= TimeSpan.Zero)
                {
                    Add($"{at}.all-loaded", "\"全部装完\"的时长要大于 0（ADR 0035）；不单独配就不写。");
                }
            }
        }

        public void Vehicles()
        {
            var deviceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            var takers = new Dictionary<(string Project, string TaskType), string>();
            var waitingPoints = new Dictionary<StationRef, string>();
            foreach (var v in c.Vehicles)
            {
                var at = $"vehicles[{v.Id}]";
                if (!deviceKeys.TryAdd(v.RiotDeviceKey, v.Id))
                {
                    Add($"{at}.riot-device-key", $"车队调度系统里的车 {v.RiotDeviceKey} 已经对应车 {deviceKeys[v.RiotDeviceKey]}（ADR 0007）。");
                }

                if (!ProjectCodes.Contains(v.Project))
                {
                    Add($"{at}.project", $"项目 {v.Project} 不在项目清单里。");
                }

                // A vehicle's zone belongs to the vehicle's project (ADR 0015, 0023).
                if (!ZonesByCode.TryGetValue(v.Zone, out var zone))
                {
                    Add($"{at}.zone", $"派车区 {v.Zone} 不在派车区清单里。");
                }
                else if (zone.Project != v.Project)
                {
                    Add($"{at}.zone", $"派车区 {v.Zone} 属于项目 {zone.Project}，车属于项目 {v.Project}；车的派车区要属于车所属的项目（ADR 0023）。");
                }

                // Within a project a task type belongs to one vehicle (ADR 0023).
                foreach (var t in v.TaskTypes)
                {
                    if (!TaskTypeCodes.Contains(t))
                    {
                        Add($"{at}.task-types", $"任务类型 {t} 不在任务类型清单里。");
                    }
                    else if (!takers.TryAdd((v.Project, t), v.Id))
                    {
                        Add($"{at}.task-types", $"项目 {v.Project} 里任务类型 {t} 已经归车 {takers[(v.Project, t)]}；同一项目里一个任务类型只归一台车（ADR 0023）。");
                    }
                }

                // At least one charger (ADR 0020), one waiting point of its own (ADR 0021).
                if (v.Chargers.Count == 0)
                {
                    Add($"{at}.chargers", "每台车至少要列一个充电桩（ADR 0020）。");
                }

                for (var i = 0; i < v.Chargers.Count; i++)
                {
                    OnMap($"{at}.chargers[{i.ToString(CultureInfo.InvariantCulture)}]", v.Chargers[i]);
                }

                OnMap($"{at}.waiting-point", v.WaitingPoint);
                if (!waitingPoints.TryAdd(v.WaitingPoint, v.Id))
                {
                    Add($"{at}.waiting-point", $"{v.WaitingPoint} 已经是车 {waitingPoints[v.WaitingPoint]} 的等待点；两台车不能配同一个等待点（ADR 0021）。");
                }

                Slots(at, v);
            }
        }

        // A switched-on task type is taken by a vehicle of the project, enabled or not (ADR 0023).
        public void Switches()
        {
            foreach (var s in c.TaskTypeSwitches)
            {
                var at = $"task-type-switches[{s.Project}/{s.TaskType}]";
                if (!ProjectCodes.Contains(s.Project) || !TaskTypeCodes.Contains(s.TaskType))
                {
                    Add(at, $"项目 {s.Project} 或任务类型 {s.TaskType} 不在清单里。");
                }
                else if (s.Enabled && !c.Vehicles.Any(v => v.Project == s.Project && v.TaskTypes.Contains(s.TaskType)))
                {
                    Add(at, $"项目 {s.Project} 开着任务类型 {s.TaskType}，但这个项目里没有车负责它；开着的任务类型必须配给一台车（ADR 0023）。");
                }
            }
        }

        // A station is one of charger, waiting point, fixed station and machine
        // station (ADR 0021); a charger serves one project (ADR 0020).
        public void StationUses()
        {
            var uses = new Dictionary<StationRef, (string Use, string Who)>();
            foreach (var f in c.FixedStations)
            {
                uses.TryAdd(f.Station, ("固定站点", $"项目 {f.Project} 任务类型 {f.TaskType}"));
            }

            var chargerProjects = new Dictionary<StationRef, string>();
            foreach (var v in c.Vehicles)
            {
                for (var i = 0; i < v.Chargers.Count; i++)
                {
                    var charger = v.Chargers[i];
                    var at = $"vehicles[{v.Id}].chargers[{i.ToString(CultureInfo.InvariantCulture)}]";
                    if (uses.TryGetValue(charger, out var use) && use.Use != "充电桩")
                    {
                        Add(at, $"{charger} 已经是{use.Who}的{use.Use}；一个站点只能当充电桩、等待点、固定站点、机台站点四者之一（ADR 0021）。");
                    }
                    else if (MachineStation(charger) is { } areas)
                    {
                        Add(at, $"{charger} 是区域号 {areas} 的机台站点；一个站点只能当充电桩、等待点、固定站点、机台站点四者之一（ADR 0021）。");
                    }
                    else
                    {
                        uses.TryAdd(charger, ("充电桩", $"车 {v.Id}"));
                        if (chargerProjects.TryGetValue(charger, out var project) && project != v.Project)
                        {
                            Add(at, $"充电桩 {charger} 已经在项目 {project} 的车的列表里；同一个桩不能出现在两个项目的车的列表里（ADR 0020）。");
                        }
                        else
                        {
                            chargerProjects.TryAdd(charger, v.Project);
                        }
                    }
                }
            }

            foreach (var v in c.Vehicles)
            {
                var at = $"vehicles[{v.Id}].waiting-point";
                if (uses.TryGetValue(v.WaitingPoint, out var use))
                {
                    Add(at, $"{v.WaitingPoint} 已经是{use.Who}的{use.Use}；一个站点只能当充电桩、等待点、固定站点、机台站点四者之一（ADR 0021）。");
                }
                else if (MachineStation(v.WaitingPoint) is { } areas)
                {
                    Add(at, $"{v.WaitingPoint} 是区域号 {areas} 的机台站点；一个站点只能当充电桩、等待点、固定站点、机台站点四者之一（ADR 0021）。");
                }
            }

            foreach (var f in c.FixedStations)
            {
                if (MachineStation(f.Station) is { } areas)
                {
                    Add(
                        $"fixed-stations[{f.Project}/{f.TaskType}/{f.Station.Map.ToString(CultureInfo.InvariantCulture)}]",
                        $"{f.Station} 是区域号 {areas} 的机台站点；一个站点只能当充电桩、等待点、固定站点、机台站点四者之一（ADR 0021）。");
                }
            }
        }

        public void Parameters()
        {
            var p = c.Parameters;
            foreach (var parameter in SiteParameters.All)
            {
                var value = parameter.Get(p);
                if (parameter.Kind == ParameterKind.Duration && (TimeSpan)value <= TimeSpan.Zero)
                {
                    Add($"parameters.{parameter.Name}", "时长要大于 0。");
                }
                else if (parameter.Kind == ParameterKind.Count && (int)value < 1)
                {
                    Add($"parameters.{parameter.Name}", "要至少是 1。");
                }
            }

            // urgent < charge-needed < ready <= full, all percentages (ADR 0020).
            var levels = new[] { ("urgent-charge-level", p.UrgentChargeLevel), ("charge-needed-level", p.ChargeNeededLevel), ("ready-level", p.ReadyLevel), ("full-level", p.FullLevel) };
            var outOfRange = levels.Where(l => l.Item2 is < 0 or > 100).ToList();
            foreach (var (name, _) in outOfRange)
            {
                Add($"parameters.{name}", "电量线是百分比，要在 0 到 100 之间。");
            }

            if (outOfRange.Count == 0
                && !(p.UrgentChargeLevel < p.ChargeNeededLevel && p.ChargeNeededLevel < p.ReadyLevel && p.ReadyLevel <= p.FullLevel))
            {
                Add(
                    "parameters.charge-levels",
                    $"四条电量线要满足 紧急充电线 < 去充电线 < 可接活线 ≤ 充满线（ADR 0020），现在是 {p.UrgentChargeLevel}、{p.ChargeNeededLevel}、{p.ReadyLevel}、{p.FullLevel}。");
            }
        }

        private void Slots(string at, Vehicle v)
        {
            var slots = new HashSet<int>();
            foreach (var g in v.SlotGroups)
            {
                foreach (var slot in g.Slots)
                {
                    if (!slots.Add(slot))
                    {
                        Add($"{at}.slot-groups", $"仓位 {slot} 在仓位分组里出现了不止一次。");
                    }
                }
            }

            var wired = v.IoPoints.Select(io => io.Slot).ToHashSet();
            var missing = slots.Where(s => !wired.Contains(s)).Order().ToList();
            if (missing.Count > 0)
            {
                Add($"{at}.io-points", $"仓位 {Join(missing)} 在 IO 点位对应表里没有（ADR 0024）。");
            }

            var points = new Dictionary<IoPoint, string>();
            var listed = new HashSet<int>();
            foreach (var io in v.IoPoints)
            {
                var slotAt = $"{at}.io-points[slot {io.Slot.ToString(CultureInfo.InvariantCulture)}]";
                if (!slots.Contains(io.Slot))
                {
                    Add(slotAt, $"车没有仓位 {io.Slot}。");
                }

                if (!listed.Add(io.Slot))
                {
                    Add(slotAt, $"仓位 {io.Slot} 在 IO 点位对应表里出现了不止一次。");
                }

                foreach (var (signal, name, point, kind) in new[]
                         {
                             ("lock", "锁反馈", io.Lock, IoPointKind.Input),
                             ("light-curtain", "光幕", io.LightCurtain, IoPointKind.Input),
                             ("unlock", "开锁输出", io.Unlock, IoPointKind.Output),
                         })
                {
                    if (point.Kind != kind)
                    {
                        Add($"{slotAt}.{signal}", $"{name}要接在{(kind == IoPointKind.Input ? "输入点（DI）" : "输出点（DO）")}上，现在是 {point}。");
                    }
                    else if (!points.TryAdd(point, $"仓位 {io.Slot} 的{name}"))
                    {
                        Add($"{slotAt}.{signal}", $"{point} 已经是{points[point]}；一个点不能接两个信号。");
                    }
                }
            }
        }

        private void OnMap(string at, StationRef station)
        {
            if (maps.Map(station.Map) is null)
            {
                Add(at, $"车队调度系统里没有地图 {station.Map}（{station}）。");
            }
            else if (maps.Station(station.Map, station.Station) is null)
            {
                Add(at, $"地图上没有 {station}（ADR 0015）。");
            }
        }

        /// <summary>The areas a station serves on the map, or null when it serves none.</summary>
        private string? MachineStation(StationRef station) =>
            maps.Station(station.Map, station.Station) is { Areas.Count: > 0 } s ? Join(s.Areas) : null;

        private void Once(IEnumerable<string> codes, string list, string what)
        {
            foreach (var code in codes.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                Add($"{list}[{code}]", $"{what} {code} 定义了不止一次，编号只能用一次。");
            }
        }

        private void Add(string where, string message) => Problems.Add(new SiteProblem(where, message));

        private static string Join<T>(IEnumerable<T> items) => string.Join("、", items);
    }
}
