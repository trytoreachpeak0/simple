using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

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

    private static async Task Until(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not so within {seconds} s.");
            await Task.Delay(50, Cancel);
        }
    }

    /// <summary>A running terminal connection with its log kept in memory.</summary>
    private sealed class Terminal : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly ILoggerFactory loggers;
        private readonly Task running;

        public Terminal(string url, string pin)
        {
            loggers = LoggerFactory.Create(logging => logging.AddProvider(new ListLogger(Log)));
            Connection = new TerminalConnection(new TerminalSettings("vehicle-7", url, pin), Inputs, TimeProvider.System, loggers);
            running = Connection.RunAsync(stop.Token);
        }

        public ConcurrentQueue<string> Log { get; } = new();

        public OnboardInputs Inputs { get; } = new();

        public TerminalConnection Connection { get; }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await running;
            Connection.Dispose();
            loggers.Dispose();
            stop.Dispose();
        }
    }

    private sealed class ListLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
