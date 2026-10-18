using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Tests;

/// <summary>
/// Which terminal's reports the server uses (ADR 0007, 0025): the registry on
/// its own, its registrations in memory, the clock in the test's hand.
/// </summary>
public sealed class OnboardTerminalPairingTests
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly Actor Engineer = new("engineer-p1", EventSources.Console, "127.0.0.1");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void AnUnknownKeyWaitsForConfirmationWhicheverVehicleItClaimsAndItsReportsAreNotUsed()
    {
        using var setup = new Setup();
        var link = setup.Connect(KeyA, "vehicle-1", run: "run-a");

        Assert.Empty(setup.Registry.Snapshot());
        var pending = Assert.Single(setup.Registry.Pending());
        Assert.Equal(KeyA, pending.KeyPin);
        Assert.Equal("vehicle-1", pending.VehicleId);
        Assert.Matches("^[0-9]{6}$", pending.PairingCode);
        Assert.True(pending.Connected);
        Assert.True(pending.InVehicleList);
        Assert.False(pending.VehicleOnlineWithAnotherKey);
        Assert.False(pending.CloneSuspected);

        // The screen shows the same code, and the claimed vehicle with it.
        var screen = setup.Registry.NextDesiredState(link).Screen.Text;
        Assert.Contains(pending.PairingCode, screen, StringComparison.Ordinal);
        Assert.Contains("vehicle-1", screen, StringComparison.Ordinal);
        Assert.NotEqual(OnboardTerminalRegistry.ConnectedScreenText, screen);

        // Fresh reports with IO and inputs change nothing: no vehicle's terminal appears.
        setup.Report(link, "run-a", withInput: true);
        Assert.Empty(setup.Registry.Snapshot());
        Assert.Equal(pending.PairingCode, Assert.Single(setup.Registry.Pending()).PairingCode);
    }

    [Fact]
    public async Task OnceConfirmedForTheVehicleItClaimsItsReportsAreUsedAtOnceOnTheSameStream()
    {
        using var setup = new Setup();
        var link = setup.Connect(KeyA, "vehicle-1", run: "run-a");
        var code = Assert.Single(setup.Registry.Pending()).PairingCode;

        var outcome = await setup.Registry.ConfirmAsync("vehicle-1", code, Engineer, Cancel);

        Assert.Null(outcome.Problem);
        Assert.Empty(setup.Registry.Pending());
        var registered = Assert.Single(setup.Keys.Rows.Values);
        Assert.Equal(("vehicle-1", KeyA, "engineer-p1"), (registered.VehicleId, registered.KeyPin, registered.ConfirmedBy));
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, setup.Registry.NextDesiredState(link).Screen.Text);
        setup.Report(link, "run-a");
        var terminal = Assert.Single(setup.Registry.Snapshot());
        Assert.Equal(("vehicle-1", KeyA), (terminal.VehicleId, terminal.KeyPin));
        Assert.True(terminal.Connected);
        Assert.Equal(0, terminal.FreshReportAgeSeconds);
        Assert.Equal(KeyA, Assert.Single(setup.Registry.Registrations()).KeyPin);
    }

    [Fact]
    public async Task AWrongCodeOrAnotherVehicleConfirmsNothing()
    {
        using var setup = new Setup();
        setup.Connect(KeyA, "vehicle-1", run: "run-a");
        var code = Assert.Single(setup.Registry.Pending()).PairingCode;
        var wrong = code == "000000" ? "000001" : "000000";

        Assert.Equal(404, (await setup.Registry.ConfirmAsync("vehicle-1", wrong, Engineer, Cancel)).Status);
        Assert.Equal(404, (await setup.Registry.ConfirmAsync("vehicle-2", code, Engineer, Cancel)).Status);

        Assert.Empty(setup.Keys.Rows);
        Assert.Single(setup.Registry.Pending());
    }

    [Fact]
    public async Task AClaimedVehicleNotInTheVehicleListIsListedButCannotBeConfirmed()
    {
        using var setup = new Setup(vehicles: ["vehicle-1"]);
        setup.Connect(KeyA, "vehicle-9", run: "run-a");
        var pending = Assert.Single(setup.Registry.Pending());
        Assert.False(pending.InVehicleList);

        var outcome = await setup.Registry.ConfirmAsync("vehicle-9", pending.PairingCode, Engineer, Cancel);

        Assert.Equal(409, outcome.Status);
        Assert.Contains("vehicle list", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(setup.Keys.Rows);
    }

    [Fact]
    public async Task AnotherKeyClaimingAnOnlineVehicleWaitsMarkedAndTheRegisteredOneWorksOn()
    {
        using var setup = new Setup();
        var a = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");

        var b = setup.Connect(KeyB, "vehicle-1", run: "run-b");
        setup.Report(a, "run-a");

        var pending = Assert.Single(setup.Registry.Pending());
        Assert.Equal(KeyB, pending.KeyPin);
        Assert.True(pending.VehicleOnlineWithAnotherKey);
        var terminal = Assert.Single(setup.Registry.Snapshot());
        Assert.Equal(KeyA, terminal.KeyPin);
        Assert.True(terminal.Connected);
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, setup.Registry.NextDesiredState(a).Screen.Text);
        Assert.False(a.Stopped.IsCancellationRequested);
        Assert.False(b.Stopped.IsCancellationRequested);
    }

    [Fact]
    public async Task ConfirmingAnotherKeyForTheVehicleReleasesTheOldKey()
    {
        using var setup = new Setup();
        var a = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");
        var b = setup.Connect(KeyB, "vehicle-1", run: "run-b");

        var outcome = await setup.Registry.ConfirmAsync("vehicle-1", Assert.Single(setup.Registry.Pending()).PairingCode, Engineer, Cancel);

        Assert.Equal(KeyA, outcome.ReplacedKeyPin);
        Assert.Equal(KeyB, Assert.Single(setup.Keys.Rows.Values).KeyPin);
        setup.Report(b, "run-b");
        Assert.Equal(KeyB, Assert.Single(setup.Registry.Snapshot()).KeyPin);

        // The old key is unknown now: its stream stays, but it waits for confirmation.
        var old = Assert.Single(setup.Registry.Pending());
        Assert.Equal(KeyA, old.KeyPin);
        Assert.True(old.VehicleOnlineWithAnotherKey);
        Assert.Contains(old.PairingCode, setup.Registry.NextDesiredState(a).Screen.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingTakesEffectAtOnceAndTheKeyWaitsForConfirmationAgain()
    {
        using var setup = new Setup();
        var a = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");

        var outcome = await setup.Registry.RevokeAsync("vehicle-1", Engineer, Cancel);

        Assert.Null(outcome.Problem);
        Assert.Empty(setup.Keys.Rows);
        Assert.Empty(setup.Registry.Registrations());
        Assert.False(Assert.Single(setup.Registry.Snapshot()).Connected);
        Assert.Equal(KeyA, Assert.Single(setup.Registry.Pending()).KeyPin);
        Assert.NotEqual(OnboardTerminalRegistry.ConnectedScreenText, setup.Registry.NextDesiredState(a).Screen.Text);
        Assert.Equal(404, (await setup.Registry.RevokeAsync("vehicle-1", Engineer, Cancel)).Status);
    }

    [Fact]
    public async Task ANewStreamOfTheSameKeyAndRunReplacesTheOldOneAgainAndAgainWithoutSuspicion()
    {
        using var setup = new Setup();
        var previous = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");

        for (var i = 0; i < 10; i++)
        {
            var next = setup.Connect(KeyA, "vehicle-1", run: "run-a");
            Assert.True(previous.Stopped.IsCancellationRequested);
            setup.Report(next, "run-a");
            setup.Registry.Close(previous, previous.StopReason!);
            previous = next;
        }

        Assert.Single(setup.Keys.Rows);
        Assert.Empty(setup.Registry.Pending());
        Assert.True(Assert.Single(setup.Registry.Snapshot()).Connected);
    }

    [Fact]
    public async Task ARestartedTerminalProgramIsNotAClone()
    {
        using var setup = new Setup();
        var first = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-1");
        setup.Registry.Close(first, "the connection was lost");

        foreach (var run in new[] { "run-2", "run-3" })
        {
            var link = setup.Connect(KeyA, "vehicle-1", run);
            setup.Report(link, run);
            setup.Registry.Close(link, "the connection was lost");
        }

        Assert.Single(setup.Keys.Rows);
        Assert.Empty(setup.Registry.Pending());
    }

    [Fact]
    public async Task TwoRunsAlternatelySendingFreshReportsAreBothRefusedUntilConfirmedAgain()
    {
        using var setup = new Setup();
        var a = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");

        // The clone starts: its stream replaces the original's…
        var b = setup.Connect(KeyA, "vehicle-1", run: "run-b");
        setup.Report(b, "run-b");
        setup.Registry.Close(a, a.StopReason!);
        Assert.Single(setup.Keys.Rows);

        // …and the original, still running, comes back with a fresh report.
        var again = setup.Connect(KeyA, "vehicle-1", run: "run-a");
        setup.Report(again, "run-a");

        // In memory at once, in the store by the registry's one writer.
        Assert.Empty(setup.Registry.Registrations());
        await setup.Registry.FlushAsync(Cancel);
        Assert.Empty(setup.Keys.Rows);
        Assert.Empty(setup.Registry.Registrations());
        var pending = Assert.Single(setup.Registry.Pending());
        Assert.True(pending.CloneSuspected);
        Assert.False(Assert.Single(setup.Registry.Snapshot()).Connected);
        Assert.Contains(pending.PairingCode, setup.Registry.NextDesiredState(again).Screen.Text, StringComparison.Ordinal);

        // Confirmed again (the clone taken away), the key works and its runs start afresh.
        Assert.Null((await setup.Registry.ConfirmAsync("vehicle-1", pending.PairingCode, Engineer, Cancel)).Problem);
        setup.Report(again, "run-a");
        Assert.True(Assert.Single(setup.Registry.Snapshot()).Connected);
        Assert.Single(setup.Keys.Rows);
    }

    [Fact]
    public async Task ARegisteredKeyIsKnownFromTheStoreAfterARestart()
    {
        using var keys = new InMemoryTerminalKeys();
        keys.Add("vehicle-1", KeyA);
        using var setup = new Setup(keys: keys);
        await setup.Registry.LoadAsync(Cancel);

        var link = setup.Connect(KeyA, "vehicle-1", run: "run-a");

        Assert.Empty(setup.Registry.Pending());
        Assert.Equal(KeyA, Assert.Single(setup.Registry.Snapshot()).KeyPin);
        Assert.Equal(OnboardTerminalRegistry.ConnectedScreenText, setup.Registry.NextDesiredState(link).Screen.Text);
    }

    [Fact]
    public async Task AKeyRegisteredForOneVehicleClaimingAnotherWaitsForTheOther()
    {
        using var setup = new Setup();
        var first = await setup.ConnectConfirmedAsync(KeyA, "vehicle-1", "run-a");

        // Someone changed the terminal's configured vehicle.
        setup.Connect(KeyA, "vehicle-2", run: "run-a");

        Assert.True(first.Stopped.IsCancellationRequested);
        var pending = Assert.Single(setup.Registry.Pending());
        Assert.Equal(("vehicle-2", "vehicle-1"), (pending.VehicleId, pending.RegisteredForVehicle));
        Assert.Equal(404, (await setup.Registry.RevokeAsync("vehicle-2", Engineer, Cancel)).Status);

        // Confirmed for vehicle-2, it is vehicle-2's key and no longer vehicle-1's.
        Assert.Null((await setup.Registry.ConfirmAsync("vehicle-2", pending.PairingCode, Engineer, Cancel)).Problem);
        Assert.Equal("vehicle-2", Assert.Single(setup.Keys.Rows.Values).VehicleId);
    }

    [Fact]
    public async Task AConfirmationWhileTheServerStopsIsNotSavedAndSaysSo()
    {
        using var setup = new Setup();
        setup.Connect(KeyA, "vehicle-1", run: "run-a");
        var code = Assert.Single(setup.Registry.Pending()).PairingCode;
        setup.Registry.Dispose();

        var outcome = await setup.Registry.ConfirmAsync("vehicle-1", code, Engineer, Cancel).WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        Assert.Equal(503, outcome.Status);
        Assert.Empty(setup.Keys.Rows);
    }

    [Fact]
    public void PendingCodesDifferAndAKeyKeepsItsCodeWhenItReconnects()
    {
        using var setup = new Setup();
        var a = setup.Connect(KeyA, "vehicle-1", run: "run-a");
        setup.Connect(KeyB, "vehicle-1", run: "run-b");
        var codes = setup.Registry.Pending().ToDictionary(p => p.KeyPin, p => p.PairingCode);
        Assert.NotEqual(codes[KeyA], codes[KeyB]);

        setup.Registry.Close(a, "the connection was lost");
        Assert.False(setup.Registry.Pending().Single(p => p.KeyPin == KeyA).Connected);
        setup.Connect(KeyA, "vehicle-1", run: "run-a");

        Assert.Equal(codes[KeyA], setup.Registry.Pending().Single(p => p.KeyPin == KeyA).PairingCode);
    }

    /// <summary>A registry with the clock in the test's hand and its registrations in <see cref="Keys"/>; its events go nowhere.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly bool ownKeys;

        public Setup(IReadOnlyList<string>? vehicles = null, InMemoryTerminalKeys? keys = null)
        {
            ownKeys = keys is null;
            Keys = keys ?? new InMemoryTerminalKeys();
            services = new ServiceCollection().AddSingleton<IOnboardTerminalKeyStore>(Keys).BuildServiceProvider();
            Registry = new OnboardTerminalRegistry(
                Time,
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OnboardTerminalRegistry>.Instance,
                vehicles is null ? new AnyVehicle() : new ListedVehicles(vehicles));
        }

        public FakeTimeProvider Time { get; } = new(Start);

        public InMemoryTerminalKeys Keys { get; }

        public OnboardTerminalRegistry Registry { get; }

        /// <summary>Opens a stream with <paramref name="key"/> and sends the first report, as a terminal does before it has any state.</summary>
        public TerminalLink Connect(string key, string vehicle, string run)
        {
            var link = Registry.Open("test", key);
            Assert.Null(Registry.Receive(link, new OnboardReport { Seq = 1, VehicleId = vehicle, TerminalVersion = "t", RunId = run }));
            return link;
        }

        /// <summary>A desired state, and a report naming it: a fresh report.</summary>
        public void Report(TerminalLink link, string run, bool withInput = false)
        {
            var state = Registry.NextDesiredState(link);
            var report = new OnboardReport
            {
                Seq = state.LastReceivedSeq + 1,
                LastReceivedSeq = state.Seq,
                VehicleId = "ignored after the first report",
                TerminalVersion = "t",
                RunId = run,
                IoModuleReadable = true,
                IoReadAgeMs = 10,
            };
            report.IoPoints.Add(new IoPoint { Kind = IoPointKind.Input, Number = 1, Value = true });
            if (withInput)
            {
                report.Inputs.Add(new OnboardInput { Id = 1, Scan = new Scan { Text = "LOT-1" } });
            }

            Assert.Null(Registry.Receive(link, report));
        }

        public async Task<TerminalLink> ConnectConfirmedAsync(string key, string vehicle, string run)
        {
            var link = Connect(key, vehicle, run);
            var code = Registry.Pending().Single(p => p.KeyPin == key).PairingCode;
            Assert.Null((await Registry.ConfirmAsync(vehicle, code, Engineer, Cancel)).Problem);
            Report(link, run);
            return link;
        }

        public void Dispose()
        {
            Registry.Dispose();
            services.Dispose();
            if (ownKeys)
            {
                Keys.Dispose();
            }
        }
    }

    private sealed class ListedVehicles(IReadOnlyList<string> vehicles) : IVehicleList
    {
        public bool Contains(string vehicleId) => vehicles.Contains(vehicleId);
    }
}

