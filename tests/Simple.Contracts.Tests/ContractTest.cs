using RIoT.Sdk.Facade;
using Simple.Contracts.Tests.Targets;

namespace Simple.Contracts.Tests;

/// <summary>
/// A contract test: one target per test (a fresh fake, or the real RIoT the
/// person selected), reached through riot-sdk's facade.
/// </summary>
public abstract class ContractTest : IAsyncLifetime
{
    private RiotTarget? target;

    protected RiotTarget Target => target ?? throw new InvalidOperationException("The target starts in InitializeAsync.");

    protected RiotSession Riot => Target.Session;

    protected RiotTargetFacts Facts => Target.Facts;

    protected static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => target = await RiotTarget.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        if (target is not null)
        {
            await target.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Reads until <paramref name="done"/> holds, within the target's patience; fails with the last reading otherwise.</summary>
    protected async Task<T> UntilAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> done, string what)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTimeOffset.UtcNow + Target.Patience;
        while (true)
        {
            var value = await read(Cancel);
            if (done(value))
            {
                return value;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"{Target.Description}：{Target.Patience.TotalSeconds} 秒内没有等到{what}；最后读到 {value}。");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel);
        }
    }

    /// <summary>Reads throughout the target's quiet window and requires <paramref name="holds"/> of every reading.</summary>
    protected async Task<IReadOnlyList<T>> ThroughoutAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> holds, string what)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(holds);
        var readings = new List<T>();
        var end = DateTimeOffset.UtcNow + Target.QuietWindow;
        do
        {
            var value = await read(Cancel);
            readings.Add(value);
            Assert.True(holds(value), $"{Target.Description}：本应一直{what}，却读到 {value}。");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancel);
        }
        while (DateTimeOffset.UtcNow < end);
        return readings;
    }
}
