using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Simple.Protocol;
using Simple.Server.Events;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Tests;

/// <summary>
/// The server's end of the terminal protocol (ADR 0024-0026), over real TLS
/// streams, with the server's clock in the test's hand.
/// </summary>
public sealed class OnboardTerminalTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AConnectedTerminalShowsInStatusWithItsVersionFreshnessAndScreen()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(kestrel: true, time: time);
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);

        var first = await terminal.ReceiveAsync();
        Assert.Equal(1UL, first.Seq);
        Assert.Equal(0UL, first.LastReceivedSeq);
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, first.Screen.Text);
        await terminal.ReportAsync();

        var status = await Api.UntilAsync(() => Api.StatusAsync(api), s => s.OnboardTerminals.Any(t => t.FreshReportAgeSeconds is not null));
        var shown = Assert.Single(status.OnboardTerminals);
        Assert.Equal("vehicle-1", shown.VehicleId);
        Assert.Equal("test-terminal-1", shown.TerminalVersion);
        Assert.True(shown.Connected);
        Assert.False(shown.Stale);
        Assert.Equal(0, shown.FreshReportAgeSeconds);
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, shown.ScreenText);

        // The age is the server's own clock since the state that report named was sent.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, Assert.Single((await Api.StatusAsync(api)).OnboardTerminals).FreshReportAgeSeconds);

        var connected = Assert.Single(await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardConnected), e => e.Count > 0));
        Assert.Equal("vehicle-1", connected.Text("vehicleId"));
        Assert.Equal("test-terminal-1", connected.Text("terminalVersion"));
    }

    [Fact]
    public async Task ReportsGoStaleTenSecondsAfterTheFreshestWasWrittenAndFreshAgainWithTheNextFreshOne()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(kestrel: true, time: time);
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);
        await terminal.ReceiveAsync();
        await terminal.ReportAsync();
        await Api.UntilAsync(() => Api.StatusAsync(api), s => s.OnboardTerminals.Any(t => t.FreshReportAgeSeconds is not null));

        // The stream stays open, but no report gets through.
        time.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(300, TestTerminal.Cancel);
        Assert.Empty(await Api.EventsAsync(api, EventTypes.OnboardStale));
        Assert.False(Assert.Single((await Api.StatusAsync(api)).OnboardTerminals).Stale);

        time.Advance(TimeSpan.FromSeconds(0.2));
        var stale = await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardStale), e => e.Count > 0);
        Assert.Equal("vehicle-1", Assert.Single(stale).Text("vehicleId"));
        Assert.InRange(stale[0].OccurredAt, Start.AddSeconds(10), Start.AddMilliseconds(10_100));
        Assert.True(Assert.Single((await Api.StatusAsync(api)).OnboardTerminals).Stale);

        // A report naming a state sent just now is fresh again.
        var latest = await terminal.ReceiveAsync();
        while (latest.Seq < 2)
        {
            latest = await terminal.ReceiveAsync();
        }

        await terminal.ReportAsync();
        var fresh = await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardFresh), e => e.Count > 0);
        Assert.Single(fresh);
        Assert.False(Assert.Single((await Api.StatusAsync(api)).OnboardTerminals).Stale);
    }

    [Fact]
    public async Task AReportNamingNoStateOrOneTooOldIsNotFresh()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(kestrel: true, time: time);
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);
        await terminal.ReceiveAsync();

        // Written before any state arrived: how old it is cannot be told.
        await terminal.ReportAsync(acknowledging: 0);
        var status = await Api.UntilAsync(() => Api.StatusAsync(api), s => s.OnboardTerminals.Count > 0);
        Assert.Null(Assert.Single(status.OnboardTerminals).FreshReportAgeSeconds);

        // Naming state 1, sent eleven seconds ago: at most that old, too old.
        time.Advance(TimeSpan.FromSeconds(11));
        await terminal.ReportAsync(acknowledging: 1);
        await Task.Delay(300, TestTerminal.Cancel);
        Assert.Null(Assert.Single((await Api.StatusAsync(api)).OnboardTerminals).FreshReportAgeSeconds);

        // Never fresh since its first report ten seconds ago: stale.
        Assert.Single(await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardStale), e => e.Count > 0));
    }

    [Fact]
    public async Task AnInputIsRecordedOnceHoweverManyReportsCarryIt()
    {
        await using var server = new ServerInstance(kestrel: true, time: new FakeTimeProvider(Start));
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);
        await terminal.ReceiveAsync();

        await terminal.ReportAsync(inputs: TestTerminal.Scan(1, "LOT-0001"));
        await terminal.ReportAsync(inputs: TestTerminal.Scan(1, "LOT-0001"));
        await terminal.ReportAsync(inputs: [TestTerminal.Scan(1, "LOT-0001"), TestTerminal.Scan(2, "LOT-0002")]);

        var inputs = await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardInputReceived), e => e.Count >= 2);
        Assert.Equal(["LOT-0001", "LOT-0002"], inputs.Select(e => e.Text("text")));
        Assert.All(inputs, e => Assert.Equal(EventSources.OnboardTerminal, e.Source));
        Assert.All(inputs, e => Assert.Equal("scan", e.Text("kind")));
        await Task.Delay(300, TestTerminal.Cancel);
        Assert.Equal(2, (await Api.EventsAsync(api, EventTypes.OnboardInputReceived)).Count);
    }

    [Fact]
    public async Task ATerminalThatEndsItsStreamIsRecordedAsDisconnectedAndStaysListed()
    {
        await using var server = new ServerInstance(kestrel: true, time: new FakeTimeProvider(Start));
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);
        await terminal.ReceiveAsync();
        await terminal.ReportAsync();
        await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardConnected), e => e.Count > 0);

        await terminal.CloseAsync();

        var disconnected = await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardDisconnected), e => e.Count > 0);
        Assert.Equal("the terminal ended the stream", Assert.Single(disconnected).Text("reason"));
        var shown = Assert.Single((await Api.StatusAsync(api)).OnboardTerminals);
        Assert.False(shown.Connected);
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, shown.ScreenText);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData(null)]
    public async Task AMajorTheServerDoesNotServeIsRefusedWithTheMajorsItServes(string? major)
    {
        await using var server = new ServerInstance(kestrel: true);
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api, major);

        var refusal = await terminal.FailureAsync();

        Assert.Equal(StatusCode.FailedPrecondition, refusal.StatusCode);
        Assert.Equal("1", refusal.Trailers.GetValue(ProtocolVersion.SupportedMajorsTrailer));
        Assert.Single(await Api.UntilAsync(() => Api.EventsAsync(api, EventTypes.OnboardRefused), e => e.Count > 0));
        Assert.Empty((await Api.StatusAsync(api)).OnboardTerminals);
    }

    [Fact]
    public async Task AReportWithoutAVehicleIsRefused()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var api = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(api);
        await terminal.ReceiveAsync();

        await terminal.ReportAsync(vehicle: "");

        Assert.Equal(StatusCode.InvalidArgument, (await terminal.FailureAsync()).StatusCode);
        Assert.Empty((await Api.StatusAsync(api)).OnboardTerminals);
    }

    [Fact]
    public async Task EachListenerServesOnlyItsOwnSide()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var api = server.Start();
        var endpoint = await TestTerminal.EndpointAsync(api);

        // The API is not found on the terminal endpoint, which other machines may reach…
        using var overTls = new HttpClient(TestTerminal.Handler(endpoint));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(endpoint.Url), "/api/v1/status")) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var onTerminalEndpoint = await overTls.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, onTerminalEndpoint.StatusCode);

        // …and the terminal protocol is not found on the API's plain HTTP.
        using var grpc = new HttpRequestMessage(HttpMethod.Post, new Uri("/simple.protocol.v1.OnboardLink/Connect", UriKind.Relative))
        {
            Content = new ByteArrayContent([]) { Headers = { { "Content-Type", "application/grpc" } } },
        };
        using var onApi = await api.SendAsync(grpc, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, onApi.StatusCode);
    }

    [Fact]
    public async Task TheCertificateKeepsItsPinFromOneStartToTheNextAndIsBoundToTheNamedKey()
    {
        await using var server = new ServerInstance(kestrel: true);
        string pin;
        IReadOnlyList<EventView> created;
        using (var api = server.Start())
        {
            pin = (await TestTerminal.EndpointAsync(api)).CertificatePin;
            created = await Api.EventsAsync(api, EventTypes.OnboardServerKeyCreated);
            var certificate = server.Services.GetRequiredService<OnboardEndpoint>().Certificate;
            using var key = certificate.GetECDsaPrivateKey();
            // Bound by name to the persisted key: nothing was imported, so
            // there is no temporary key container to leave behind.
            Assert.Equal(server.Database.ServerKeyName, Assert.IsType<ECDsaCng>(key).Key.KeyName);
        }

        // A new key is said so, with its pin, in the event record; a key found again is not.
        Assert.Equal(pin, Assert.Single(created).Text("pin"));
        await server.StopAsync();

        using var again = server.Start();
        Assert.Equal(pin, (await TestTerminal.EndpointAsync(again)).CertificatePin);
        Assert.Single(await Api.EventsAsync(again, EventTypes.OnboardServerKeyCreated));
    }
}
