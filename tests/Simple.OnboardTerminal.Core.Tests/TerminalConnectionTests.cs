using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Simple.IoSimulator.Core;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>A real terminal connection against <see cref="FakeServer"/>, on real time.</summary>
public sealed class TerminalConnectionTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItConnectsToThePinnedServerAndReportsItsVehicleAndWhatThePersonDid()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);

        await Until(() => terminal.Connection.Status.State == TerminalState.Connected);
        Assert.Equal("测试画面", terminal.Connection.Status.ScreenText);
        Assert.Equal("1", Assert.Single(server.MajorsAsked.Distinct()));
        var first = server.Reports.First();
        Assert.Equal("vehicle-7", first.VehicleId);
        Assert.Equal(1UL, first.Seq);
        Assert.Equal(terminal.Connection.Version, first.TerminalVersion);
        Assert.Equal(terminal.Key.Pin, Assert.Single(server.ClientPins));

        // A scan typed like a scanner reaches the next report as it was typed…
        Assert.Equal("LOT-0001", Assert.Single(terminal.Inputs.Type("LOT-0001\r\n")!).Text);
        await Until(() => server.Reports.Any(r => r.Inputs.Any(i => i.Scan?.Text == "LOT-0001")));
        // …and leaves the reports once the server has acknowledged one carrying it.
        var carriedFirst = server.Reports.First(r => r.Inputs.Count > 0).Seq;
        await Until(() => server.Reports.Any(r => r.Seq > carriedFirst + 3 && r.Inputs.Count == 0));
    }

    [Fact]
    public async Task ItReportsOnEveryDesiredStateSoTheServersBoundStaysShort()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);
        await Until(() => server.Reports.Count >= 10);

        // States come every 200 ms; a report answers each of them, not only once a second.
        var reports = server.Reports.ToList();
        Assert.True(reports.Count(r => r.LastReceivedSeq > 0) >= 8, $"{reports.Count} reports");
        Assert.Equal(reports.Select(r => r.Seq), Enumerable.Range(1, reports.Count).Select(i => (ulong)i));
    }

    [Fact]
    public async Task ItRefusesAServerWhoseCertificateIsNotThePinnedOneAndSaysWhy()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var other = new FakeServer();
        await using var terminal = new Terminal(server.Url, other.Pin);

        await Until(() => terminal.Connection.Status.State == TerminalState.ServerNotTrusted);

        Assert.Empty(server.Reports);
        Assert.Empty(server.MajorsAsked);
        var why = terminal.Log.First(l => l.Contains("not the pinned one", StringComparison.Ordinal));
        Assert.Contains($"pinned {other.Pin}", why, StringComparison.Ordinal);
        Assert.Contains($"presented {server.Pin}", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItReconnectsOnItsOwnWhenTheServerComesBack()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);
        await Until(() => terminal.Connection.Status.State == TerminalState.Connected);

        await server.StopAsync();
        await Until(() => terminal.Connection.Status.State == TerminalState.Disconnected);
        Assert.Equal("与服务端断开", terminal.Connection.Status.ScreenText);
        Assert.Null(terminal.Inputs.Type("LOT-0001\r"));
        await Task.Delay(TimeSpan.FromSeconds(3), Cancel);
        var before = server.Reports.Count;
        await server.StartAsync();

        // The delays so far are short (about 1, 2, 4 s), well inside the 25 s cap.
        await Until(() => terminal.Connection.Status.State == TerminalState.Connected, seconds: 15);
        await Until(() => server.Reports.Skip(before).Any(r => r.Seq == 1));
    }

    [Fact]
    public async Task AStopThatComesWhileAFailedStreamIsBeingRetriedEndsTheRunQuietly()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        var time = new StillClock();
        await using var terminal = new Terminal(server.Url, server.Pin, time);
        await Until(() => terminal.Connection.Status.State == TerminalState.Connected);

        // The first reading once the stream fails is the one that asks
        // whether it settled: the session has just seen that the terminal is
        // not stopping, and the retry has not yet looked. The stop lands
        // there, as it can on a busy machine (#55).
        var before = terminal.Log.Count;
        time.OnNextReading = terminal.Stop;
        await server.StopAsync();
        await Until(() => time.OnNextReading is null);

        await terminal.Running;
        Assert.Equal(TerminalState.Disconnected, terminal.Connection.Status.State);
        // The stop did land in that window: the stream failed while the
        // terminal was not stopping, and the retry turned it down at once
        // (a stop after that would have logged "Retry 1" first).
        var after = terminal.Log.Skip(before).ToList();
        Assert.Contains(after, l => l.StartsWith("Stream ended", StringComparison.Ordinal) || l.StartsWith("Disconnected (", StringComparison.Ordinal));
        Assert.DoesNotContain(after, l => l.StartsWith("Retry ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARetryAfterAFailureNobodyForesawSaysWhatFailed()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        var time = new StillClock();
        await using var terminal = new Terminal(server.Url, server.Pin, time);
        await Until(() => terminal.Connection.Status.State == TerminalState.Connected);

        // The reading that asks whether the failed stream settled throws
        // instead: a type the session does not catch, so only the retry sees it.
        var before = terminal.Log.Count;
        time.OnNextReading = () => throw new InvalidOperationException("the clock broke");
        await server.StopAsync();
        await Until(() => terminal.Log.Skip(before).Any(l => l.StartsWith("Retry 1 ", StringComparison.Ordinal)));

        var retry = terminal.Log.Skip(before).First(l => l.StartsWith("Retry 1 ", StringComparison.Ordinal));
        Assert.EndsWith("after InvalidOperationException: the clock broke", retry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2,3", "车载终端版本需要更新，请联系系统维护人员")]
    [InlineData("0", "车载终端与服务端版本不一致，请联系系统维护人员")]
    public async Task AServerOfOtherMajorsGetsTheMatchingScreenAndNoStorm(string majors, string screen)
    {
        await using var server = new FakeServer { RefuseServingMajors = majors };
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);

        await Until(() => terminal.Connection.Status.State == TerminalState.VersionMismatch);
        Assert.Equal(screen, terminal.Connection.Status.ScreenText);
        await Task.Delay(TimeSpan.FromSeconds(4), Cancel);
        // Retried with growing delays, never several times a second.
        Assert.InRange(server.MajorsAsked.Count, 1, 5);
    }

    [Fact]
    public async Task EveryReportCarriesTheIoModulesRawValuesAndAChangeGoesOutAtOnce()
    {
        using var cabinet = new Cabinet(TimeProvider.System, CabinetTiming.Default);
        using var module = IoModuleServer.Start(cabinet, new IPEndPoint(IPAddress.Loopback, 0), NullLoggerFactory.Instance);
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin, ioModule: module.Endpoint);

        await Until(() => server.Reports.Any(r => r.IoModuleReadable));
        var report = server.Reports.Last();
        Assert.Equal(32, report.IoPoints.Count);
        Assert.Equal(16, report.IoPoints.Count(p => p.Kind == IoPointKind.Input && p.Value));
        Assert.True(report.HasIoReadAgeMs);
        Assert.InRange(report.IoReadAgeMs, 0UL, 1000UL);

        // Slot 2's light curtain (DI10) reads cargo: a report says so within 2 s.
        var before = server.Reports.Count;
        cabinet.PinLightCurtain(2, false);
        await Until(() => server.Reports.Skip(before).Any(r => r.IoPoints.Any(p => p is { Kind: IoPointKind.Input, Number: 10, Value: false, Changes: 1 })), seconds: 2);
    }

    [Fact]
    public async Task AnIoChangeWakesAReportOfItsOwn()
    {
        // Nothing else wakes a report here: the server sends only its first
        // state, and the terminal's clock never moves, so the once-a-second
        // report never comes (ADR 0024: on every change at once).
        using var cabinet = new Cabinet(TimeProvider.System, CabinetTiming.Default);
        using var module = IoModuleServer.Start(cabinet, new IPEndPoint(IPAddress.Loopback, 0), NullLoggerFactory.Instance);
        await using var server = new FakeServer { StateInterval = null };
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin, new FakeTimeProvider(), ioModule: module.Endpoint);
        await Until(() => server.Reports.Any(r => r.IoModuleReadable));
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
        var before = server.Reports.Count;

        cabinet.PinLightCurtain(5, false);

        await Until(() => server.Reports.Skip(before).Any(r => r.IoPoints.Any(p => p is { Kind: IoPointKind.Input, Number: 13, Value: false })), seconds: 2);
        Assert.Equal(before + 1, server.Reports.Count);
    }

    [Fact]
    public async Task WithoutAnIoModuleTheReportsSayItCannotBeRead()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);

        await Until(() => server.Reports.Count >= 2);

        Assert.All(server.Reports, r => Assert.False(r.IoModuleReadable));
        Assert.All(server.Reports, r => Assert.Empty(r.IoPoints));
        Assert.All(server.Reports, r => Assert.False(r.HasIoReadAgeMs));
    }

    [Fact]
    public async Task ItPresentsItsOwnKeyOnEveryStreamAndKeepsItsRunIdAcrossReconnects()
    {
        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin);
        await Until(() => server.Reports.Count >= 2);

        await server.StopAsync();
        await server.StartAsync();
        await Until(() => server.ClientPins.Count >= 2 && terminal.Connection.Status.State == TerminalState.Connected, seconds: 30);

        Assert.All(server.ClientPins, pin => Assert.Equal(terminal.Key.Pin, pin));
        Assert.Matches("^[0-9a-f]{32}$", terminal.Connection.RunId);
        Assert.All(server.Reports, r => Assert.Equal(terminal.Connection.RunId, r.RunId));
    }

    [Fact]
    public async Task AKeyInTheTpmGetsThroughTheHandshakeToo()
    {
        var key = TerminalKey.OpenOrCreate(TestKeys.NewName(), TestKeyStore.IsMachine, tryTpm: true, out var tpmProblem);
        if (!key.InTpm)
        {
            TestKeys.Delete(key);
            Assert.Skip($"No TPM this account can use here: {tpmProblem}");
        }

        await using var server = new FakeServer();
        await server.StartAsync();
        await using var terminal = new Terminal(server.Url, server.Pin, key: key);

        await Until(() => terminal.Connection.Status.State == TerminalState.Connected);
        Assert.Equal(key.Pin, Assert.Single(server.ClientPins));
    }

    private static async Task Until(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not so within {seconds} s.");
            await Task.Delay(50, Cancel);
        }
    }

    /// <summary>A running terminal connection with its own key (deleted afterwards) and its log kept in memory.</summary>
    private sealed class Terminal : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly ILoggerFactory loggers;

        /// <param name="key">The terminal's key, deleted with the terminal; a new one in the software store when none is given.</param>
        public Terminal(string url, string pin, TimeProvider? time = null, IPEndPoint? ioModule = null, TerminalKey? key = null)
        {
            loggers = LoggerFactory.Create(logging => logging.AddProvider(new ListLogger(Log)));
            Io = new IoModuleReader(ioModule, TimeProvider.System, loggers);
            Key = key ?? TestKeys.Create();
            Connection = new TerminalConnection(new TerminalSettings("vehicle-7", url, pin), Key.Certificate, Inputs, time ?? TimeProvider.System, loggers, io: Io);
            Reading = Io.RunAsync(stop.Token);
            Running = Connection.RunAsync(stop.Token);
        }

        public IoModuleReader Io { get; }

        public TerminalKey Key { get; }

        public Task Reading { get; }

        public Task Running { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        public OnboardInputs Inputs { get; } = new();

        public TerminalConnection Connection { get; }

        public void Stop() => stop.Cancel();

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await Running;
            await Reading;
            Connection.Dispose();
            Io.Dispose();
            TestKeys.Delete(Key);
            loggers.Dispose();
            stop.Dispose();
        }
    }

    /// <summary>
    /// Real timers, but readings that never move, so no stream ever counts as
    /// settled; the next reading first runs <see cref="OnNextReading"/>.
    /// </summary>
    private sealed class StillClock : TimeProvider
    {
        private readonly long reading = System.GetTimestamp();
        private Action? onNextReading;

        public Action? OnNextReading
        {
            get => Volatile.Read(ref onNextReading);
            set => Volatile.Write(ref onNextReading, value);
        }

        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref onNextReading, null)?.Invoke();
            return reading;
        }
    }
}
