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

    /// <summary>
    /// The server found no key for its terminal certificate and created one:
    /// the certificate pin is new, and terminals pinned to an earlier key refuse
    /// the server until they are given it.
    /// </summary>
    public const string OnboardServerKeyCreated = "onboard.server-key-created";

    /// <summary>The first report arrived on a terminal's new stream.</summary>
    public const string OnboardConnected = "onboard.connected";

    /// <summary>A terminal's stream ended; data says why.</summary>
    public const string OnboardDisconnected = "onboard.disconnected";

    /// <summary>A terminal's freshest report has become older than ten seconds (ADR 0009, 0024): its reports are stale.</summary>
    public const string OnboardStale = "onboard.stale";

    /// <summary>A fresh report arrived from a terminal whose reports were stale.</summary>
    public const string OnboardFresh = "onboard.fresh";

    /// <summary>A stream was refused, for example for a protocol major the server does not serve (ADR 0026).</summary>
    public const string OnboardRefused = "onboard.refused";

    /// <summary>A person at the vehicle scanned or pressed a button. Recorded only, not acted on yet.</summary>
    public const string OnboardInputReceived = "onboard.input-received";
}

/// <summary>The entries an event can come through (ADR 0019): the server itself and onboard terminals, later the console and MCP.</summary>
public static class EventSources
{
    public const string Server = "server";

    /// <summary>A person at a vehicle, through its onboard terminal.</summary>
    public const string OnboardTerminal = "onboard-terminal";
}
