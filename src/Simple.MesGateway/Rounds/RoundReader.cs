using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Rounds;

/// <summary>
/// Reads the MES in rounds, one at a time. After a good round it waits
/// <see cref="GatewaySettings.RoundInterval"/>; after a failed one it tries
/// again with growing, jittered waits up to <see cref="GatewaySettings.RetryMaxDelay"/>
/// and never gives up (ADR 0008). Every attempt is a round of its own and is
/// published, so the snapshot shows a failure while the retries go on.
/// </summary>
public sealed partial class RoundReader(
    IMesSource source,
    SnapshotStore snapshots,
    IOptions<GatewaySettings> settings,
    TimeProvider time,
    ILogger<RoundReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RoundFailedException>(),
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = settings.Value.RetryDelay,
                MaxDelay = settings.Value.RetryMaxDelay,
            })
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            await pipeline.ExecuteAsync(ReadRoundAsync, stoppingToken);
            await Task.Delay(settings.Value.RoundInterval, time, stoppingToken);
        }
    }

    private async ValueTask ReadRoundAsync(CancellationToken cancellationToken)
    {
        var round = snapshots.NextRound();
        var readAt = time.GetUtcNow();
        MesReadResult result;
        try
        {
            result = await source.ReadAsync(cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever a source throws, the read failed; the round says why.
            Fail(round, readAt, new MesReadFailure(MesReadFailure.Read, e.Message), e);
            throw new RoundFailedException();
        }

        if (!result.TryGetRows(out var rows, out var error))
        {
            Fail(round, readAt, new MesReadFailure(MesReadFailure.Format, error!), null);
            throw new RoundFailedException();
        }

        snapshots.Publish(new MesSnapshot(round, readAt, true, null, rows));
        LogRead(logger, round, rows.Count);
    }

    private void Fail(long round, DateTimeOffset readAt, MesReadFailure failure, Exception? exception)
    {
        snapshots.Publish(new MesSnapshot(round, readAt, false, failure, null));
        LogFailed(logger, exception, round, failure.Kind, failure.Message);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Round {Round}: read {Rows} rows")]
    private static partial void LogRead(ILogger logger, long round, int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Round {Round} failed ({Kind}), retrying with backoff: {Message}")]
    private static partial void LogFailed(ILogger logger, Exception? exception, long round, string kind, string message);

    /// <summary>Tells the retry that a round failed; the round itself has already been published.</summary>
    private sealed class RoundFailedException : Exception;
}
