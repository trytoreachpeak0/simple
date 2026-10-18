using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NJsonSchema;
using NJsonSchema.Validation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Simple.Cli.Scenarios;

/// <summary>Something wrong in a scenario file, and where.</summary>
public sealed record ScenarioProblem(int Line, int Column, string Message)
{
    public override string ToString() => $"line {Line.ToString(CultureInfo.InvariantCulture)}, column {Column.ToString(CultureInfo.InvariantCulture)}: {Message}";
}

/// <summary>
/// Reads a scenario file: YAML, checked against the format
/// (scenarios/scenario.schema.json), then against the rules the format
/// cannot express and against its site. Every problem names its line.
/// </summary>
public static partial class ScenarioFile
{
    public const string Directory = "scenarios";
    public const string Extension = ".yaml";
    public static readonly TimeSpan DefaultWithin = TimeSpan.FromSeconds(30);

    /// <summary>The action keys of an event, as the format names them.</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "add-mes-row", "remove-mes-row", "fail-mes-reads", "malform-mes-reads",
        "set-riot-vehicle", "fail-riot-creates",
        "scan", "press", "confirm-terminal",
        "fault-io-module", "auto-respond",
        "stop-process",
    ];

    private static readonly string[] Comparisons = ["equals", "not-equals", "at-least", "at-most"];

    private static readonly string[] Kinds = ["eventually", "always", "never"];

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var stream = typeof(ScenarioFile).Assembly.GetManifestResourceStream("Simple.Cli.Scenarios.scenario.schema.json")
            ?? throw new InvalidOperationException("The scenario schema is not embedded in the command line.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromJsonAsync(reader.ReadToEnd()).GetAwaiter().GetResult();
    });

    /// <summary>Every scenario file under the clone's scenarios/, by name.</summary>
    public static IReadOnlyList<(string Name, string Path)> All(string repository)
    {
        var root = System.IO.Path.Combine(repository, Directory);
        return !System.IO.Directory.Exists(root)
            ? []
            : System.IO.Directory.EnumerateFiles(root, "*" + Extension, SearchOption.AllDirectories)
                .Select(path => (NameOf(root, path), path))
                .OrderBy(s => s.Item1, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// A scenario named on the command line: a path to a .yaml file, or a
    /// name under scenarios/ (infrastructure-up). Null when there is neither.
    /// </summary>
    public static (string Name, string Path)? Find(string repository, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(reference))
        {
            var full = System.IO.Path.GetFullPath(reference);
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, Directory));
            return (full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? NameOf(root, full)
                : System.IO.Path.GetFileNameWithoutExtension(full), full);
        }

        var path = System.IO.Path.Combine(repository, Directory, reference.Replace('/', System.IO.Path.DirectorySeparatorChar) + Extension);
        return File.Exists(path) ? (reference, path) : null;
    }

    private static string NameOf(string root, string path) =>
        System.IO.Path.ChangeExtension(System.IO.Path.GetRelativePath(root, path), null)!.Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>Reads and checks a scenario; the scenario is null when there is any problem.</summary>
    public static (Scenario? Scenario, IReadOnlyList<ScenarioProblem> Problems) Load(string repository, string name, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var problems = new List<ScenarioProblem>();
        var marks = new Dictionary<string, Mark>(StringComparer.Ordinal);
        JsonNode? root;
        try
        {
            var yaml = new YamlStream();
            using (var reader = File.OpenText(path))
            {
                yaml.Load(reader);
            }

            if (yaml.Documents.Count != 1)
            {
                return (null, [new(1, 1, $"a scenario file holds exactly one YAML document, not {yaml.Documents.Count}")]);
            }

            root = ToJson(yaml.Documents[0].RootNode, string.Empty, marks);
        }
        catch (YamlException e)
        {
            return (null, [new((int)e.Start.Line, (int)e.Start.Column, (e.InnerException ?? e).Message)]);
        }

        foreach (var error in Flatten(Schema.Value.Validate(root?.ToJsonString() ?? "null")))
        {
            var at = MarkOf(marks, error.Path);
            problems.Add(new(at.Line, at.Column, Describe(error)));
        }

        if (problems.Count > 0)
        {
            return (null, Sorted(problems));
        }

        var scenario = new Reader(repository, name, path, marks, problems).Read((JsonObject)root!);
        return problems.Count > 0 ? (null, Sorted(problems)) : (scenario, []);
    }

    private static List<ScenarioProblem> Sorted(List<ScenarioProblem> problems) =>
        problems.Distinct().OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();

    private readonly record struct Mark(int Line, int Column);

    /// <summary>
    /// The YAML as JSON, recording where each value starts under the path the
    /// schema validator reports (a.b[0].c). Plain scalars are typed as YAML
    /// 1.2's core schema types them; quoted ones are strings.
    /// </summary>
    private static JsonNode? ToJson(YamlNode node, string path, Dictionary<string, Mark> marks)
    {
        marks[path] = new Mark((int)node.Start.Line, (int)node.Start.Column);
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    // A key written twice is refused by the YAML parser already.
                    var name = key is YamlScalarNode { Value: { } text } ? text : key.ToString();
                    obj[name] = ToJson(value, path.Length == 0 ? name : $"{path}.{name}", marks);
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    array.Add(ToJson(item, $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]", marks));
                    index++;
                }

                return array;
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                throw new YamlException(node.Start, node.End, "an unsupported YAML node");
        }
    }

    private static JsonValue? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style is not ScalarStyle.Plain)
        {
            return JsonValue.Create(text);
        }

        return text switch
        {
            "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => JsonValue.Create(true),
            "false" or "False" or "FALSE" => JsonValue.Create(false),
            // Numbers are kept as JSON text, so they read as int, long or double alike.
            _ when Integer().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) =>
                Number(whole.ToString(CultureInfo.InvariantCulture)),
            _ when Float().IsMatch(text) => Number(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(text),
        };
    }

    private static JsonValue Number(string json) => (JsonValue)JsonNode.Parse(json)!;

    /// <summary>Where a reported path is; a path that is not there (a missing key) is reported at its nearest parent.</summary>
    private static Mark MarkOf(Dictionary<string, Mark> marks, string? reported)
    {
        var path = reported switch
        {
            null or "#" => string.Empty,
            _ when reported.StartsWith("#/", StringComparison.Ordinal) => reported[2..],
            _ => reported,
        };
        while (true)
        {
            if (marks.TryGetValue(path, out var mark))
            {
                return mark;
            }

            if (path.Length == 0)
            {
                return new Mark(1, 1);
            }

            var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
            path = cut < 0 ? string.Empty : path[..cut];
        }
    }

    private static IEnumerable<ValidationError> Flatten(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors)
        {
            if (error is ChildSchemaValidationError child && child.Errors.Count > 0)
            {
                foreach (var inner in Flatten(child.Errors.SelectMany(e => e.Value)))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return error;
            }
        }
    }

    private static string Describe(ValidationError error)
    {
        var where = error.Property is { Length: > 0 } property ? $"'{property}'" : "this value";
        return error.Kind switch
        {
            ValidationErrorKind.NoAdditionalPropertiesAllowed => $"'{error.Property}' is not a key the format knows here",
            ValidationErrorKind.PropertyRequired => $"'{error.Property}' is missing",
            ValidationErrorKind.StringExpected => $"{where} must be a string",
            ValidationErrorKind.IntegerExpected => $"{where} must be a whole number",
            ValidationErrorKind.NumberExpected => $"{where} must be a number",
            ValidationErrorKind.BooleanExpected => $"{where} must be true or false",
            ValidationErrorKind.ObjectExpected => $"{where} must be a mapping (key: value lines)",
            ValidationErrorKind.ArrayExpected => $"{where} must be a list",
            ValidationErrorKind.NotInEnumeration => $"{where} must be one of {string.Join(", ", error.Schema.Enumeration.Select(e => e?.ToString()))}",
            ValidationErrorKind.PatternMismatch when error.Schema.Pattern is { } pattern && pattern.Contains("ms|s|m", StringComparison.Ordinal) =>
                $"{where} must be a duration such as 500ms, 5s or 2m",
            ValidationErrorKind.PatternMismatch => $"{where} must be lowercase letters, digits and '-'",
            ValidationErrorKind.StringTooShort => $"{where} must not be empty",
            ValidationErrorKind.TooFewItems => $"{where} needs at least {error.Schema.MinItems.ToString(CultureInfo.InvariantCulture)} item",
            ValidationErrorKind.NumberTooSmall => $"{where} is too small",
            ValidationErrorKind.NumberTooBig or ValidationErrorKind.IntegerTooBig => $"{where} is too big",
            var kind => $"{where}: {kind}",
        };
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    [GeneratedRegex(@"^(?<amount>[0-9]+(\.[0-9]+)?)(?<unit>ms|s|m)$")]
    private static partial Regex DurationText();

    public static TimeSpan ParseDuration(string text)
    {
        var match = DurationText().Match(text);
        if (!match.Success)
        {
            throw new FormatException($"'{text}' is not a duration such as 500ms, 5s or 2m.");
        }

        var amount = double.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            _ => TimeSpan.FromMinutes(amount),
        };
    }

    /// <summary>Builds the scenario from JSON the schema accepted, adding a problem for every rule the schema cannot express.</summary>
    private sealed class Reader(string repository, string name, string path, Dictionary<string, Mark> marks, List<ScenarioProblem> problems)
    {
        private Site? site;

        public Scenario Read(JsonObject root)
        {
            var siteName = (string)root["site"]!;
            var sitePath = Site.PathOf(repository, siteName);
            if (!File.Exists(sitePath))
            {
                Problem("site", $"site '{siteName}' has no {System.IO.Path.Combine("sites", siteName, Site.FileName)} in this clone");
            }
            else
            {
                try
                {
                    site = Site.Read(sitePath);
                }
                catch (InvalidDataException e)
                {
                    Problem("site", $"site '{siteName}' cannot be read: {e.Message}");
                }
            }

            var initial = Initial(root["initial"] as JsonObject);
            var events = Events(root["events"] as JsonArray, initial.Terminals);
            var expectations = Expectations((JsonArray)root["expectations"]!, events);
            var expectedVerdict = root["expected-verdict"] is { } expected ? Verdicts.Parse((string)expected!) : Verdict.Holds;
            if (expectedVerdict != Verdict.Holds && expectations.Count != 1)
            {
                // With more, the other expectations could hide which one gave the verdict.
                Problem("expected-verdict", $"a scenario that expects {expectedVerdict.Name()} proves one thing: it has exactly one expectation, not {expectations.Count}");
            }

            return new Scenario(
                name,
                path,
                (string)root["title"]!,
                (string?)root["description"],
                siteName,
                expectedVerdict,
                initial,
                events,
                expectations);
        }

        private InitialState Initial(JsonObject? initial)
        {
            const string at = "initial";
            var terminals = (int?)initial?["terminals"] ?? 0;
            if (site is not null && terminals > site.Vehicles.Count)
            {
                Problem($"{at}.terminals", $"site '{site.Name}' has {site.Vehicles.Count} vehicles, not {terminals}");
            }

            var vehicles = new List<RiotVehicleChange>();
            if (initial?["fake-riot"]?["vehicles"] is JsonArray changes)
            {
                for (var i = 0; i < changes.Count; i++)
                {
                    vehicles.Add(Vehicle((JsonObject)changes[i]!, $"{at}.fake-riot.vehicles[{i}]"));
                }
            }

            List<JsonObject>? rows = null;
            if (initial?["fake-mes"]?["rows"] is JsonArray given)
            {
                rows = [];
                for (var i = 0; i < given.Count; i++)
                {
                    rows.Add(MesRow((JsonObject)given[i]!, $"{at}.fake-mes.rows[{i}]"));
                }
            }

            var cargo = new Dictionary<int, IReadOnlyList<int>>();
            if (initial?["slots"] is JsonArray slots)
            {
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = (JsonObject)slots[i]!;
                    var terminal = Terminal(slot, $"{at}.slots[{i}]", terminals);
                    if (!cargo.TryAdd(terminal, slot["cargo"]!.AsArray().Select(n => (int)n!).Distinct().Order().ToList()))
                    {
                        Problem($"{at}.slots[{i}].terminal", $"terminal {terminal} is listed twice under slots");
                    }
                }
            }

            return new InitialState(terminals, (double?)initial?["fake-riot"]?["speedup"], vehicles, rows, cargo, (bool?)initial?["confirm-terminals"] ?? true);
        }

        private List<ScenarioEvent> Events(JsonArray? events, int terminals)
        {
            var result = new List<ScenarioEvent>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var processes = Components.All
                .SelectMany(c => Components.ProcessNames(c, c.Count(new UpOptions(terminals))))
                .ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < (events?.Count ?? 0); i++)
            {
                var at = $"events[{i}]";
                var e = (JsonObject)events![i]!;
                var id = (string?)e["id"];
                if (id is not null && !ids.Add(id))
                {
                    Problem($"{at}.id", $"another event is already called '{id}'");
                }

                var actions = Actions.Where(e.ContainsKey).ToList();
                if (actions.Count != 1)
                {
                    Problem(at, actions.Count == 0
                        ? $"an event does exactly one action: one of {string.Join(", ", Actions)}"
                        : $"an event does exactly one action, not {string.Join(" and ", actions)}");
                    continue;
                }

                if (e.ContainsKey("at") == e.ContainsKey("when"))
                {
                    Problem(at, "an event has exactly one of at (a time) and when (a condition)");
                    continue;
                }

                if (e.ContainsKey("within") && !e.ContainsKey("when"))
                {
                    Problem($"{at}.within", "within goes with when");
                }

                var kind = actions[0];
                var arguments = (JsonObject)e[kind]!;
                CheckAction(kind, arguments, $"{at}.{kind}", terminals, processes);
                result.Add(new ScenarioEvent(
                    id,
                    id ?? $"event {(i + 1).ToString(CultureInfo.InvariantCulture)}",
                    e["at"] is { } time ? ParseDuration((string)time!) : null,
                    e["when"] is JsonObject wait ? Condition(wait, $"{at}.when") : null,
                    e["within"] is { } within ? ParseDuration((string)within!) : DefaultWithin,
                    new ScenarioAction(kind, arguments),
                    Line(at)));
            }

            return result;
        }

        private void CheckAction(string kind, JsonObject arguments, string at, int terminals, HashSet<string> processes)
        {
            switch (kind)
            {
                case "add-mes-row":
                    MesRow(arguments, at);
                    break;
                case "remove-mes-row":
                    MesRow(arguments, at);
                    break;
                case "set-riot-vehicle":
                    Vehicle(arguments, at);
                    break;
                case "scan" or "press" or "confirm-terminal" or "fault-io-module" or "auto-respond":
                    Terminal(arguments, at, terminals);
                    break;
                case "stop-process" when !processes.Contains((string)arguments["process"]!):
                    Problem($"{at}.process", $"the system has no process '{(string)arguments["process"]!}'; with {terminals} terminals it has {string.Join(", ", processes.Order(StringComparer.Ordinal))}");
                    break;
            }
        }

        private List<Expectation> Expectations(JsonArray expectations, List<ScenarioEvent> events)
        {
            var result = new List<Expectation>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < expectations.Count; i++)
            {
                var at = $"expectations[{i}]";
                var e = (JsonObject)expectations[i]!;
                var id = (string?)e["id"];
                if (id is not null && !ids.Add(id))
                {
                    Problem($"{at}.id", $"another expectation is already called '{id}'");
                }

                var kinds = Kinds.Where(e.ContainsKey).ToList();
                if (kinds.Count != 1)
                {
                    Problem(at, "an expectation has exactly one of eventually, always and never");
                    continue;
                }

                var kind = kinds[0] switch
                {
                    "eventually" => ExpectationKind.Eventually,
                    "always" => ExpectationKind.Always,
                    _ => ExpectationKind.Never,
                };
                var windowKey = kind == ExpectationKind.Eventually ? "within" : "for";
                var otherKey = kind == ExpectationKind.Eventually ? "for" : "within";
                if (e.ContainsKey(otherKey))
                {
                    Problem($"{at}.{otherKey}", $"{kinds[0]} takes {windowKey}, not {otherKey}");
                }

                var window = e[windowKey];
                if (window is null)
                {
                    Problem(at, $"{kinds[0]} needs {windowKey}: how long the window stays open");
                }

                if (kind == ExpectationKind.Always && e[kinds[0]]?["event"] is not null)
                {
                    Problem($"{at}.always", "always does not take an event condition: an entry, once recorded, stays recorded; write eventually (it is recorded) or never (it is not)");
                }

                if (e["only-if"]?["event"] is not null)
                {
                    Problem($"{at}.only-if", "only-if takes a status condition: it is judged once, as the window opens, when no entry of the window can be there yet");
                }

                var after = (string?)e["after"];
                if (after is not null && events.All(ev => ev.Id != after))
                {
                    Problem($"{at}.after", $"no event is called '{after}'");
                }

                result.Add(new Expectation(
                    id,
                    id ?? $"expectation {(i + 1).ToString(CultureInfo.InvariantCulture)}",
                    (string?)e["title"],
                    kind,
                    Condition((JsonObject)e[kinds[0]]!, $"{at}.{kinds[0]}"),
                    window is null ? TimeSpan.Zero : ParseDuration((string)window!),
                    after,
                    e["only-if"] is JsonObject onlyIf ? Condition(onlyIf, $"{at}.only-if") : null,
                    Line(at)));
            }

            return result;
        }

        private Condition Condition(JsonObject condition, string at)
        {
            var comparisons = Comparisons.Where(condition.ContainsKey).ToList();
            if (condition["status"] is { } status)
            {
                if (condition.ContainsKey("event") || condition.ContainsKey("source") || condition.ContainsKey("data"))
                {
                    Problem(at, "a condition reads either status or event, not both");
                }

                if (comparisons.Count != 1)
                {
                    Problem(at, $"a status condition has exactly one of {string.Join(", ", Comparisons)}");
                    return new EventCondition("invalid", null, null);
                }

                var text = (string)status!;
                var segments = StatusCondition.ParsePath(text, out var error);
                if (segments is null)
                {
                    Problem($"{at}.status", error!);
                    segments = [];
                }

                var comparison = comparisons[0] switch
                {
                    "equals" => Comparison.EqualTo,
                    "not-equals" => Comparison.NotEqualTo,
                    "at-least" => Comparison.AtLeast,
                    _ => Comparison.AtMost,
                };
                return new StatusCondition(text, segments, comparison, condition[comparisons[0]]?.DeepClone());
            }

            if (condition["event"] is { } type)
            {
                if (comparisons.Count > 0)
                {
                    Problem(at, $"{comparisons[0]} goes with status, not with event");
                }

                return new EventCondition((string)type!, (string?)condition["source"], condition["data"]?.DeepClone() as JsonObject);
            }

            Problem(at, "a condition reads status (with a comparison) or event");
            return new EventCondition("invalid", null, null);
        }

        private RiotVehicleChange Vehicle(JsonObject vehicle, string at)
        {
            var key = (string)vehicle["device-key"]!;
            if (site is not null && site.Vehicles.All(v => v.RiotDeviceKey != key))
            {
                Problem($"{at}.device-key", $"site '{site.Name}' has no vehicle with RIoT device key '{key}'");
            }

            return new RiotVehicleChange(
                key,
                (int?)vehicle["battery"],
                (bool?)vehicle["brake-released"],
                (string?)vehicle["mode"],
                (bool?)vehicle["manual-charger"],
                (bool?)vehicle["emergency-button-pressed"]);
        }

        private JsonObject MesRow(JsonObject row, string at)
        {
            if (site is not null)
            {
                if ((string?)row["task-type"] is { } taskType && !site.TaskTypes.Contains(taskType))
                {
                    Problem($"{at}.task-type", $"site '{site.Name}' has no task type '{taskType}' ({string.Join(", ", site.TaskTypes)})");
                }

                if ((string?)row["area"] is { } area && !site.Areas.Contains(area))
                {
                    Problem($"{at}.area", $"site '{site.Name}' has no area '{area}' ({string.Join(", ", site.Areas)})");
                }
            }

            return row;
        }

        private int Terminal(JsonObject arguments, string at, int terminals)
        {
            var terminal = (int)arguments["terminal"]!;
            if (terminal > terminals)
            {
                Problem($"{at}.terminal", $"the scenario starts {terminals} terminals (initial.terminals), so there is no terminal {terminal}");
            }

            return terminal;
        }

        private int Line(string at) => MarkOf(marks, at).Line;

        private void Problem(string at, string message)
        {
            var mark = MarkOf(marks, at);
            problems.Add(new ScenarioProblem(mark.Line, mark.Column, message));
        }
    }
}