/// <summary>Registrations kept in memory, in place of the database.</summary>
public sealed class InMemoryTerminalKeys : IOnboardTerminalKeyStore, IDisposable
{
    private long nextId;

    public ConcurrentDictionary<long, OnboardTerminalKey> Rows { get; } = new();

    public void Add(string vehicleId, string keyPin)
    {
        var id = Interlocked.Increment(ref nextId);
        Rows[id] = new OnboardTerminalKey { Id = id, VehicleId = vehicleId, KeyPin = keyPin, ConfirmedAt = DateTimeOffset.UnixEpoch, ConfirmedBy = "test" };
    }

    public Task<IReadOnlyList<OnboardTerminalKey>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OnboardTerminalKey>>([.. Rows.Values]);

    public Task<OnboardTerminalKey> RegisterAsync(string vehicleId, string keyPin, DateTimeOffset at, Actor actor, object eventData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        foreach (var (id, row) in Rows)
        {
            if (row.VehicleId == vehicleId || row.KeyPin == keyPin)
            {
                Rows.TryRemove(id, out _);
            }
        }

        var key = new OnboardTerminalKey { Id = Interlocked.Increment(ref nextId), VehicleId = vehicleId, KeyPin = keyPin, ConfirmedAt = at, ConfirmedBy = actor.Account };
        Rows[key.Id] = key;
        return Task.FromResult(key);
    }

    public Task RemoveAsync(long id, string eventType, Actor? actor, object eventData, CancellationToken cancellationToken)
    {
        Rows.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public void Dispose() => Rows.Clear();
}
