using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Simple.Server.Events;
using Simple.Server.Mes;
using Simple.Server.Status;

namespace Simple.Server.Tests;

/// <summary>
/// The server polling a stub MES gateway (100 ms apart here): what the status
/// API shows and which changes reach the event record.
/// </summary>
public sealed class MesGatewayTests : IAsyncLifetime
{
    private static readonly MesRow Row = new("SAMPLE_TO_OVEN", "SMP0001.01", "S1-1", "SMP-DA-01", "SMP-CURE", "2026-01-05T08:00:00", "SMP-PKG-A");

    private readonly ServerInstance server = new();
    private HttpClient client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        client = server.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task StatusShowsTheGatewaysRowsWithinAPoll()
    {
        server.MesGateway.Answer(Row);

        var mes = await StatusUntilAsync(m => m.Rows?.Contains(Row) == true);

        Assert.Equal(new Uri("http://127.0.0.1:9"), mes.Url);
        Assert.True(mes.Reachable);
        Assert.True(mes.Available);
        Assert.Null(mes.GatewayError);
        Assert.Null(mes.UnavailableSince);
        Assert.Equal(1, mes.RowCount);
        Assert.True(mes.LastRound!.Succeeded);
        Assert.NotNull(mes.LastRound.ReadAt);
        Assert.InRange(mes.CheckedAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

        server.MesGateway.Answer();
        Assert.Empty((await StatusUntilAsync(m => m.RowCount == 0)).Rows!);
    }

    [Fact]
    public async Task AFailedReadIsRecordedOnceAndItsRecoveryOnce()
    {
        await StatusUntilAsync(m => m.Available == true);

        server.MesGateway.FailRounds("read", "ORA-03113: end-of-file on communication channel");
        var failing = await StatusUntilAsync(m => m.Available == false);
        // A few more failed rounds: still one event.
        await FetchesAsync(5);
        server.MesGateway.Answer(Row);
        var recovered = await StatusUntilAsync(m => m.Available == true);

        Assert.True(failing.Reachable);
        Assert.False(failing.LastRound!.Succeeded);
        Assert.Equal(new MesReadFailure("read", "ORA-03113: end-of-file on communication channel"), failing.LastRound.Failure);
        Assert.Null(failing.RowCount);
        Assert.Null(failing.Rows);
        Assert.NotNull(failing.UnavailableSince);
        Assert.Null(recovered.UnavailableSince);

        var events = await MesEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        var down = events[0].Data!.Value;
        Assert.Equal(MesGatewayMonitor.CauseRead, down.GetProperty("cause").GetString());
        Assert.Equal("ORA-03113: end-of-file on communication channel", down.GetProperty("error").GetString());
        var up = events[1].Data!.Value;
        Assert.Equal(1, up.GetProperty("rowCount").GetInt32());
        Assert.Equal(failing.UnavailableSince, up.GetProperty("unavailableSince").GetDateTimeOffset());
        Assert.All(events, e => Assert.Equal(EventSources.Server, e.Source));
    }

    [Fact]
    public async Task AGatewayThatDoesNotAnswerIsAGatewayCauseAndKeepsTheLastRound()
    {
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Rows?.Contains(Row) == true);

        server.MesGateway.BeUnreachable("No connection could be made because the target machine actively refused it.");
        var gone = await StatusUntilAsync(m => m.Reachable == false);
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Available == true);

        Assert.False(gone.Available);
        Assert.Equal("No connection could be made because the target machine actively refused it.", gone.GatewayError);
        // What the gateway last said stays visible, with its time.
        Assert.True(gone.LastRound!.Succeeded);
        Assert.NotNull(gone.LastRound.ReadAt);
        Assert.Equal([Row], gone.Rows);

