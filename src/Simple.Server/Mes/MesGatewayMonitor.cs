using Microsoft.Extensions.Options;
using Simple.Server.Events;

namespace Simple.Server.Mes;

/// <summary>
/// What the server last learned from the MES gateway, for the status API, and
/// the event record of the MES becoming unavailable and available again.
/// The MES is available when the gateway answers and its latest round
/// succeeded. It becomes unavailable for one of two causes, which are the
/// glossary's two faults: <see cref="CauseGateway"/> (MES gateway fault: the
/// gateway does not answer) and <see cref="CauseRead"/> (MES fault: the
/// gateway answers but could not read the MES or got the wrong shape).
/// </summary>
public sealed partial class MesGatewayMonitor(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<MesGatewaySettings> settings,
    ILogger<MesGatewayMonitor> logger)
{
    public const string CauseGateway = "gateway";
    public const string CauseRead = "read";

    private Observation current = new(null, null, null, null, false, null, null);

    public MesGatewayStatus Status()
    {
        var o = Volatile.Read(ref current);
        return new MesGatewayStatus(
            settings.Value.Url!,
            o.Reachable,
            o.CheckedAt,
            o.GatewayError,
            Available: IsAvailable(o) ? true : o.OpenCause is not null ? false : null,
            o.UnavailableSince,
            o.Snapshot is { } s ? new MesRoundStatus(s.Round, s.ReadAt, s.Succeeded, s.Failure) : null,
            o.Snapshot?.Rows?.Count,
            o.Snapshot?.Rows);
    }

    /// <summary>The gateway did not answer, or not with a snapshot.</summary>
    public Task GatewayUnreachableAsync(string error, CancellationToken cancellationToken) =>
        ObserveAsync(o => o with { Reachable = false, CheckedAt = time.GetUtcNow(), GatewayError = error }, cancellationToken);

    /// <summary>
    /// The gateway answered with its latest round. Round 0 (a gateway that
    /// has just started and not read yet) is no news: the last round stays
    /// shown and an open problem stays open until a real round comes.
    /// </summary>
    public Task SnapshotReceivedAsync(MesSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ObserveAsync(
            o => o with
            {
                Reachable = true,
                CheckedAt = time.GetUtcNow(),
                GatewayError = null,
                NotReadYet = snapshot.Round == 0,
                Snapshot = snapshot.Round > 0 ? snapshot : o.Snapshot,
            },
            cancellationToken);
    }

    /// <summary>
    /// Applies one observation. A problem stays open from its
    /// <see cref="EventTypes.MesUnreachable"/> until the MES is available again
    /// and <see cref="EventTypes.MesRecovered"/> is recorded; in between, a
    /// change of cause is recorded as another <see cref="EventTypes.MesUnreachable"/>,
    /// and a gateway that answers but has not read yet changes nothing.
    /// </summary>
    private async Task ObserveAsync(Func<Observation, Observation> change, CancellationToken cancellationToken)
    {
        // Only the poller calls in, one observation at a time.
        var old = Volatile.Read(ref current);
        var next = change(old);
        string? type = null;
        object? data = null;
        if (Problem(next) is { } problem && problem.Cause != old.OpenCause)
        {
            next = next with { OpenCause = problem.Cause, UnavailableSince = old.UnavailableSince ?? next.CheckedAt };
            (type, data) = (EventTypes.MesUnreachable, new { problem.Cause, problem.Error, next.Snapshot?.Round });
        }
        else if (IsAvailable(next) && old.OpenCause is not null)
        {
            next = next with { OpenCause = null, UnavailableSince = null };
            (type, data) = (EventTypes.MesRecovered, new { next.Snapshot!.Round, RowCount = next.Snapshot.Rows?.Count, old.UnavailableSince });
        }

        if (type is not null)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(type, EventSources.Server, data, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Nothing is changed, so the next poll sees the same change and records it then.
                LogNotRecorded(logger, e, type);
                return;
            }
        }

        Volatile.Write(ref current, next);
    }

    private static bool IsAvailable(Observation o) => o.Reachable == true && !o.NotReadYet && o.Snapshot is { Succeeded: true };

    /// <summary>Why the MES is unavailable now, or null when it is available or nothing is known yet.</summary>
    private static (string Cause, string? Error)? Problem(Observation o) =>
        o.Reachable == false ? (CauseGateway, o.GatewayError)
        : !o.NotReadYet && o.Snapshot is { Succeeded: false } failed ? (CauseRead, failed.Failure?.Message)
        : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Type} in the event record; it is tried again at the next poll")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string type);

    /// <param name="Snapshot">The latest round the gateway reported, kept while the gateway is unreachable.</param>
    /// <param name="NotReadYet">The gateway answered round 0 last time.</param>
    /// <param name="OpenCause">The cause recorded in the last <see cref="EventTypes.MesUnreachable"/>, until the recovery is recorded.</param>
    private sealed record Observation(
        bool? Reachable,
        DateTimeOffset? CheckedAt,
        string? GatewayError,
        MesSnapshot? Snapshot,
        bool NotReadYet,
        string? OpenCause,
        DateTimeOffset? UnavailableSince);
}

/// <summary>The MES gateway column of the status API.</summary>
/// <param name="Url">Where the server reaches the gateway.</param>
/// <param name="Reachable">Whether the gateway answered the last fetch; null before the first.</param>
/// <param name="CheckedAt">When the server last fetched, by the server's clock.</param>
/// <param name="GatewayError">Why the last fetch failed; null when it succeeded.</param>
/// <param name="Available">The gateway answered and its latest round succeeded; null while nothing is known.</param>
/// <param name="UnavailableSince">When the MES became unavailable, by the server's clock; null while available.</param>
/// <param name="LastRound">The gateway's latest round; kept, with its time, while the gateway is unreachable.</param>
/// <param name="RowCount">Rows of the latest round; null when it failed.</param>
/// <param name="Rows">The rows themselves; null when the latest round failed.</param>
public sealed record MesGatewayStatus(
    Uri Url,
    bool? Reachable,
    DateTimeOffset? CheckedAt,
    string? GatewayError,
    bool? Available,
    DateTimeOffset? UnavailableSince,
    MesRoundStatus? LastRound,
    int? RowCount,
    IReadOnlyList<MesRow>? Rows);

/// <param name="ReadAt">When the gateway began the read, by the gateway's clock.</param>
public sealed record MesRoundStatus(long Round, DateTimeOffset? ReadAt, bool Succeeded, MesReadFailure? Failure);
