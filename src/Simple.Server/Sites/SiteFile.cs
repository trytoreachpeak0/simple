using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Simple.Server.Sites;

/// <summary>What reading a site file gave: the configuration, or every problem found (then no configuration).</summary>
public sealed record SiteFileRead(SiteConfiguration? Configuration, IReadOnlyList<SiteProblem> Problems);

/// <summary>
/// Reads <c>sites/&lt;site&gt;/site.yaml</c>, a site's starting configuration
/// (ADR 0017). The format is documented in sites/README.md. The file has no
/// task type switches: a first import switches every one off, and only the
/// administrator turns them on (ADR 0017). A parameter the file leaves out
/// keeps its ADR default.
/// </summary>
public static partial class SiteFile
{
    public const string FileName = "site.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .Build();

    public static SiteFileRead Read(string path) => Parse(File.ReadAllText(path), path);

    /// <param name="name">How problems name the file, for example its path.</param>
    public static SiteFileRead Parse(string text, string name)
    {
        FileEntry? file;
        try
        {
            file = Deserializer.Deserialize<FileEntry?>(text);
        }
        catch (YamlException e)
        {
            return Failed(new SiteProblem(
                $"{name} line {e.Start.Line.ToString(CultureInfo.InvariantCulture)}, column {e.Start.Column.ToString(CultureInfo.InvariantCulture)}",
                Message(e)));
        }

        if (file is null)
        {
            return Failed(new SiteProblem(name, "文件是空的。"));
        }

        var reader = new Reader();
        var configuration = reader.Build(file);
        return reader.Problems.Count == 0 ? new SiteFileRead(configuration, []) : new SiteFileRead(null, reader.Problems);
    }

    /// <summary>Reads <c>500ms</c>, <c>30s</c>, <c>1.5m</c> or <c>2h</c>; null for anything else.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || DurationPattern().Match(text.Trim()) is not { Success: true } match)
        {
            return null;
        }

