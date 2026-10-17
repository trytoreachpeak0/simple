using System.Net.Http.Json;
using Simple.Server.Riot;
using Simple.Server.Status;

namespace Simple.Server.Tests;

/// <summary>
/// The server checking that it reaches the fleet manager (a stub RIoT,
/// checked 100 ms apart here) and showing it in the status API.
/// </summary>
public sealed class RiotConnectionTests : IAsyncLifetime
{
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
    public async Task StatusShowsRiotConnectedWithTheCallKeyAsBearer()
    {
        var riot = await StatusUntilAsync(r => r.State == RiotConnectionMonitor.Connected);

        Assert.Equal(new Uri("http://127.0.0.1:9"), riot.Url);
        Assert.Equal(2, riot.MapCount);
        Assert.Null(riot.Error);
        Assert.NotNull(riot.ConnectedAt);
        Assert.Equal($"Bearer {StubRiot.CallApiKey}", server.Riot.LastAuthorization);
    }

    /// <summary>A refused connection, a body cut off midway and a business failure all read as unreachable, and the server keeps trying.</summary>
    [Theory]
    [InlineData("refused")]
    [InlineData("cut-off")]
    [InlineData("00002")]
    public async Task ARiotThatDoesNotAnswerIsUnreachableAndTheServerKeepsTrying(string failure)
    {
        var connected = await StatusUntilAsync(r => r.State == RiotConnectionMonitor.Connected);
        Action fail = failure switch
        {
            "refused" => () => server.Riot.BeUnreachable(),
            "cut-off" => server.Riot.CutOffMidBody,
            _ => server.Riot.FailWithInternalError,
        };

        fail();
        var unreachable = await StatusUntilAsync(r => r.State == RiotConnectionMonitor.Unreachable);
        var calls = server.Riot.Calls;
        await StatusUntilAsync(_ => server.Riot.Calls > calls + 1);
        server.Riot.Answer();
        var back = await StatusUntilAsync(r => r.State == RiotConnectionMonitor.Connected);

        Assert.False(string.IsNullOrEmpty(unreachable.Error));
        Assert.Equal(connected.ConnectedAt, unreachable.ConnectedAt);
        Assert.Null(back.Error);
        Assert.True(back.ConnectedAt > connected.ConnectedAt);
    }

    private async Task<RiotStatus> StatusUntilAsync(Func<RiotStatus, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
            if (condition(status!.Riot))
            {
                return status.Riot;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Not seen within 15 s; the last status was {status.Riot}.");
            await Task.Delay(50, Cancel);
        }
    }
}

/// <summary>Without <c>Simple:Riot:Url</c> the server runs without a fleet manager and says so.</summary>
public sealed class RiotNotConfiguredTests : IAsyncLifetime
{
    private readonly ServerInstance server = new(riot: false);
    private HttpClient client = null!;

    public ValueTask InitializeAsync()
    {
        client = server.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task StatusSaysNotConfigured()
    {
        var status = await client.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(RiotConnectionMonitor.NotConfigured, status!.Riot.State);
        Assert.Null(status.Riot.Url);
        Assert.Equal(0, server.Riot.Calls);
    }
}
