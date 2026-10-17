namespace Simple.Server.Events;

/// <summary>
/// One entry of the event record (ADR 0019): a state change, a decision of
/// the server or an action of a person. Append-only.
/// </summary>
public sealed class EventRecord
{
    /// <summary>Assigned by the database in insertion order; also the paging cursor.</summary>
    public long Id { get; private set; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>What happened, for example <see cref="EventTypes.ServerStarted"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Through which entry it happened, for example <see cref="EventSources.Server"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Details as a JSON object, or null.</summary>
    public string? Data { get; init; }
}

public static class EventTypes
{
    public const string ServerStarted = "server.started";
    public const string ServerStopped = "server.stopped";
}

/// <summary>The entries an event can come through (ADR 0019): the server itself, later the console, MCP and onboard terminals.</summary>
public static class EventSources
{
    public const string Server = "server";
}