        var amount = double.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromHours(amount),
        };
    }

    private static SiteFileRead Failed(SiteProblem problem) => new(null, [problem]);

    private static string Message(YamlException e)
    {
        var message = e.InnerException?.Message ?? e.Message;
        return UnknownKey().Match(message) is { Success: true } unknown
            ? $"不认识的键 '{unknown.Groups["key"].Value}'。"
            : message;
    }

    [GeneratedRegex(@"^(?<amount>\d+(\.\d+)?)(?<unit>ms|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex("Property '(?<key>[^']*)' not found", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownKey();

    private sealed class Reader
    {
        public List<SiteProblem> Problems { get; } = [];

        public SiteConfiguration Build(FileEntry file)
        {
            var projects = new List<Project>();
            var zones = new List<DispatchZone>();
            var fixedStations = new List<FixedStation>();
            foreach (var (p, i) in Indexed(file.Projects))
            {
                var at = $"projects[{i}]";
                var code = Required(p.Code, $"{at}.code");
                projects.Add(new Project(code, p.Name));
                foreach (var (z, j) in Indexed(p.Zones))
                {
                    var zoneAt = $"{at}.zones[{j}]";
                    zones.Add(new DispatchZone(
                        Required(z.Code, $"{zoneAt}.code"),
                        Required(z.Name, $"{zoneAt}.name"),
                        code,
                        Policy(z.DemandOrder, $"{zoneAt}.demand-order"),
                        z.WaitCap is null ? DispatchZone.DefaultWaitCap : Duration(z.WaitCap, $"{zoneAt}.wait-cap")));
                }

                foreach (var (f, j) in Indexed(p.FixedStations))
                {
                    var fixedAt = $"{at}.fixed-stations[{j}]";
                    fixedStations.Add(new FixedStation(
                        code,
                        Required(f.TaskType, $"{fixedAt}.task-type"),
                        Station(f.Map, f.Station, fixedAt),
                        f.AllLoaded is null ? null : Duration(f.AllLoaded, $"{fixedAt}.all-loaded")));
                }
            }

            var taskTypes = Indexed(file.TaskTypes).Select(x =>
            {
                var (t, i) = x;
                var at = $"task-types[{i}]";
                return new TaskType(
                    Required(t.Code, $"{at}.code"),
                    Required(t.Name, $"{at}.name"),
                    End(t.Pickup, $"{at}.pickup"),
                    End(t.Unload, $"{at}.unload"));
            }).ToList();

            var areas = Indexed(file.Areas).Select(x =>
            {
                var (a, i) = x;
                return new AreaSetting(Required(a.Area, $"areas[{i}].area").Trim(), a.Zone, a.DoorSide, a.NotServed ?? false);
            }).ToList();

            var vehicles = Indexed(file.Vehicles).Select(x => Vehicle(x.Item, $"vehicles[{x.Index}]")).ToList();

            return new SiteConfiguration
            {
                Site = Required(file.Name, "name"),
                Description = file.Description,
                Projects = projects,
                Zones = zones,
                Areas = areas,
                TaskTypes = taskTypes,
                FixedStations = fixedStations,
                Vehicles = vehicles,
                Parameters = Parameters(file.Parameters),
            };
        }

        private Vehicle Vehicle(VehicleEntry v, string at) => new(
            Required(v.Id, $"{at}.id"),
            Required(v.RiotDeviceKey, $"{at}.riot-device-key"),
            Required(v.Project, $"{at}.project"),
            Required(v.Zone, $"{at}.zone"),
            v.TaskTypes ?? [],
            [.. Indexed(v.SlotGroups).Select(x => new SlotGroup(Required(x.Item.Name, $"{at}.slot-groups[{x.Index}].name"), x.Item.Slots ?? []))],
            [.. Indexed(v.Chargers).Select(x => Station(x.Item.Map, x.Item.Station, $"{at}.chargers[{x.Index}]"))],
            v.WaitingPoint is { } w ? Station(w.Map, w.Station, $"{at}.waiting-point") : Missing<StationRef>($"{at}.waiting-point", new StationRef(0, 0)),
            [.. Indexed(v.IoPoints).Select(x =>
            {
                var (io, i) = x;
                var ioAt = $"{at}.io-points[{i}]";
                return new SlotIo(
                    io.Slot ?? Missing($"{ioAt}.slot", 0),
                    Point(io.Lock, $"{ioAt}.lock"),
                    Point(io.LightCurtain, $"{ioAt}.light-curtain"),
                    Point(io.Unlock, $"{ioAt}.unlock"));
            })]);

        private SiteParameters Parameters(Dictionary<string, string>? given)
        {
            var parameters = new SiteParameters();
            foreach (var (key, value) in given ?? [])
            {
                var at = $"parameters.{key}";
                if (SiteParameters.All.FirstOrDefault(p => p.Name == key) is not { } parameter)
                {
                    Add(at, $"没有这个参数（有：{string.Join("、", SiteParameters.All.Select(p => p.Name))}）。");
                }
                else if (parameter.Kind == ParameterKind.Duration)
                {
                    parameters = parameter.With(parameters, Duration(value, at));
                }
                else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    parameters = parameter.With(parameters, number);
                }
                else
                {
                    Add(at, $"'{value}' 不是整数。");
                }
            }

            return parameters;
        }

        private StationRef Station(int? map, int? station, string at) =>
            new(map ?? Missing($"{at}.map", 0), station ?? Missing($"{at}.station", 0));

        private TimeSpan Duration(string text, string at)
        {
            if (ParseDuration(text) is { } duration)
            {
                return duration;
            }

            Add(at, $"'{text}' 不是时长；写成 500ms、30s、10m 或 2h。");
            return TimeSpan.Zero;
        }

        private DemandOrderPolicy Policy(string? text, string at) => text switch
        {
            null or "nearest-first" => DemandOrderPolicy.NearestFirst,
            "first-come-first-served" => DemandOrderPolicy.FirstComeFirstServed,
            "nearest-first-with-wait-cap" => DemandOrderPolicy.NearestFirstWithWaitCap,
            "shortest-overall" => DemandOrderPolicy.ShortestOverall,
            _ => Missing(at, DemandOrderPolicy.NearestFirst, $"'{text}' 不是需求先后策略；可选 first-come-first-served、nearest-first、nearest-first-with-wait-cap、shortest-overall（ADR 0036）。"),
        };

        private TaskEnd End(string? text, string at) => text switch
        {
            "machine" => TaskEnd.Machine,
            "fixed-station" => TaskEnd.FixedStation,
            _ => Missing(at, TaskEnd.Machine, $"要写 machine 或 fixed-station{(text is null ? string.Empty : $"，不是 '{text}'")}。"),
        };

        private IoPoint Point(string? text, string at) =>
            IoPoint.Parse(text) ?? Missing(at, default(IoPoint), $"要写 IO 模块的点，例如 DI1 或 DO1{(text is null ? string.Empty : $"，不是 '{text}'")}。");

        private string Required(string? value, string at) =>
            string.IsNullOrWhiteSpace(value) ? Missing(at, string.Empty) : value;

        private T Missing<T>(string at, T stand, string message = "要写明。")
        {
            Add(at, message);
            return stand;
        }

        private void Add(string at, string message) => Problems.Add(new SiteProblem(at, message));

        private static IEnumerable<(T Item, int Index)> Indexed<T>(IEnumerable<T>? items) =>
            (items ?? []).Select((item, i) => (item, i));
    }

    // The file's shape. YamlDotNet refuses keys it does not know, which
    // catches a misspelt key instead of quietly using a default.
