using Microsoft.Extensions.Options;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Server.Riot;

/// <summary>
/// Reads every map and every station from the fleet manager, read only, for
/// checking the site configuration against them (ADR 0015, 0020, 0021). The
/// areas a station serves come from its name (<see cref="StationAreas"/>).
/// </summary>
public sealed class RiotMapReader(IHttpMessageHandlerFactory handlers, IOptions<RiotSettings> settings)
{
    /// <exception cref="InvalidOperationException">No fleet manager is configured (<c>Simple:Riot:Url</c>).</exception>
    public async Task<FleetMaps> ReadAsync(CancellationToken cancellationToken)
    {
        if (!settings.Value.Configured)
        {
            throw new InvalidOperationException($"No fleet manager is configured ({RiotSettings.Section}:Url), so there are no maps to read.");
        }

        var options = new RiotOptions
        {
            BaseUrl = settings.Value.Url!.ToString(),
            CallApiKey = settings.Value.CallApiKey,
            Timeout = settings.Value.RequestTimeout,
        };
        // The factory's handler has automatic redirects off (ADR 0028), as for the probe.
        await using var session = new RiotSession(options, handlers.CreateHandler(RiotConnectionProbe.HttpClientName));
        var maps = new List<FleetMap>();
        foreach (var map in await session.Maps.ListMapsAsync(cancellationToken))
        {
            var stations = await session.Maps.ListStationsAsync(map.MapId, cancellationToken);
            maps.Add(new FleetMap(map.MapId, map.Name, [.. stations.Select(s => new FleetStation(s.StationId, s.Name, StationAreas.Of(s.Name ?? string.Empty)))]));
        }

        return new FleetMaps(maps);
    }
}
