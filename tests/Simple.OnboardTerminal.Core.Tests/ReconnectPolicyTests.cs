using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Simple.OnboardTerminal.Core.Tests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public async Task DelaysGrowFromAboutASecondAndNeverExceedTwentyFiveSeconds()
    {
        var time = new FakeTimeProvider();
        var delays = Channel.CreateUnbounded<TimeSpan>();
        var pipeline = ReconnectPolicy.Create(time, (_, delay, _) => delays.Writer.TryWrite(delay));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = pipeline.ExecuteAsync(_ => ValueTask.FromException(new IOException("refused")), stop.Token).AsTask();

        var seen = new List<TimeSpan>();
        // The clock moves on in small steps: a delay may start a moment after
        // it was reported, and the step size does not change what Polly chose.
        while (seen.Count < 60)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (delays.Reader.TryRead(out var delay))
            {
                seen.Add(delay);
                continue;
            }

            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.InRange(seen[0], TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.All(seen, d => Assert.InRange(d, TimeSpan.Zero, ReconnectPolicy.MaxDelay));
        // Once grown, the delays stay near the cap: retrying goes on, slowly.
        Assert.InRange(seen.Skip(10).Average(d => d.TotalSeconds), 10, 25);
    }
}