#pragma warning disable CA1812, CA2227 // Created and filled by YamlDotNet.
    private sealed class FileEntry
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<ProjectEntry>? Projects { get; set; }

        public List<TaskTypeEntry>? TaskTypes { get; set; }

        public List<AreaEntry>? Areas { get; set; }

        public List<VehicleEntry>? Vehicles { get; set; }

        public Dictionary<string, string>? Parameters { get; set; }
    }

    private sealed class ProjectEntry
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public List<ZoneEntry>? Zones { get; set; }

        public List<FixedStationEntry>? FixedStations { get; set; }
    }

    private sealed class ZoneEntry
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public string? DemandOrder { get; set; }

        public string? WaitCap { get; set; }
    }

    private sealed class FixedStationEntry
    {
        public string? TaskType { get; set; }

        public int? Map { get; set; }

        public int? Station { get; set; }

        public string? AllLoaded { get; set; }
    }

    private sealed class TaskTypeEntry
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public string? Pickup { get; set; }

        public string? Unload { get; set; }
    }

    private sealed class AreaEntry
    {
        public string? Area { get; set; }

        public string? Zone { get; set; }

        public string? DoorSide { get; set; }

        public bool? NotServed { get; set; }
    }

    private sealed class VehicleEntry
    {
        public string? Id { get; set; }

        public string? RiotDeviceKey { get; set; }

        public string? Project { get; set; }

        public string? Zone { get; set; }

        public List<string>? TaskTypes { get; set; }

        public List<SlotGroupEntry>? SlotGroups { get; set; }

        public List<StationEntry>? Chargers { get; set; }

        public StationEntry? WaitingPoint { get; set; }

        public List<IoEntry>? IoPoints { get; set; }
    }

    private sealed class SlotGroupEntry
    {
        public string? Name { get; set; }

        public List<int>? Slots { get; set; }
    }

    private sealed class StationEntry
    {
        public int? Map { get; set; }

        public int? Station { get; set; }
    }

    private sealed class IoEntry
    {
        public int? Slot { get; set; }

        public string? Lock { get; set; }

        public string? LightCurtain { get; set; }

        public string? Unlock { get; set; }
    }
#pragma warning restore CA1812, CA2227
}
