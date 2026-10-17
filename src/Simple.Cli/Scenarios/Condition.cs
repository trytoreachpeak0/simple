using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Simple.Cli.Scenarios;

/// <summary>Whether a condition held at one look: yes, no, or it could not be seen.</summary>
public enum Truth
{
    True,
    False,
    Unknown,
}

/// <param name="Detail">What was seen, or why nothing could be: shown in the timeline and the result.</param>
public sealed record Observation(Truth Truth, string Detail);

/// <summary>One entry of the server's event record, as GET /api/v1/events gives it.</summary>
public sealed record RecordedEvent(long Id, DateTimeOffset OccurredAt, string Type, string Source, JsonElement? Data, JsonElement Raw);

/// <summary>
/// One look at the system: the server's status and its event record so far,
/// each with why it could not be read. Every condition is judged on looks.
/// </summary>
/// <param name="At">When the look was finished, on this machine's clock (the server runs on it too).</param>
/// <param name="Events">Every entry recorded since the run began, oldest first; null when they could not be read.</param>
public sealed record Snapshot(DateTimeOffset At, JsonElement? Status, string? StatusError, IReadOnlyList<RecordedEvent>? Events, string? EventsError);

/// <summary>Something the scenario looks for: a value of the server's status, or an entry of its event record.</summary>
public abstract record Condition
{
    /// <summary>
    /// Judges the condition on one look. Event conditions count entries that
    /// occurred from <paramref name="from"/> up to <paramref name="until"/>;
    /// status conditions read the status of the look.
    /// </summary>
    public abstract Observation Observe(Snapshot snapshot, DateTimeOffset from, DateTimeOffset until);

    /// <summary>
    /// Whether it accumulates: once an entry is recorded it stays, so the
    /// last look after the window decides for the whole window.
    /// </summary>
    public abstract bool IsEvent { get; }

    public abstract string Describe();
}

public enum Comparison
{
    EqualTo,
    NotEqualTo,
    AtLeast,
    AtMost,
}

/// <summary>A value of GET /api/v1/status, compared with one value.</summary>
public sealed partial record StatusCondition(string Path, IReadOnlyList<PathSegment> Segments, Comparison Comparison, JsonNode? Operand) : Condition
{
    public override bool IsEvent => false;

    public override string Describe() => $"status {Path} {Keyword(Comparison)} {Operand?.ToJsonString() ?? "null"}";

    public static string Keyword(Comparison comparison) => comparison switch
    {
        Comparison.EqualTo => "equals",
        Comparison.NotEqualTo => "not-equals",
        Comparison.AtLeast => "at-least",
        _ => "at-most",
    };

    public override Observation Observe(Snapshot snapshot, DateTimeOffset from, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status is not { } status)
        {
            return new(Truth.Unknown, $"the server's status could not be read: {snapshot.StatusError}");
        }

        if (Resolve(status, Segments) is not { } value)
        {
            return new(Truth.Unknown, $"the status has no {Path}");
        }

