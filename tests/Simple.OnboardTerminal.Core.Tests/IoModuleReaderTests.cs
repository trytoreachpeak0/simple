using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Simple.IoSimulator.Core;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>
/// The terminal reading its IO module over Modbus TCP (ADR 0024), here the IO
/// simulator's module on loopback, on real time: every point's raw value, how
/// often it changed and how long ago, and whether the module can be read.
/// </summary>
public sealed class IoModuleReaderTests : IAsyncDisposable
{
    private readonly Cabinet cabinet = new(TimeProvider.System, CabinetTiming.Default);
    private readonly IoModuleServer module;
    private readonly IoModuleReader reader;
    private readonly CancellationTokenSource stop = new();
    private readonly Task running;
    private int changed;

    public IoModuleReaderTests()
    {
        module = IoModuleServer.Start(cabinet, new IPEndPoint(IPAddress.Loopback, 0), NullLoggerFactory.Instance);
        reader = new IoModuleReader(module.Endpoint, TimeProvider.System, NullLoggerFactory.Instance);
        reader.Changed += () => Interlocked.Increment(ref changed);
        running = reader.RunAsync(stop.Token);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItReadsEveryInputAndOutputAsTheModuleHasThem()
    {
        await Until(() => reader.Read().Readable);

        var reading = reader.Read();
        Assert.Equal(32, reading.Points.Count);
        // Every slot locked and empty: all 16 inputs 1; every output 0.
        Assert.All(reading.Points.Where(p => p.Kind == IoPointKind.Input), p => Assert.True(p.Value));
        Assert.All(reading.Points.Where(p => p.Kind == IoPointKind.Output), p => Assert.False(p.Value));
        Assert.Equal(Enumerable.Range(1, 16), reading.Points.Where(p => p.Kind == IoPointKind.Input).Select(p => p.Number));
        Assert.All(reading.Points, p => Assert.Equal(0UL, p.Changes));
        Assert.InRange(reading.SinceRead!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AChangeIsCountedAndToldAtOnce()
    {
        await Until(() => reader.Read().Readable);
        var before = Volatile.Read(ref changed);

        // Slot 4's light curtain (DI12) reads cargo.
        cabinet.PinLightCurtain(4, false);

        await Until(() => !Input(12).Value, seconds: 2);
        Assert.Equal(1UL, Input(12).Changes);
        Assert.InRange(Input(12).SinceChange, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.True(Volatile.Read(ref changed) > before);
        Assert.Equal(0UL, Input(11).Changes);
        Assert.True(Input(11).SinceChange > Input(12).SinceChange);
    }

    [Fact]
    public async Task AModuleThatStopsAnsweringIsUnreadableAndItsLastValuesAge()
    {
        await Until(() => reader.Read().Readable);

        module.SetFault(ModbusFault.NoResponse, TimeSpan.Zero);

        await Until(() => !reader.Read().Readable, seconds: 5);
        var unreadable = reader.Read();
        Assert.Equal(32, unreadable.Points.Count);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
        Assert.True(reader.Read().SinceRead > unreadable.SinceRead);

        module.SetFault(ModbusFault.None, TimeSpan.Zero);
        await Until(() => reader.Read().Readable, seconds: 5);
    }

    [Fact]
    public async Task ItReconnectsAfterTheModuleDroppedTheConnection()
    {
        await Until(() => reader.Read().Readable);

        module.SetFault(ModbusFault.Disconnect, TimeSpan.Zero);
        await Until(() => !reader.Read().Readable, seconds: 5);
        module.SetFault(ModbusFault.None, TimeSpan.Zero);

        await Until(() => reader.Read().Readable, seconds: 5);
    }

    [Fact]
    public async Task TheFirstAttemptEndsWithinTheReadTimeoutEvenWithNoModuleThere()
    {
        // A loopback port nothing listens on.
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var nothing = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();
        using var absent = new IoModuleReader(nothing, TimeProvider.System, NullLoggerFactory.Instance);
        using var stopAbsent = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        var reading = absent.RunAsync(stopAbsent.Token);

        await absent.FirstAttempt.WaitAsync(IoModuleReader.ReadTimeout * 3, Cancel);
        Assert.False(absent.Read().Readable);
        await stopAbsent.CancelAsync();
        await reading;
        await reader.FirstAttempt.WaitAsync(IoModuleReader.ReadTimeout * 3, Cancel);
        Assert.True(reader.Read().Readable);
    }

    [Fact]
    public async Task WithoutAModuleNothingIsReadable()
    {
        using var none = new IoModuleReader(null, TimeProvider.System, NullLoggerFactory.Instance);

        await none.RunAsync(Cancel);

        Assert.True(none.FirstAttempt.IsCompleted);
        var reading = none.Read();
        Assert.False(reading.Readable);
        Assert.Null(reading.SinceRead);
        Assert.Empty(reading.Points);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await running;
        reader.Dispose();
        module.Dispose();
        cabinet.Dispose();
        stop.Dispose();
    }

    private IoPointReading Input(int number) => reader.Read().Points.Single(p => p.Kind == IoPointKind.Input && p.Number == number);

    private static async Task Until(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not so within {seconds} s.");
            await Task.Delay(20, Cancel);
        }
    }
}
