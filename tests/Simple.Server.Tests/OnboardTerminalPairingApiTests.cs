using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Simple.Protocol;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Tests;

/// <summary>
/// Pairing over real TLS streams and the API (ADR 0007, 0025, 0029): mutual
/// TLS, the pending list and its pairing code, confirming and revoking, and
/// what the event record and the database keep.
/// </summary>
public sealed class OnboardTerminalPairingApiTests
{
    private const string Password = "password-1234";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStreamWithoutAClientCertificateDoesNotGetThroughTheHandshake()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var api = server.Start();
        using var channel = TestTerminal.Channel(await TestTerminal.EndpointAsync(api), certificate: null);
        var headers = new Metadata { { ProtocolVersion.MajorHeader, "1" } };
        using var call = new OnboardLink.OnboardLinkClient(channel).Connect(headers, cancellationToken: Cancel);

        var refusal = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(Cancel));

        Assert.Equal(StatusCode.Unavailable, refusal.StatusCode);
        Assert.Empty((await Api.StatusAsync(api)).PendingOnboardTerminals);
    }

    [Fact]
    public async Task AnUnknownTerminalWaitsWithACodeAnEngineerConfirmsAndTheKeyIsKnownAfterARestart()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var admin = server.Start();
        await CreateAccountAsync(admin, "p1-engineer", "engineer");
        await CreateAccountAsync(admin, "p1-team-leader", "team-leader");
        using var key = new TestTerminalKey();
        string code;
        await using (var terminal = await TestTerminal.ConnectAsync(admin, key: key))
        {
            await terminal.ReceiveAsync();
            await terminal.ReportAsync(inputs: TestTerminal.Scan(1, "LOT-0001"));

            // Waiting: listed with its code, the same code on its screen, its reports unused.
            code = await Api.PairingCodeAsync(admin, key.Pin);
            var screen = await terminal.ReceiveAsync();
            Assert.Equal(OnboardTerminalRegistry.PairingScreenText("vehicle-1", code), screen.Screen.Text);
            var pending = Assert.Single((await Api.StatusAsync(admin)).PendingOnboardTerminals);
            Assert.True(pending.Connected);
            Assert.True(pending.InVehicleList);
            Assert.Empty((await Api.StatusAsync(admin)).OnboardTerminals);
            // Recorded without the code, which is treated as a credential (ADR 0034).
            var waiting = Assert.Single(await Api.UntilAsync(() => Api.EventsAsync(admin, EventTypes.OnboardPending), e => e.Count > 0));
            Assert.Equal(("vehicle-1", key.Pin), (waiting.Text("vehicleId"), waiting.Text("keyPin")));
            Assert.DoesNotContain(code, waiting.Data!.Value.GetRawText(), StringComparison.Ordinal);
            Assert.Empty(await Api.EventsAsync(admin, EventTypes.OnboardInputReceived));

            // A team leader may not confirm; an engineer may, with the right code only.
            using var teamLeader = (await server.LogInAsync("p1-team-leader", Password)).Client!;
            using var engineer = (await server.LogInAsync("p1-engineer", Password)).Client!;
            using (var refused = await Api.ConfirmAsync(teamLeader, "vehicle-1", code))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            using (var wrong = await Api.ConfirmAsync(engineer, "vehicle-1", code == "123456" ? "654321" : "123456"))
            {
                Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            }

            using (var malformed = await Api.ConfirmAsync(engineer, "vehicle-1", "12345"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            using (var confirmed = await Api.ConfirmAsync(engineer, "vehicle-1", code))
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
                var registered = await confirmed.Content.ReadFromJsonAsync<Status.OnboardTerminalKeyStatus>(Cancel);
                Assert.Equal(("vehicle-1", key.Pin, "p1-engineer"), (registered!.VehicleId, registered.KeyPin, registered.ConfirmedBy));
            }

            var event_ = Assert.Single(await Api.EventsAsync(admin, EventTypes.OnboardConfirmed));
            Assert.Equal(("p1-engineer", "vehicle-1", key.Pin), (event_.Account, event_.Text("vehicleId"), event_.Text("keyPin")));
            Assert.Equal(EventSources.Console, event_.Source);
            Assert.DoesNotContain(code, event_.Data!.Value.GetRawText(), StringComparison.Ordinal);

            // Used at once, on the same stream.
            while ((await terminal.ReceiveAsync()).Screen.Text != OnboardTerminalRegistry.ConnectedScreenText)
            {
                // Until the state after the confirmation arrives.
            }

            await terminal.ReportAsync();
            var status = await Api.UntilAsync(() => Api.StatusAsync(admin), s => s.OnboardTerminals.Any(t => t.FreshReportAgeSeconds is not null));
            Assert.Equal(key.Pin, Assert.Single(status.OnboardTerminals).KeyPin);
            Assert.Empty(status.PendingOnboardTerminals);
            var list = await admin.GetFromJsonAsync<OnboardTerminalKeysResponse>(new Uri("/api/v1/onboard-terminals", UriKind.Relative), Cancel);
            Assert.Equal(key.Pin, Assert.Single(list!.Registered).KeyPin);
        }

        await server.StopAsync();
        using var again = server.Start();
        await using var reconnected = await TestTerminal.ConnectAsync(again, key: key);
        await reconnected.ReceiveAsync();
        await reconnected.ReportAsync();
        var state = await reconnected.ReceiveAsync();
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, state.Screen.Text);
        Assert.Empty((await Api.StatusAsync(again)).PendingOnboardTerminals);
    }

    [Fact]
    public async Task RevokingAKeyPutsItsTerminalBackOnThePendingListAtOnce()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var admin = server.Start();
        await using var terminal = await TestTerminal.ConnectConfirmedAsync(admin);
        await terminal.ReportAsync();

        using (var revoked = await admin.PostAsync(new Uri("/api/v1/onboard-terminals/vehicle-1/revoke", UriKind.Relative), null, Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        // The states sent before the confirmation are still on the way, the
        // new code's comes after them; it times out if it never comes.
        var code = await Api.PairingCodeAsync(admin, terminal.Key.Pin);
        while ((await terminal.ReceiveAsync()).Screen.Text != OnboardTerminalRegistry.PairingScreenText("vehicle-1", code))
        {
            // Until the screen shows the new code.
        }

        var event_ = Assert.Single(await Api.EventsAsync(admin, EventTypes.OnboardRevoked));
        Assert.Equal(("admin", terminal.Key.Pin), (event_.Account, event_.Text("keyPin")));
        Assert.Contains(await Api.EventsAsync(admin, EventTypes.OnboardDisconnected), e => e.Text("reason") == "its key was revoked");
        await using (var db = server.CreateDbContext())
        {
            Assert.Empty(db.OnboardTerminalKeys);
        }

        using var again = await admin.PostAsync(new Uri("/api/v1/onboard-terminals/vehicle-1/revoke", UriKind.Relative), null, Cancel);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task TwoComputersWithOneKeySendingFreshReportsAlternatelyAreBothRefusedAndRecorded()
    {
        await using var server = new ServerInstance(kestrel: true);
        using var admin = server.Start();
        using var key = new TestTerminalKey();
        await using var original = await TestTerminal.ConnectConfirmedAsync(admin, key: key);
        await original.ReportAsync();

        // The clone's stream replaces the original's…
        await using var clone = await TestTerminal.ConnectAsync(admin, key: key);
        await clone.ReceiveAsync();
        await clone.ReportAsync();
        await clone.ReceiveAsync();
        await clone.ReportAsync();
        await original.CloseAsync();

        // …and the original, still running, comes back.
        await using var back = await TestTerminal.ConnectAsync(admin, key: key);
        back.RunId = original.RunId;
        await back.ReceiveAsync();
        await back.ReportAsync();
        await back.ReceiveAsync();
        await back.ReportAsync();

        var suspected = Assert.Single(await Api.UntilAsync(() => Api.EventsAsync(admin, EventTypes.OnboardCloneSuspected), e => e.Count > 0));
        Assert.Equal(("vehicle-1", key.Pin), (suspected.Text("vehicleId"), suspected.Text("keyPin")));
        var status = await Api.StatusAsync(admin);
        Assert.True(Assert.Single(status.PendingOnboardTerminals).CloneSuspected);
        Assert.False(Assert.Single(status.OnboardTerminals).Connected);
        await using var db = server.CreateDbContext();
        Assert.Empty(db.OnboardTerminalKeys);
    }

    [Fact]
    public async Task AVehicleNotInTheVehicleListIsListedButCannotBeConfirmedAListedOneCan()
    {
        await using var server = new ServerInstance(
            kestrel: true,
            services: s => s.Replace(ServiceDescriptor.Singleton<IVehicleList>(new OnlyVehicle("vehicle-1"))));
        using var admin = server.Start();
        await using var terminal = await TestTerminal.ConnectAsync(admin);
        await terminal.ReceiveAsync();
        await terminal.ReportAsync("vehicle-9");

        var code = await Api.PairingCodeAsync(admin, terminal.Key.Pin, "vehicle-9");
        Assert.False(Assert.Single((await Api.StatusAsync(admin)).PendingOnboardTerminals).InVehicleList);
        using (var refused = await Api.ConfirmAsync(admin, "vehicle-9", code))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        Assert.Empty(await Api.EventsAsync(admin, EventTypes.OnboardConfirmed));
        Assert.Single((await Api.StatusAsync(admin)).PendingOnboardTerminals);

        // A terminal claiming a listed vehicle is confirmed as usual.
        await using var listed = await TestTerminal.ConnectConfirmedAsync(admin, "vehicle-1");
        Assert.Equal("vehicle-1", Assert.Single(await Api.EventsAsync(admin, EventTypes.OnboardConfirmed)).Text("vehicleId"));
        Assert.Equal(terminal.Key.Pin, Assert.Single((await Api.StatusAsync(admin)).PendingOnboardTerminals).KeyPin);
    }

    private static async Task CreateAccountAsync(HttpClient admin, string name, string role)
    {
        using var created = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name, role, project = "P1", password = Password }, Cancel);
        created.EnsureSuccessStatusCode();
    }

    private sealed class OnlyVehicle(string listed) : IVehicleList
    {
        public bool Contains(string vehicleId) => vehicleId == listed;
    }
}
