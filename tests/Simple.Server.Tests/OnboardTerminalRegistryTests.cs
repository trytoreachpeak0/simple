using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Tests;

/// <summary>The registry on its own, with the clock in the test's hand and no stream in between.</summary>
public sealed class OnboardTerminalRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private static OnboardReport Report(ulong seq, ulong acknowledging, string vehicle = "vehicle-1") =>
        new() { Seq = seq, LastReceivedSeq = acknowledging, VehicleId = vehicle, TerminalVersion = "t" };

    /// <summary>A registry whose events go nowhere (they fail and are logged): for tests that only look at its state.</summary>
    private static OnboardTerminalRegistry Registry(TimeProvider time) =>
        new(time, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<OnboardTerminalRegistry>.Instance);

    [Theory]
    [InlineData(10.0, 10.0)]
    [InlineData(11.0, null)]
    public void AReportIsFreshOnlyIfTheStateItNamesWasSentAtMostTenSecondsAgo(double secondsLater, double? age)
    {
        // Nothing is sent in between, as when the server's writes are stuck:
        // the state the report names is still remembered, and only the
        // ten-second bound decides.
        var time = new FakeTimeProvider(Start);
        using var registry = Registry(time);
        using var link = registry.Open("test");
        registry.NextDesiredState(link);
        Assert.Null(registry.Receive(link, Report(1, acknowledging: 0)));

        time.Advance(TimeSpan.FromSeconds(secondsLater));
        Assert.Null(registry.Receive(link, Report(2, acknowledging: 1)));

        Assert.Equal(age, Assert.Single(registry.Snapshot()).FreshReportAgeSeconds);
    }

    [Fact]
    public void AVehicleIdOverTheLimitEndsTheStream()
    {
        using var registry = Registry(new FakeTimeProvider(Start));
        using var link = registry.Open("test");

        var refusal = registry.Receive(link, Report(1, 0, vehicle: new string('v', OnboardTerminalRegistry.MaxVehicleIdLength + 1)));

        Assert.Contains("longer than", refusal, StringComparison.Ordinal);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task EventsGoInInTheOrderDecidedAndInputsOverTheLimitsAreSkipped()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance();
        using var _ = server.Start();
        using var registry = new OnboardTerminalRegistry(
            time, server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OnboardTerminalRegistry>.Instance);
        using var link = registry.Open("test");

        registry.NextDesiredState(link);
        var first = Report(1, acknowledging: 1);
        first.Inputs.Add(Enumerable.Range(1, OnboardTerminalRegistry.MaxInputsPerReport + 2)
            .Select(i => new OnboardInput { Id = (ulong)i, Scan = new Scan { Text = $"LOT-{i}" } }));
        first.Inputs[0].Scan.Text = new string('x', OnboardTerminalRegistry.MaxInputTextLength + 1);
        Assert.Null(registry.Receive(link, first));

        time.Advance(TimeSpan.FromSeconds(10.5));
        registry.NextDesiredState(link);
        Assert.Null(registry.Receive(link, Report(2, acknowledging: 2)));
        registry.Close(link, "the terminal ended the stream");
        await registry.FlushAsync(TestContext.Current.CancellationToken);

        await using var db = server.CreateDbContext();
        var types = await db.Events.Where(e => e.Type.StartsWith("onboard.")).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [
                EventTypes.OnboardConnected,
                // The first input was too long and the last two over the count: inputs 2 to 16.
                .. Enumerable.Repeat(EventTypes.OnboardInputReceived, OnboardTerminalRegistry.MaxInputsPerReport - 1),
                EventTypes.OnboardStale,
                EventTypes.OnboardFresh,
                EventTypes.OnboardDisconnected,
            ],
            types);
    }
}
