using Microsoft.Extensions.Options;
using Simple.Server.Configuration;

namespace Simple.Server.Status;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/status", (ServerInfo info, IOptions<ServerSettings> settings) =>
                new StatusResponse(
                    info.Version,
                    info.StartedAt,
                    settings.Value.Instance!,
                    settings.Value.DatabaseName,
                    OnboardTerminals: [],
                    Riot: null,
                    MesGateway: null))
            .WithName("GetStatus")
            .WithSummary("Server version, start time and instance, and the state of every connected part.");
    }
}

/// <param name="OnboardTerminals">Onboard terminals connected to the server; empty until the protocol is wired in.</param>
/// <param name="Riot">The fleet manager connection; null until the RIoT adapter is wired in.</param>
/// <param name="MesGateway">The MES gateway connection; null until the gateway is wired in.</param>
public sealed record StatusResponse(
    string Version,
    DateTimeOffset StartedAt,
    string Instance,
    string Database,
    IReadOnlyList<OnboardTerminalStatus> OnboardTerminals,
    RiotStatus? Riot,
    MesGatewayStatus? MesGateway);

// Placeholders: the tickets that connect each part give these their fields.
public sealed record OnboardTerminalStatus;

public sealed record RiotStatus;

public sealed record MesGatewayStatus;
