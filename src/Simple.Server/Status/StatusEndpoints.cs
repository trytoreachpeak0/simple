using Simple.Server.Accounts;
using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Listeners;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;
using Simple.Server.Riot;

namespace Simple.Server.Status;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/status", (ServerInfo info, IOptions<ServerSettings> settings, OnboardTerminalRegistry terminals, OnboardEndpoint endpoint, ConsoleEndpoint console, MesGatewayMonitor mes, RiotConnectionMonitor riot) =>
                new StatusResponse(
                    info.Version,
                    info.StartedAt,
                    settings.Value.Instance!,
                    settings.Value.DatabaseName,
                    endpoint.Describe(),
                    OnboardTerminals: terminals.Snapshot(),
                    PendingOnboardTerminals: terminals.Pending(),
                    Riot: riot.Status(),
                    MesGateway: mes.Status(),
                    ConsoleEndpoint: console.Describe(),
                    OverviewEndpoint: console.DescribeOverview()))
            .RequireAccess(Access.View)
            .WithName("GetStatus")
            .WithSummary("Server version, start time and instance, and the state of every connected part.");
    }
}

/// <param name="OnboardTerminalEndpoint">Where onboard terminals connect and the pin they check; null when the server has no terminal endpoint.</param>
/// <param name="OnboardTerminals">Every vehicle whose onboard terminal's reports the server has used since it started: its key is registered for the vehicle (ADR 0025).</param>
/// <param name="PendingOnboardTerminals">Every onboard terminal key waiting for confirmation (ADR 0025, 0029); their reports are not used.</param>
/// <param name="Riot">Whether the server reaches the fleet manager.</param>
/// <param name="MesGateway">What the server last learned from the MES gateway.</param>
/// <param name="ConsoleEndpoint">The console's HTTPS address and its certificate, with the expiry warning (ADR 0046); null without one.</param>
/// <param name="OverviewEndpoint">The vehicle overview's plain address; null without one.</param>
public sealed record StatusResponse(
    string Version,
    DateTimeOffset StartedAt,
    string Instance,
    string Database,
    OnboardTerminalEndpointStatus? OnboardTerminalEndpoint,
    IReadOnlyList<OnboardTerminalStatus> OnboardTerminals,
    IReadOnlyList<PendingOnboardTerminalStatus> PendingOnboardTerminals,
    RiotStatus Riot,
    MesGatewayStatus MesGateway,
    ConsoleEndpointStatus? ConsoleEndpoint = null,
    OverviewEndpointStatus? OverviewEndpoint = null);

/// <param name="Url">The address terminals are configured with.</param>
/// <param name="CertificatePin">What terminals pin: the SHA-256 of the server certificate's public key, in hex.</param>
public sealed record OnboardTerminalEndpointStatus(string Url, string CertificatePin);

/// <param name="VehicleId">The vehicle the terminal claims, for which its key is registered.</param>
/// <param name="TerminalVersion">The terminal program's version, from its latest report.</param>
/// <param name="Connected">Whether its stream is open now.</param>
/// <param name="RemoteAddress">Where its open stream comes from.</param>
/// <param name="FreshReportAgeSeconds">
/// On the server's clock, at most how long ago its freshest report was
/// written (ADR 0024); null when no report has been fresh yet.
/// </param>
/// <param name="Stale">Its freshest report is older than ten seconds, or none was fresh within ten seconds of its first report.</param>
/// <param name="IoModuleReadable">Whether it reported that it can read its IO module.</param>
/// <param name="ScreenText">The screen text of the latest desired state sent to it.</param>
/// <param name="Io">The raw IO points of its latest report.</param>
/// <param name="KeyPin">The pin of the key its reports were last used from.</param>
public sealed record OnboardTerminalStatus(
    string VehicleId,
    string TerminalVersion,
    bool Connected,
    string? RemoteAddress,
    double? FreshReportAgeSeconds,
    bool Stale,
    bool IoModuleReadable,
    string? ScreenText,
    OnboardIoStatus Io,
    string KeyPin);

/// <summary>A terminal key waiting for confirmation (ADR 0025, 0029): unknown to the server, or no longer registered for the vehicle it claims.</summary>
/// <param name="KeyPin">The pin of the certificate the terminal presents: the SHA-256 of its public key, in hex.</param>
/// <param name="VehicleId">The vehicle its newest stream claims; an engineer confirms it as this vehicle, never another.</param>
/// <param name="PairingCode">The six digits its screen shows too.</param>
/// <param name="Connected">Whether a stream of it is open now.</param>
/// <param name="RemoteAddress">Where its open stream comes from.</param>
/// <param name="TerminalVersion">The terminal program's version, from its latest report.</param>
/// <param name="FirstSeenAt">When it started waiting, on the server's clock.</param>
/// <param name="InVehicleList">Whether the vehicle it claims is in the vehicle list; only then can it be confirmed.</param>
/// <param name="VehicleOnlineWithAnotherKey">The vehicle it claims is online now with its registered key: "this vehicle is online, on another computer".</param>
/// <param name="RegisteredForVehicle">The vehicle the key is registered for, when that is another one than it claims now.</param>
/// <param name="CloneSuspected">It waits because two computers sent fresh reports with this key at once (a cloned disk).</param>
public sealed record PendingOnboardTerminalStatus(
    string KeyPin,
    string VehicleId,
    string PairingCode,
    bool Connected,
    string? RemoteAddress,
    string TerminalVersion,
    DateTimeOffset FirstSeenAt,
    bool InVehicleList,
    bool VehicleOnlineWithAnotherKey,
    string? RegisteredForVehicle,
    bool CloneSuspected);

/// <summary>A vehicle's registered terminal key (ADR 0025).</summary>
/// <param name="ConfirmedBy">The account that confirmed it.</param>
public sealed record OnboardTerminalKeyStatus(string VehicleId, string KeyPin, DateTimeOffset ConfirmedAt, string? ConfirmedBy);

/// <summary>
/// A terminal's IO module as its latest report gave it (ADR 0024): raw
/// values, not yet read through the vehicle's IO point map. Ages are on the
/// terminal's clock, as of when it wrote that report.
/// </summary>
/// <param name="ReadAgeMs">How long before the report the module was last read successfully; null when it has not been since the terminal started.</param>
/// <param name="Inputs">DI1, DI2… as reported.</param>
/// <param name="Outputs">DO1, DO2… as reported.</param>
public sealed record OnboardIoStatus(ulong? ReadAgeMs, IReadOnlyList<OnboardIoPointStatus> Inputs, IReadOnlyList<OnboardIoPointStatus> Outputs)
{
    public static OnboardIoStatus None { get; } = new(null, [], []);
}

/// <param name="Number">The module's channel, from 1.</param>
/// <param name="Value">The raw value, 0 or 1.</param>
/// <param name="Changes">How often it changed since the terminal started.</param>
/// <param name="MsSinceChange">How long before the report it last changed, or, if it never did, was first read.</param>
public sealed record OnboardIoPointStatus(int Number, int Value, ulong Changes, ulong MsSinceChange);
