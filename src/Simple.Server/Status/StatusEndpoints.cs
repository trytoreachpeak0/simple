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
/// <param name="OnboardTerminals">Every onboard terminal that has reported since the server started.</param>
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
    RiotStatus Riot,
    MesGatewayStatus MesGateway,
    ConsoleEndpointStatus? ConsoleEndpoint = null,
    OverviewEndpointStatus? OverviewEndpoint = null);

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