        var seen = value.GetRawText();
        switch (Comparison)
        {
            case Comparison.EqualTo:
                return new(JsonEquals(value, Operand) ? Truth.True : Truth.False, $"{Path} is {seen}");
            case Comparison.NotEqualTo:
                return new(JsonEquals(value, Operand) ? Truth.False : Truth.True, $"{Path} is {seen}");
            default:
                if (value.ValueKind != JsonValueKind.Number || Operand?.GetValueKind() != JsonValueKind.Number)
                {
                    return new(Truth.Unknown, $"{Path} is {seen}, not a number");
                }

                var actual = value.GetDouble();
                var bound = Number(Operand);
                var holds = Comparison == Comparison.AtLeast ? actual >= bound : actual <= bound;
                return new(holds ? Truth.True : Truth.False, $"{Path} is {seen}");
        }
    }

    /// <summary>
    /// Reads a path written as segments joined by dots: a field (riot), a
    /// field with an index (onboardTerminals[0]) or with a filter that picks
    /// the first item whose field has that value (onboardTerminals[vehicleId=vehicle-1]),
    /// and count, the length of an array. Null when the path is malformed,
    /// with <paramref name="error"/> saying why.
    /// </summary>
    public static IReadOnlyList<PathSegment>? ParsePath(string path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<PathSegment>();
        foreach (var part in path.Split('.'))
        {
            var match = Segment().Match(part);
            if (!match.Success)
            {
                error = $"'{part}' in status path '{path}' is neither a field, field[index] nor field[field=value]";
                return null;
            }

            int? index = null;
            string? filterField = null;
            string? filterValue = null;
            if (match.Groups["index"].Success)
            {
                index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            }
            else if (match.Groups["field"].Success)
            {
                filterField = match.Groups["field"].Value;
                filterValue = match.Groups["value"].Value;
            }

            segments.Add(new PathSegment(match.Groups["name"].Value, index, filterField, filterValue));
        }

        error = null;
        return segments;
    }

    private static JsonElement? Resolve(JsonElement root, IReadOnlyList<PathSegment> segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (segment.Name == "count" && segment.Index is null && segment.FilterField is null && current.ValueKind == JsonValueKind.Array)
            {
                current = JsonSerializer.SerializeToElement(current.GetArrayLength());
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment.Name, out current))
            {
                return null;
            }

            if (segment.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else if (segment.FilterField is { } field)
            {
                if (current.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                JsonElement? found = null;
                foreach (var item in current.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) && Text(value) == segment.FilterValue)
                    {
                        found = item;
                        break;
                    }
                }

                if (found is not { } item1)
                {
                    return null;
                }

                current = item1;
            }
        }

        return current;
    }

    /// <summary>A number however it was made: from a file it is JSON text, from code an int or a double.</summary>
    private static double Number(JsonNode number) => JsonSerializer.SerializeToElement(number).GetDouble();

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>Equal as JSON values: numbers by value, everything else as written.</summary>
    public static bool JsonEquals(JsonElement value, JsonNode? operand)
    {
        if (operand is null)
        {
            return value.ValueKind == JsonValueKind.Null;
        }

        if (value.ValueKind == JsonValueKind.Number && operand.GetValueKind() == JsonValueKind.Number)
        {
            return value.GetDouble() == Number(operand);
        }

        return JsonNode.DeepEquals(JsonNode.Parse(value.GetRawText()), operand);
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(\[((?<index>[0-9]+)|(?<field>[A-Za-z_][A-Za-z0-9_]*)=(?<value>[^\]]*))\])?$")]
    private static partial Regex Segment();
}

/// <param name="Index">[n]: the item at n, from 0.</param>
/// <param name="FilterField">[field=value]: the first item whose field reads value.</param>
public sealed record PathSegment(string Name, int? Index, string? FilterField, string? FilterValue);

/// <summary>An entry of the event record of this type (and source, and data fields), recorded within the window.</summary>
public sealed record EventCondition(string Type, string? Source, JsonObject? Data) : Condition
{
    public override bool IsEvent => true;

    public override string Describe() =>
        $"event {Type}" + (Source is null ? string.Empty : $" from {Source}") + (Data is null ? string.Empty : $" with {Data.ToJsonString()}");

    public override Observation Observe(Snapshot snapshot, DateTimeOffset from, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Events is not { } events)
        {
            return new(Truth.Unknown, $"the event record could not be read: {snapshot.EventsError}");
        }

        foreach (var e in events)
        {
            if (e.Type == Type && e.OccurredAt >= from && e.OccurredAt <= until
                && (Source is null || e.Source == Source)
                && Matches(e.Data))
            {
                return new(Truth.True, $"#{e.Id} {e.Type} at {e.OccurredAt.ToLocalTime():HH:mm:ss.fff}" + (e.Data is { } data ? $" {data.GetRawText()}" : string.Empty));
            }
        }

        return new(Truth.False, $"no {Describe()} from {from.ToLocalTime():HH:mm:ss.fff} to {until.ToLocalTime():HH:mm:ss.fff}");
    }

    private bool Matches(JsonElement? data)
    {
        if (Data is null)
        {
            return true;
        }

        if (data is not { ValueKind: JsonValueKind.Object } fields)
        {
            return false;
        }

        foreach (var (name, expected) in Data)
        {
            if (!fields.TryGetProperty(name, out var actual) || !StatusCondition.JsonEquals(actual, expected))
            {
                return false;
            }
        }

        return true;
    }
}