        var events = await MesEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        Assert.Equal(MesGatewayMonitor.CauseGateway, events[0].Data!.Value.GetProperty("cause").GetString());
    }

    [Fact]
    public async Task ACauseThatChangesWhileUnavailableIsRecordedAndRecoversOnce()
    {
        await StatusUntilAsync(m => m.Available == true);

        server.MesGateway.FailRounds("format", "Row 4 has 6 values for 7 columns.");
        await StatusUntilAsync(m => m.Available == false);
        server.MesGateway.BeUnreachable();
        await StatusUntilAsync(m => m.Reachable == false);
        // Back, but not read yet (a restarted gateway): still unavailable, nothing recorded.
        server.MesGateway.AnswerNotReadYet();
        var notRead = await StatusUntilAsync(m => m.Reachable == true);
        await FetchesAsync(3);
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Available == true);

        Assert.False(notRead.Available);
        var events = await MesEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        Assert.Equal(
            [MesGatewayMonitor.CauseRead, MesGatewayMonitor.CauseGateway],
            events.Take(2).Select(e => e.Data!.Value.GetProperty("cause").GetString()));
    }

    [Fact]
    public async Task ARoundThatStopsMovingMakesTheMesUnavailableWhileTheGatewayAnswers()
    {
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Rows?.Contains(Row) == true);

        server.MesGateway.Freeze();
        var stale = await StatusUntilAsync(m => m.Available == false);
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Available == true);

        Assert.True(stale.Reachable);
        Assert.True(stale.LastRound!.Succeeded);
        // StaleAfter is 2 s in these tests.
        Assert.True(stale.CheckedAt - stale.NewRoundSeenAt > TimeSpan.FromSeconds(2));
        var events = await MesEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        var down = events[0].Data!.Value;
        Assert.Equal(MesGatewayMonitor.CauseRead, down.GetProperty("cause").GetString());
        Assert.StartsWith("No new round for more than 00:00:02", down.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyCutOffMidwayIsAGatewayCauseAndThePollerGoesOn()
    {
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Available == true);

        server.MesGateway.CutOffMidBody();
        var cut = await StatusUntilAsync(m => m.Reachable == false);
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.Available == true);

        Assert.Contains("The response ended prematurely. (stub)", cut.GatewayError, StringComparison.Ordinal);
        var events = await MesEventsAsync();
        Assert.Equal([EventTypes.MesUnreachable, EventTypes.MesRecovered], events.Select(e => e.Type));
        Assert.Equal(MesGatewayMonitor.CauseGateway, events[0].Data!.Value.GetProperty("cause").GetString());
    }

    [Fact]
    public async Task AGatewayRestartWhoseRoundsStartOverIsNotAStoppedRound()
    {
        // A gateway that has been up a while: far ahead of where a restarted one starts.
        server.MesGateway.ContinueFrom(100_000);
        server.MesGateway.Answer(Row);
        await StatusUntilAsync(m => m.LastRound?.Round >= 100_000);

        server.MesGateway.RestartCounter();
        await StatusUntilAsync(m => m.LastRound?.Round < 100_000);

        // Longer than StaleAfter (2 s here): the rounds still count as moving.
        await StaysAsync(TimeSpan.FromSeconds(3), m => m.Available == true);
        Assert.Empty(await MesEventsAsync());
    }

    [Fact]
    public async Task AGatewayUnreachableFromTheStartIsRecordedAndARoundZeroIsNotData()
    {
        await server.StopAsync();
        server.MesGateway.BeUnreachable();
        client = server.Start();

        var down = await StatusUntilAsync(m => m.Reachable == false);
        server.MesGateway.AnswerNotReadYet();
        var notRead = await StatusUntilAsync(m => m.Reachable == true);

        Assert.False(down.Available);
        Assert.Null(notRead.LastRound);
        Assert.False(notRead.Available);
        Assert.Equal([EventTypes.MesUnreachable], (await MesEventsAsync()).Select(e => e.Type));
    }

    [Fact]
    public async Task WhileTheGatewayDoesNotAnswerTheServerBacksOff()
    {
        await StatusUntilAsync(m => m.Available == true);
        server.MesGateway.BeUnreachable();
        await StatusUntilAsync(m => m.Reachable == false);

        var from = server.MesGateway.Fetches;
        await Task.Delay(TimeSpan.FromSeconds(3), Cancel);
        var fetches = server.MesGateway.Fetches - from;

        // Polling every 100 ms would be about 30 fetches; backing off to the
        // 300 ms the test sets as the longest wait gives about 10 or more.
        Assert.InRange(fetches, 3, 20);
    }

    [Fact]
    public void TheServersDefaultIsTheGatewaysLoopbackAddressAndTheAdr0008Cap()
    {
        var settings = server.Services.GetRequiredService<IOptions<MesGatewaySettings>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), new MesGatewaySettings().RetryMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), new MesGatewaySettings().PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), new MesGatewaySettings().StaleAfter);
        Assert.Equal(new Uri("http://127.0.0.1:9"), settings.Url);
    }

    [Theory]
    [InlineData(null, "Simple:MesGateway:Url '' is not an http or https address")]
    [InlineData("ftp://127.0.0.1:5190", "Simple:MesGateway:Url 'ftp://127.0.0.1:5190' is not an http or https address")]
    public void AnInvalidGatewayAddressIsNamed(string? url, string message)
    {
        var result = new MesGatewaySettingsValidator().Validate(null, new MesGatewaySettings { Url = url is null ? null : new Uri(url) });

        Assert.Contains(result.Failures!, f => f.StartsWith(message, StringComparison.Ordinal));
    }

    private async Task<MesGatewayStatus> StatusUntilAsync(Func<MesGatewayStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
            if (condition(status!.MesGateway))
            {
                return status.MesGateway;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The MES status did not get there within 10 s; it is {status.MesGateway}.");
            await Task.Delay(20, Cancel);
        }
    }

    /// <summary>Samples the status for the whole of <paramref name="period"/>; the condition must hold every time.</summary>
    private async Task StaysAsync(TimeSpan period, Func<MesGatewayStatus, bool> condition)
    {
        var until = DateTime.UtcNow + period;
        while (DateTime.UtcNow < until)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
            Assert.True(condition(status!.MesGateway), $"The MES status did not stay as expected; it became {status.MesGateway}.");
            await Task.Delay(50, Cancel);
        }
    }

    private async Task FetchesAsync(int count)
    {
        var until = server.MesGateway.Fetches + count;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.MesGateway.Fetches < until)
        {
            Assert.True(DateTime.UtcNow < deadline, "The server stopped fetching.");
            await Task.Delay(20, Cancel);
        }
    }

    private async Task<List<EventView>> MesEventsAsync()
    {
        var page = await client.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        return page!.Items.Where(e => e.Type.StartsWith("mes.", StringComparison.Ordinal)).ToList();
    }
}
