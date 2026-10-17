using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.OnboardTerminals;
using Simple.Server.Status;

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
                // Its reports never say the IO module can be read.
                EventTypes.OnboardIoUnreadable,
                // The first input was too long and the last two over the count: inputs 2 to 16.
                .. Enumerable.Repeat(EventTypes.OnboardInputReceived, OnboardTerminalRegistry.MaxInputsPerReport - 1),
                EventTypes.OnboardStale,
                EventTypes.OnboardFresh,
                EventTypes.OnboardDisconnected,
            ],
            types);
    }

    [Fact]
    public void TheLatestReportsIoPointsAreListedByKindAndNumberAsRawValues()
    {
        using var registry = Registry(new FakeTimeProvider(Start));
        using var link = registry.Open("test");
        var report = Report(1, 0);
        report.IoModuleReadable = true;
        report.IoReadAgeMs = 120;
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Output, Number = 1, Value = false, Changes = 0, MsSinceChange = 9000 });
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Input, Number = 2, Value = true, Changes = 0, MsSinceChange = 9000 });
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Input, Number = 1, Value = false, Changes = 3, MsSinceChange = 250 });

        Assert.Null(registry.Receive(link, report));

        var io = Assert.Single(registry.Snapshot()).Io;
        Assert.Equal(120UL, io.ReadAgeMs);
        Assert.Equal([new OnboardIoPointStatus(1, 0, 3, 250), new OnboardIoPointStatus(2, 1, 0, 9000)], io.Inputs);
        Assert.Equal([new OnboardIoPointStatus(1, 0, 0, 9000)], io.Outputs);

        var later = Report(2, 0);
        Assert.Null(registry.Receive(link, later));
        var none = Assert.Single(registry.Snapshot()).Io;
        Assert.Null(none.ReadAgeMs);
        Assert.Empty(none.Inputs);
    }

    [Fact]
    public void IoPointsTheServerCannotUseAreSkipped()
    {
        using var registry = Registry(new FakeTimeProvider(Start));
        using var link = registry.Open("test");
        var report = Report(1, 0);
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Unspecified, Number = 1 });
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Input, Number = 0 });
        report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Input, Number = OnboardTerminalRegistry.MaxIoPointNumber + 1 });
        // The three above and DO1 reported over and over fill the report's
        // limit; DO2, reported beyond it, is skipped. For DO1 the last within
        // the limit counts.
        var withinLimit = OnboardTerminalRegistry.MaxIoPointsPerReport - 3;
        report.IoPoints.Add(Enumerable.Range(1, withinLimit).Select(i => new IoPoint { Kind = IoPointKind.Output, Number = 1, Changes = (ulong)i }));
        report.IoPoints.Add(Enumerable.Range(1, 5).Select(_ => new IoPoint { Kind = IoPointKind.Output, Number = 2, Value = true }));

        Assert.Null(registry.Receive(link, report));

        var io = Assert.Single(registry.Snapshot()).Io;
        Assert.Empty(io.Inputs);
        Assert.Equal([new OnboardIoPointStatus(1, 0, (ulong)withinLimit, 0)], io.Outputs);
    }

    [Fact]
    public async Task AnIoModuleTurningUnreadableOrReadableAgainIsRecorded()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance();
        using var _ = server.Start();
        using var registry = new OnboardTerminalRegistry(
            time, server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OnboardTerminalRegistry>.Instance);
        using var link = registry.Open("test");

        // Unreadable from the first report, readable, readable, unreadable.
        foreach (var (seq, readable) in new[] { (1UL, false), (2UL, true), (3UL, true), (4UL, false) })
        {
            var report = Report(seq, 0);
            report.IoModuleReadable = readable;
            Assert.Null(registry.Receive(link, report));
        }

        await registry.FlushAsync(TestContext.Current.CancellationToken);

        await using var db = server.CreateDbContext();
        var types = await db.Events.Where(e => e.Type.StartsWith("onboard.io-")).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([EventTypes.OnboardIoUnreadable, EventTypes.OnboardIoReadable, EventTypes.OnboardIoUnreadable], types);
    }
}
