using Polly;
using Polly.Retry;

namespace Simple.OnboardTerminal.Core;

/// <summary>
/// How a terminal retries after its stream fails (ADR 0008, 0025): 1 s, then
/// longer and longer with jitter, never more than <see cref="MaxDelay"/>
/// between two attempts, for as long as the terminal runs.
/// </summary>
public static class ReconnectPolicy
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 25 s (the product owner, 2026-10-17): a server that comes back right
    /// after a failed attempt is found by the next one, within 30 s with the
    /// time to connect (#18, step 6).
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(25);

    /// <param name="onRetry">Told the attempt number (1 for the first retry) and the delay before it.</param>
    public static ResiliencePipeline Create(TimeProvider time, Action<int, TimeSpan, Exception?> onRetry) =>
        new ResiliencePipelineBuilder { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions
            {
                // Every failure is retried; only the terminal's own stop ends it.
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null && !args.Context.CancellationToken.IsCancellationRequested),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = FirstDelay,
                MaxDelay = MaxDelay,
                MaxRetryAttempts = int.MaxValue,
                OnRetry = args =>
                {
                    onRetry(args.AttemptNumber + 1, args.RetryDelay, args.Outcome.Exception);
                    return default;
                },
            })
            .Build();
}
