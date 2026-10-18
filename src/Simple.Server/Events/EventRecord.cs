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

    /// <summary>For a person's action: the account that did it (ADR 0019); null otherwise.</summary>
    public string? Account { get; init; }

    /// <summary>For a person's action through the console or MCP: the computer it came from (ADR 0045); null otherwise.</summary>
    public string? RemoteAddress { get; init; }
}

public static class EventTypes
{
    public const string ServerStarted = "server.started";
    public const string ServerStopped = "server.stopped";

    /// <summary>The MES became unavailable, or its cause changed: data.cause is "gateway" or "read".</summary>
    public const string MesUnreachable = "mes.unreachable";

    /// <summary>The MES is available again after <see cref="MesUnreachable"/>.</summary>
    public const string MesRecovered = "mes.recovered";

    /// <summary>
    /// The server found no key for its terminal certificate and created one:
    /// the certificate pin is new, and terminals pinned to an earlier key refuse
    /// the server until they are given it.
    /// </summary>
    public const string OnboardServerKeyCreated = "onboard.server-key-created";

    /// <summary>
    /// A vehicle's terminal is connected: the first report arrived on a stream
    /// whose key is registered for the vehicle it claims, or the key of an
    /// open stream was just confirmed. data.keyPin is the key.
    /// </summary>
    public const string OnboardConnected = "onboard.connected";

    /// <summary>
    /// A vehicle's terminal is gone: its stream ended, or the server stopped
    /// using it because its key was revoked, another key was confirmed for
    /// the vehicle or it looks cloned. data.reason says which.
    /// </summary>
    public const string OnboardDisconnected = "onboard.disconnected";

    /// <summary>
    /// A terminal key started waiting for confirmation (ADR 0025, 0029):
    /// data.vehicleId (the vehicle it claims), data.keyPin, data.inVehicleList,
    /// data.vehicleOnlineWithAnotherKey, data.registeredForVehicle,
    /// data.cloneSuspected. Its reports are not used. The pairing code is not
    /// recorded (a credential, ADR 0034); the status shows it while it waits.
    /// </summary>
    public const string OnboardPending = "onboard.pending";

    /// <summary>
    /// An engineer confirmed a terminal key for the vehicle it claims:
    /// data.vehicleId, data.keyPin, data.replacedKeyPin (the vehicle's earlier
    /// key, released), data.movedFromVehicle.
    /// </summary>
    public const string OnboardConfirmed = "onboard.confirmed";

    /// <summary>An engineer revoked a vehicle's terminal key: data.vehicleId, data.keyPin.</summary>
    public const string OnboardRevoked = "onboard.revoked";

    /// <summary>
    /// Two computers sent fresh reports with one registered key, alternately
    /// (a cloned disk, ADR 0025): data.vehicleId, data.keyPin, data.runs. The
    /// key's registration ended; neither computer's reports are used until an
    /// engineer confirms the key again.
    /// </summary>
    public const string OnboardCloneSuspected = "onboard.clone-suspected";

    /// <summary>
    /// A vehicle's terminal's freshest report has become older than ten
    /// seconds (ADR 0009, 0024), or the vehicle stopped using that terminal's
    /// key (revoked, replaced, suspected clone; data.reason says which): its
    /// reports are stale.
    /// </summary>
    public const string OnboardStale = "onboard.stale";

    /// <summary>A fresh report arrived from a terminal whose reports were stale.</summary>
    public const string OnboardFresh = "onboard.fresh";

    /// <summary>A stream was refused, for example for a protocol major the server does not serve (ADR 0026).</summary>
    public const string OnboardRefused = "onboard.refused";

    /// <summary>
    /// A vehicle's terminal reports that it cannot read its IO module: in its
    /// first report, or after it could. Recorded only, not acted on yet.
    /// </summary>
    public const string OnboardIoUnreadable = "onboard.io-unreadable";

    /// <summary>A terminal that reported it could not read its IO module reports that it can again.</summary>
    public const string OnboardIoReadable = "onboard.io-readable";

    /// <summary>A person at the vehicle scanned or pressed a button. Recorded only, not acted on yet.</summary>
    public const string OnboardInputReceived = "onboard.input-received";

    /// <summary>The administrator created an account: data.name, data.role, data.project.</summary>
    public const string AccountCreated = "account.created";

    /// <summary>The administrator deleted an account: data.name. Its logins ended with it.</summary>
    public const string AccountDeleted = "account.deleted";

    /// <summary>
    /// An account's password was set: by the administrator, by the account
    /// itself with its old password, or (the administrator's) by the server
    /// command. data.name, data.by. Every login of the account ended.
    /// </summary>
    public const string AccountPasswordSet = "account.password-set";

    /// <summary>The administrator switched the on-duty pause (ADR 0033): data.name, data.paused.</summary>
    public const string AccountPauseSet = "account.pause-set";

    /// <summary>
    /// The server made the console's certificate (ADR 0046): data.sha256,
    /// data.names, data.notAfter. Every console PC has to install it.
    /// </summary>
    public const string ConsoleCertificateCreated = "console.certificate-created";

    /// <summary>A person discarded the console's certificate with the server command; the next start makes a new one.</summary>
    public const string ConsoleCertificateDiscarded = "console.certificate-discarded";
}

/// <summary>The entries an event can come through (ADR 0019).</summary>
public static class EventSources
{
    public const string Server = "server";

    /// <summary>A person at a vehicle, through its onboard terminal.</summary>
    public const string OnboardTerminal = "onboard-terminal";

    /// <summary>A person through the console (the command line logs in this way too).</summary>
    public const string Console = "console";

    /// <summary>An AI session through MCP.</summary>
    public const string Mcp = "mcp";

    /// <summary>A person on the server machine, through a server command (setting the administrator's password).</summary>
    public const string ServerCommand = "server-command";
}
