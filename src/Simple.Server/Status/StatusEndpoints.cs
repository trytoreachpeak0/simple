using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Status;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/status", (ServerInfo info, IOptions<ServerSettings> settings, OnboardTerminalRegistry terminals, OnboardEndpoint endpoint, MesGatewayMonitor mes) =>
                new StatusResponse(
                    info.Version,
                    info.StartedAt,
                    settings.Value.Instance!,
                    settings.Value.DatabaseName,
                    endpoint.Describe(),
                    OnboardTerminals: terminals.Snapshot(),
                    Riot: null,
                    MesGateway: mes.Status()))
            .WithName("GetStatus")
            .WithSummary("Server version, start time and instance, and the state of every connected part.");
    }
}

/// <param name="OnboardTerminalEndpoint">Where onboard terminals connect and the pin they check; null when the server has no terminal endpoint.</param>
/// <param name="OnboardTerminals">Every onboard terminal that has reported since the server started.</param>
/// <param name="Riot">The fleet manager connection; null until the RIoT adapter is wired in.</param>
/// <param name="MesGateway">What the server last learned from the MES gateway.</param>
public sealed record StatusResponse(
    string Version,
    DateTimeOffset StartedAt,
    string Instance,
    string Database,
    OnboardTerminalEndpointStatus? OnboardTerminalEndpoint,
    IReadOnlyList<OnboardTerminalStatus> OnboardTerminals,
    RiotStatus? Riot,
    MesGatewayStatus MesGateway);

/// <param name="Url">The address terminals are configured with.</param>
/// <param name="CertificatePin">What terminals pin: the SHA-256 of the server certificate's public key, in hex.</param>
public sealed record OnboardTerminalEndpointStatus(string Url, string CertificatePin);

/// <param name="VehicleId">The vehicle the terminal reports it is on; not confirmed yet (pairing comes later).</param>
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
public sealed record OnboardTerminalStatus(
    string VehicleId,
    string TerminalVersion,
    bool Connected,
    string? RemoteAddress,
    double? FreshReportAgeSeconds,
    bool Stale,
    bool IoModuleReadable,
    string? ScreenText);

// Placeholders: the tickets that connect each part give these their fields.
public sealed record RiotStatus;
