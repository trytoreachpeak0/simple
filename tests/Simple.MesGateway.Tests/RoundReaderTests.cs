using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Rounds;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

/// <summary>Rounds and retries on a fake clock, so minutes of backoff take a moment.</summary>
public sealed class RoundReaderTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>The first waits of an exponential backoff from 1 s without jitter.</summary>
    private static readonly TimeSpan[] PlainBackoff = [.. new[] { 1, 2, 4, 8, 16 }.Select(s => TimeSpan.FromSeconds(s))];

    private readonly Clock time = new();
    private readonly SnapshotStore snapshots = new();
    private readonly ScriptedSource source;
    private readonly RoundReader reader;

    public RoundReaderTests()
    {
        source = new ScriptedSource(time);
        var settings = new GatewaySettings
        {
            Source = GatewaySettings.FakeSource,
            RoundInterval = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
            RetryMaxDelay = TimeSpan.FromSeconds(30),
        };
        reader = new RoundReader(source, snapshots, Options.Create(settings), time, NullLogger<RoundReader>.Instance);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => reader.Dispose();

    [Fact]
    public async Task GoodRoundsFollowEachOtherAtTheRoundInterval()
    {
        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => source.Reads.Count >= 4);
        await reader.StopAsync(Cancel);

        Assert.All(Gaps(source.Reads), gap => Assert.Equal(TimeSpan.FromSeconds(5), gap));
        var latest = snapshots.Latest;
        Assert.True(latest.Succeeded);
        Assert.Equal(source.Reads.Count, latest.Round);
        Assert.Equal(source.Reads[^1], latest.ReadAt);
        Assert.Null(latest.Failure);
        Assert.Equal(ScriptedSource.Row, Assert.Single(latest.Rows!));
    }

    [Fact]
    public async Task AFailingSourceIsRetriedWithGrowingWaitsCappedAt30SecondsAndNeverGivenUp()
    {
        source.FailuresLeft = int.MaxValue;
        var failed = new List<MesSnapshot>();
        source.OnRead = () =>
        {
            if (snapshots.Latest.Round > 0)
            {
                failed.Add(snapshots.Latest);
            }
        };

        // 16 reads need at most 15 waits of 30 s; give the clock a little more and no further.
        var limit = time.GetUtcNow() + TimeSpan.FromSeconds(16 * 31);
        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => source.Reads.Count >= 16 || time.GetUtcNow() >= limit);
        await reader.StopAsync(Cancel);

        var gaps = Gaps(source.Reads);
        Assert.True(source.Reads.Count >= 16, $"Only {source.Reads.Count} reads in {16 * 31} s: the waits are not capped at 30 s. Waits: {string.Join(", ", gaps)}");
        // The clock moves in whole steps, so a wait shows up to one step late.
        Assert.All(gaps, gap => Assert.InRange(gap, TimeSpan.Zero, TimeSpan.FromSeconds(30) + Step));
        // Growing: the last waits are far longer than the first one (1 s).
        Assert.True(gaps.TakeLast(5).Average(g => g.TotalSeconds) >= 10, string.Join(", ", gaps));
        // Every attempt was a round of its own, published as failed with the source's message.
        Assert.Equal(Enumerable.Range(1, failed.Count).Select(i => (long)i), failed.Select(s => s.Round));
        Assert.All(failed, s =>
        {
            Assert.False(s.Succeeded);
            Assert.Null(s.Rows);
            Assert.Equal(new MesReadFailure(MesReadFailure.Read, ScriptedSource.FailureMessage), s.Failure);
        });
    }

    [Fact]
    public async Task AfterAFailureTheFirstGoodRoundIsPublishedAndRoundsGoBackToTheInterval()
    {
        source.FailuresLeft = 3;

        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => source.Reads.Count >= 6);
        await reader.StopAsync(Cancel);

        Assert.True(snapshots.Latest.Succeeded);
        Assert.Equal(6, snapshots.Latest.Round);
        Assert.All(Gaps(source.Reads).Skip(3), gap => Assert.Equal(TimeSpan.FromSeconds(5), gap));
    }

    [Fact]
    public async Task ARoundOfTheWrongShapeFailsAsAFormatFailureAndIsRetried()
    {
        source.MalformedLeft = 1;

        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => snapshots.Latest.Round >= 1);
        var malformed = snapshots.Latest;
        await AdvanceUntilAsync(() => snapshots.Latest.Round >= 2);
        await reader.StopAsync(Cancel);

        Assert.False(malformed.Succeeded);
        Assert.Equal(new MesReadFailure(MesReadFailure.Format, "Row 1 has 6 values for 7 columns."), malformed.Failure);
        Assert.Null(malformed.Rows);
        Assert.True(snapshots.Latest.Succeeded);
        // Retried after a backoff wait, not after the round interval.
        Assert.InRange(Gaps(source.Reads)[0], TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ASourceThatReturnsNothingIsAFormatFailureAndTheReaderGoesOn()
    {
        source.ReturnNothingLeft = 2;

        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => snapshots.Latest.Round >= 1);
        var nothing = snapshots.Latest;
        await AdvanceUntilAsync(() => snapshots.Latest.Succeeded);
        await reader.StopAsync(Cancel);

        Assert.Equal(new MesReadFailure(MesReadFailure.Format, "The source returned no result."), nothing.Failure);
        Assert.Equal(3, snapshots.Latest.Round);
    }

    [Fact]
    public async Task RetryWaitsAreJittered()
    {
        source.FailuresLeft = int.MaxValue;

        // A fine clock, so a jittered wait shows as such: without jitter the
        // first waits would be exactly 1, 2, 4, 8 and 16 s.
        await reader.StartAsync(Cancel);
        await AdvanceUntilAsync(() => source.Reads.Count >= 6, TimeSpan.FromMilliseconds(100), 5000);
        await reader.StopAsync(Cancel);

        var gaps = Gaps(source.Reads).Take(5).ToList();
        Assert.False(gaps.SequenceEqual(PlainBackoff), $"The waits are not jittered: {string.Join(", ", gaps)}");
    }

    private static List<TimeSpan> Gaps(IReadOnlyList<DateTimeOffset> times) =>
        times.Zip(times.Skip(1), (a, b) => b - a).ToList();

    /// <summary>
    /// Moves the fake clock one step at a time until the condition holds.
    /// Before each step it waits until the reader is parked on its next wait,
    /// so no step happens while the reader is between a read and its wait.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan? step = null, int steps = 2000)
    {
        for (var i = 0; i < steps; i++)
        {
            await WaitUntilParkedAsync();
            if (condition())
            {
                return;
            }

            time.Advance(step ?? Step);
        }

        Assert.Fail($"The condition did not hold within {steps} steps of the fake clock.");
    }

    /// <summary>Every read is followed by exactly one wait (a retry wait or the round interval); parked means that wait is pending.</summary>
    private async Task WaitUntilParkedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(time.Created == source.Reads.Count && time.Fired == time.Created - 1))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The reader did not park: {source.Reads.Count} reads, {time.Created} waits started, {time.Fired} ended.");
            await Task.Delay(1, Cancel);
        }
    }

    /// <summary>A fake clock that counts the timers the reader starts and the ones that have fired.</summary>
    private sealed class Clock() : FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero))
    {
        private int created;
        private int fired;

        public int Created => Volatile.Read(ref created);

        public int Fired => Volatile.Read(ref fired);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref created);
            return base.CreateTimer(
                s =>
                {
                    Interlocked.Increment(ref fired);
                    callback(s);
                },
                state,
                dueTime,
                period);
        }
    }

    /// <summary>Gives one row per read, or fails while told to, and records when it was read by the fake clock.</summary>
    private sealed class ScriptedSource(TimeProvider time) : IMesSource
    {
        public const string FailureMessage = "ORA-12170: TNS:Connect timeout occurred";

        public static readonly MesRow Row = new("SAMPLE_TO_OVEN", "SMP0001.01", "S1-1", "SMP-DA-01", "SMP-CURE", "2026-01-05T08:00:00", "SMP-PKG-A");

        private readonly List<DateTimeOffset> reads = [];

        public int FailuresLeft { get; set; }

        public int MalformedLeft { get; set; }

        public int ReturnNothingLeft { get; set; }

        public Action? OnRead { get; set; }

        public IReadOnlyList<DateTimeOffset> Reads
        {
            get
            {
                lock (reads)
                {
                    return [.. reads];
                }
            }
        }

        public Task<MesReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            OnRead?.Invoke();
            lock (reads)
            {
                reads.Add(time.GetUtcNow());
            }

            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new IOException(FailureMessage);
            }

            if (ReturnNothingLeft > 0)
            {
                ReturnNothingLeft--;
                return Task.FromResult<MesReadResult>(null!);
            }

            object?[] values = [Row.TaskType, Row.Sublot, Row.Area, Row.Eqp, Row.Step, Row.Dates, Row.Package];
            if (MalformedLeft > 0)
            {
                MalformedLeft--;
                values = values[..^1];
            }

            return Task.FromResult(new MesReadResult(MesColumns.All, [values]));
        }
    }
}
