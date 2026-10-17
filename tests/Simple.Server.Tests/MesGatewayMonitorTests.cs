using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Simple.Server.Events;
using Simple.Server.Mes;

namespace Simple.Server.Tests;

/// <summary>
/// The monitor alone on a fake clock, so the edges of StaleAfter are exact.
/// It records into a running server's event record; the server's own monitor
/// sees a healthy stub and records nothing.
/// </summary>
public sealed class MesGatewayMonitorTests : IAsyncLifetime
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private static readonly MesRow Row = new("SAMPLE_TO_OVEN", "SMP0001.01", "S1-1", "SMP-DA-01", "SMP-CURE", "2026-01-05T08:00:00", "SMP-PKG-A");

    private readonly ServerInstance server = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private HttpClient client = null!;
    private MesGatewayMonitor monitor = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        client = server.Start();
        monitor = new MesGatewayMonitor(
            server.Services.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new MesGatewaySettings { Url = new Uri("http://127.0.0.1:9"), StaleAfter = StaleAfter }),
            NullLogger<MesGatewayMonitor>.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task ARoundUnchangedForExactlyStaleAfterIsStillFreshAndOneTickMoreIsNot()
    {
        await monitor.SnapshotReceivedAsync(Round(7), Cancel);
        var seenAt = time.GetUtcNow();

        time.Advance(StaleAfter);
        await monitor.SnapshotReceivedAsync(Round(7), Cancel);
        Assert.True(monitor.Status().Available);

        time.Advance(TimeSpan.FromTicks(1));
        await monitor.SnapshotReceivedAsync(Round(7), Cancel);
        var stale = monitor.Status();

        Assert.False(stale.Available);
        Assert.True(stale.Reachable);
        Assert.Equal(seenAt, stale.NewRoundSeenAt);
        Assert.Equal(time.GetUtcNow(), stale.UnavailableSince);

        time.Advance(TimeSpan.FromSeconds(2));
        await monitor.SnapshotReceivedAsync(Round(8), Cancel);
        Assert.True(monitor.Status().Available);
        Assert.Equal(time.GetUtcNow(), monitor.Status().NewRoundSeenAt);

        var events = await MonitorEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        Assert.Equal(MesGatewayMonitor.CauseRead, events[0].Data!.Value.GetProperty("cause").GetString());
        Assert.Equal("No new round for more than 00:01:30; the gateway still reports round 7.", events[0].Data!.Value.GetProperty("error").GetString());
        Assert.Equal(8, events[1].Data!.Value.GetProperty("round").GetInt64());
    }

    [Fact]
    public async Task ARoundThatGoesDownIsANewRound()
    {
        await monitor.SnapshotReceivedAsync(Round(500), Cancel);

        time.Advance(StaleAfter);
        await monitor.SnapshotReceivedAsync(Round(1), Cancel);
        time.Advance(StaleAfter);
        await monitor.SnapshotReceivedAsync(Round(1), Cancel);

        Assert.True(monitor.Status().Available);
        Assert.Empty(await MonitorEventsAsync());
    }

    [Fact]
    public async Task WhileTheGatewayDoesNotAnswerStalenessDoesNotReplaceTheGatewayCause()
    {
        await monitor.SnapshotReceivedAsync(Round(3), Cancel);
        time.Advance(StaleAfter * 2);

        await monitor.GatewayUnreachableAsync("refused", Cancel);

        var events = await MonitorEventsAsync();
        Assert.Equal(MesGatewayMonitor.CauseGateway, Assert.Single(events).Data!.Value.GetProperty("cause").GetString());
    }

    private static MesSnapshot Round(long round) => new(round, DateTimeOffset.UtcNow, true, null, [Row]);

    private async Task<List<EventView>> MonitorEventsAsync()
    {
        var page = await client.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        return page!.Items.Where(e => e.Type.StartsWith("mes.", StringComparison.Ordinal)).ToList();
    }
}
