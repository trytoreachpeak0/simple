using Microsoft.Extensions.Options;

namespace Simple.FakeRiot;

/// <summary>Moves the fake's world on every <see cref="FakeRiotTiming.Tick"/>.</summary>
public sealed class FakeRiotClock(FakeRiotWorld world, IOptions<FakeRiotSettings> settings, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Value.Timing.Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            world.Tick();
        }
    }
}
