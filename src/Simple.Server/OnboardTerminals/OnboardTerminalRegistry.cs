using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.Status;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// Which terminal streams the server listens to, and what it knows about
/// every vehicle's terminal: its stream (if one is open), its version, and
/// whether its reports are fresh.
/// </summary>
/// <remarks>
/// <para>
/// A terminal is known by its key (ADR 0025): the pin of the certificate it
/// presents. Its reports are used only while that key is registered for the
/// vehicle it claims (ADR 0007). Any other stream waits for confirmation,
/// whatever vehicle it claims: its reports are not used, it is sent no unlock
/// requests and no questions, and its screen shows a pairing code that the
/// pending list shows too. An engineer confirms the key for the vehicle it
/// claims; the vehicle's earlier key is released with it. Registrations live
/// in the database (<see cref="IOnboardTerminalKeyStore"/>); the pending list
/// lives in memory, so a server restart gives a waiting terminal a new code.
/// </para>
/// <para>
/// A new stream of a key replaces the key's older stream. Two computers with
/// one key (a cloned disk) are told from one computer that reconnects by the
/// run id of their reports: when a run whose stream another run replaced
/// sends a fresh report again, both are running, and the key's registration
/// ends at once; both wait for confirmation again.
/// </para>
/// <para>
/// Freshness is judged on the server's own clock (ADR 0024): a report that
/// names, as the latest state it had received, the desired state with seq S
/// was written no earlier than the server sent S. A report is fresh when that
/// bound is at most <see cref="StaleAfter"/> old; the terminal's reports are
/// stale once the freshest one is older than that. Whether the stream is open
/// does not enter into it: a stream can stay open while nothing gets through,
/// and a terminal can be reconnecting while its last report is still fresh.
/// </para>
/// <para>
/// Events and changes to the registrations go into the database one at a
/// time, in the order the registry decided them (under its lock), through one
/// writer: a stale terminal's fresh-again never lands before its stale, a
/// replaced stream's disconnect never after the new stream's connect.
/// </para>
/// </remarks>
public sealed partial class OnboardTerminalRegistry : IDisposable
{
    /// <summary>ADR 0009: a reading older than this is not a reading.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Limits on what one report may bring in. A stream waiting for
    /// confirmation can come from anyone on the plant network, so these keep a
    /// stranger from filling memory and the event record with one report. A
    /// vehicle id over the limit ends the stream; an input over a limit is
    /// skipped, as ADR 0024 skips any item it cannot use.
    /// </summary>
    public const int MaxVehicleIdLength = 64;

    public const int MaxInputsPerReport = 16;

    public const int MaxInputTextLength = 256;

    /// <summary>A vehicle's IO module has 16 inputs and 16 outputs; these leave room for a larger one.</summary>
    public const int MaxIoPointsPerReport = 128;

    public const int MaxIoPointNumber = 64;

    /// <summary>
    /// How many keys may wait for confirmation at once. The oldest one without
    /// a stream makes room for a new one; when all have a stream, a new key's
    /// stream is refused. A limit against strangers on the plant network only:
    /// a site has a handful of vehicles.
    /// </summary>
    public const int MaxPending = 64;

    /// <summary>How many replaced runs of a key are remembered for the clone check.</summary>
    public const int MaxReplacedRuns = 16;

    /// <summary>What the screen shows in this version, where the server has nothing else to say yet.</summary>
    public const string ConnectedScreenText = "已连接服务端";

    /// <summary>What the screen shows before the terminal's first report has said which vehicle it claims.</summary>
    public const string IdentifyingScreenText = "正在与服务端核对本机身份";

    private readonly TimeProvider time;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<OnboardTerminalRegistry> logger;
    private readonly IVehicleList vehicles;
    private readonly Lock gate = new();

    /// <summary>Every vehicle whose terminal's reports have been used since the server started.</summary>
    private readonly Dictionary<string, KnownTerminal> terminals = new(StringComparer.Ordinal);

    /// <summary>The registered key of each vehicle that has one.</summary>
    private readonly Dictionary<string, OnboardTerminalKey> registrations = new(StringComparer.Ordinal);

    /// <summary>Keys waiting for confirmation, by pin.</summary>
    private readonly Dictionary<string, PendingTerminal> pending = new(StringComparer.Ordinal);

    /// <summary>The newest stream of each key that has said which vehicle it claims.</summary>
    private readonly Dictionary<string, TerminalLink> links = new(StringComparer.Ordinal);

    /// <summary>The runs each registered key's fresh reports came from (the clone check).</summary>
    private readonly Dictionary<string, Runs> runs = new(StringComparer.Ordinal);

    private readonly Channel<QueueItem> queue = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writing;
    private bool disposed;

    public OnboardTerminalRegistry(TimeProvider time, IServiceScopeFactory scopes, ILogger<OnboardTerminalRegistry> logger, IVehicleList? vehicles = null)
    {
        this.time = time;
        this.scopes = scopes;
        this.logger = logger;
        this.vehicles = vehicles ?? new AnyVehicle();
        writing = Task.Run(WriteAsync);
    }

    /// <summary>Reads the registered keys from the store. Called once at start, before any stream.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<IOnboardTerminalKeyStore>().LoadAsync(cancellationToken);
        lock (gate)
        {
            registrations.Clear();
            foreach (var key in keys)
            {
                registrations[key.VehicleId] = key;
            }

            foreach (var link in links.Values.ToList())
            {
                Reevaluate(link, "the registered keys were read again");
            }
        }
    }

    /// <param name="keyPin">The pin of the certificate the terminal presented.</param>
    public TerminalLink Open(string remoteAddress, string keyPin) =>
        new(Guid.NewGuid().ToString("N")[..12], remoteAddress, keyPin, time.GetUtcNow());

    /// <summary>The next desired state to send on <paramref name="link"/>; remembers when it was sent.</summary>
    public DesiredState NextDesiredState(TerminalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (gate)
        {
            var now = time.GetUtcNow();
            var state = new DesiredState
            {
                Seq = ++link.LastSentSeq,
                LastReceivedSeq = link.LastReceivedSeq,
                Screen = new Screen { Text = ScreenText(link) },
            };
            link.Sent.Enqueue((state.Seq, now));
            // A state sent longer ago than StaleAfter cannot make a report
            // fresh any more; a report naming it gets no bound at all.
            while (link.Sent.TryPeek(out var oldest) && now - oldest.SentAt > StaleAfter)
            {
                link.Sent.Dequeue();
            }

            link.LastScreenText = state.Screen.Text;
            if (link.Terminal is { } terminal && terminal.Link == link)
            {
                terminal.LastScreenText = state.Screen.Text;
            }

            return state;
        }
    }

    /// <summary>
    /// Takes in one report. Returns null when it is taken in (whether or not
    /// it is used), or why the stream must end.
    /// </summary>
    public string? Receive(TerminalLink link, OnboardReport report)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(report);
        TerminalLink? replaced = null;
        lock (gate)
        {
            var now = time.GetUtcNow();
            link.LastReceivedSeq = report.Seq;
            if (link.Replaced)
            {
                // A newer stream of the same key took over; this one is ending.
                return null;
            }

            link.Version = report.TerminalVersion;
            if (link.Claim is null)
            {
                if (string.IsNullOrWhiteSpace(report.VehicleId))
                {
                    return "the first report names no vehicle. Every terminal is configured with the vehicle it is on (ADR 0007).";
                }

                if (report.VehicleId.Length > MaxVehicleIdLength)
                {
                    return $"the vehicle id is longer than {MaxVehicleIdLength} characters.";
                }

                if (!links.TryGetValue(link.KeyPin, out var previous) && !pending.ContainsKey(link.KeyPin) && !IsRegistered(link.KeyPin) && !MakeRoom())
                {
                    return $"{MaxPending} terminals are waiting for confirmation already, each with a stream open.";
                }

                link.Claim = report.VehicleId;
                if (previous is not null && previous != link)
                {
                    replaced = previous;
                    previous.Replaced = true;
                    if (previous.Terminal is { } terminal && terminal.Link == previous)
                    {
                        // Its close records the disconnect.
                        terminal.Link = null;
                    }

                    if (pending.TryGetValue(previous.KeyPin, out var waiting) && waiting.Link == previous)
                    {
                        waiting.Link = null;
                    }
                }

                links[link.KeyPin] = link;
                Place(link, now);
            }
            else if (!string.Equals(report.VehicleId, link.Claim, StringComparison.Ordinal))
            {
                LogVehicleChanged(logger, link.Id, link.Claim, report.VehicleId);
            }

            if (link.Terminal is { } accepted)
            {
                Use(link, accepted, report, now);
            }
        }

        replaced?.Stop("replaced by a newer stream with the same key");
        return null;
    }

    /// <summary>The stream of <paramref name="link"/> has ended.</summary>
    public void Close(TerminalLink link, string reason)
    {
        ArgumentNullException.ThrowIfNull(link);
        string? vehicle;
        lock (gate)
        {
            if (links.TryGetValue(link.KeyPin, out var current) && current == link)
            {
                links.Remove(link.KeyPin);
            }

            if (pending.TryGetValue(link.KeyPin, out var waiting) && waiting.Link == link)
            {
                waiting.Link = null;
            }

            vehicle = link.Terminal?.VehicleId;
            if (link.Terminal is { } terminal && terminal.Link == link)
            {
                terminal.Link = null;
            }

            if (vehicle is not null)
            {
                Record(EventTypes.OnboardDisconnected, EventSources.Server, new { VehicleId = vehicle, Connection = link.Id, Reason = reason });
            }
        }

        LogClosed(logger, link.Id, vehicle ?? link.Claim ?? "-", reason);
    }

    /// <summary>
    /// An engineer confirms that the terminal waiting with <paramref name="pairingCode"/>
    /// is <paramref name="vehicleId"/>, the vehicle it claims (ADR 0025, 0029).
    /// Its key becomes the vehicle's registered key, and the vehicle's earlier
    /// key, if any, is released; the key's earlier vehicle, if it had one,
    /// loses it. Takes effect on the open stream at once.
    /// </summary>
    public async Task<PairingOutcome> ConfirmAsync(string vehicleId, string pairingCode, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var done = new TaskCompletionSource<PairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (pending.Values.FirstOrDefault(p => p.Claim == vehicleId && p.Code == pairingCode) is not { } waiting)
            {
                return PairingOutcome.NotFound($"No terminal waiting for confirmation claims vehicle {vehicleId} with that pairing code.");
            }

            if (!vehicles.Contains(vehicleId))
            {
                return PairingOutcome.Conflict(
                    $"Vehicle {vehicleId} is not in the vehicle list. A terminal claiming it is listed, but it can be confirmed only once the vehicle is in the list (ADR 0029).");
            }

            var keyPin = waiting.KeyPin;
            var replacedKey = registrations.GetValueOrDefault(vehicleId)?.KeyPin;
            var movedFrom = registrations.Values.FirstOrDefault(r => r.KeyPin == keyPin && r.VehicleId != vehicleId)?.VehicleId;
            var at = time.GetUtcNow();
            var queued = Enqueue(async services =>
            {
                try
                {
                    var key = await services.GetRequiredService<IOnboardTerminalKeyStore>().RegisterAsync(
                        vehicleId,
                        keyPin,
                        at,
                        actor,
                        new { VehicleId = vehicleId, KeyPin = keyPin, ReplacedKeyPin = replacedKey, MovedFromVehicle = movedFrom },
                        CancellationToken.None);
                    lock (gate)
                    {
                        foreach (var vehicle in registrations.Where(r => r.Key == vehicleId || r.Value.KeyPin == keyPin).Select(r => r.Key).ToList())
                        {
                            registrations.Remove(vehicle);
                        }

                        registrations[vehicleId] = key;
                        pending.Remove(keyPin);
                        runs.Remove(keyPin);
                        if (replacedKey is not null)
                        {
                            runs.Remove(replacedKey);
                        }

                        foreach (var link in links.Values.ToList())
                        {
                            Reevaluate(link, $"another key was confirmed for vehicle {vehicleId}");
                        }
                    }

                    LogConfirmed(logger, vehicleId, keyPin, actor.Account ?? "-");
                    done.TrySetResult(PairingOutcome.Done(key, replacedKey == keyPin ? null : replacedKey));
                }
                catch (Exception e)
                {
                    LogNotSaved(logger, e, vehicleId);
                    done.TrySetResult(PairingOutcome.NotSaved(e.Message));
                }
            });
            if (!queued)
            {
                return PairingOutcome.NotSaved("the server is stopping.");
            }
        }

        return await done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// An engineer revokes the key registered for <paramref name="vehicleId"/>
    /// (ADR 0025): its stream, if open, waits for confirmation from now on.
    /// </summary>
    public async Task<PairingOutcome> RevokeAsync(string vehicleId, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var done = new TaskCompletionSource<PairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!registrations.TryGetValue(vehicleId, out var key))
            {
                return PairingOutcome.NotFound($"Vehicle {vehicleId} has no registered terminal key.");
            }

            var queued = Enqueue(async services =>
            {
                try
                {
                    await services.GetRequiredService<IOnboardTerminalKeyStore>().RemoveAsync(
                        key.Id, EventTypes.OnboardRevoked, actor, new { VehicleId = vehicleId, key.KeyPin }, CancellationToken.None);
                    lock (gate)
                    {
                        if (registrations.TryGetValue(vehicleId, out var current) && current.Id == key.Id)
                        {
                            registrations.Remove(vehicleId);
                            runs.Remove(key.KeyPin);
                            foreach (var link in links.Values.ToList())
                            {
                                Reevaluate(link, "its key was revoked");
                            }
                        }
                    }

                    LogRevoked(logger, vehicleId, key.KeyPin, actor.Account ?? "-");
                    done.TrySetResult(PairingOutcome.Done(key, null));
                }
                catch (Exception e)
                {
                    LogNotSaved(logger, e, vehicleId);
                    done.TrySetResult(PairingOutcome.NotSaved(e.Message));
                }
            });
            if (!queued)
            {
                return PairingOutcome.NotSaved("the server is stopping.");
            }
        }

        return await done.Task.WaitAsync(cancellationToken);
    }

    public void RecordRefusal(string remoteAddress, string? major, string reason)
    {
        lock (gate)
        {
            Record(EventTypes.OnboardRefused, EventSources.Server, new { Remote = remoteAddress, ProtocolMajor = major, Reason = reason });
        }
    }

    /// <summary>Waits until everything decided so far is in the database (or failed to get there and was logged).</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!queue.Writer.TryWrite(new QueueItem(null, null, null, done, null)))
            {
                return Task.CompletedTask;
            }
        }

        return done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Every vehicle whose terminal's reports have been used since the server started.</summary>
    public IReadOnlyList<OnboardTerminalStatus> Snapshot()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            return terminals.Values
                .OrderBy(t => t.VehicleId, StringComparer.Ordinal)
                .Select(t => new OnboardTerminalStatus(
                    t.VehicleId,
                    t.Version,
                    Connected: t.Link is not null,
                    t.Link?.RemoteAddress,
                    t.FreshWrittenAt is { } written ? Math.Round((now - written).TotalSeconds, 3) : null,
                    t.Stale,
                    t.IoModuleReadable ?? false,
                    t.LastScreenText,
                    t.Io,
                    t.KeyPin))
                .ToList();
        }
    }

    /// <summary>Every key waiting for confirmation, the oldest first.</summary>
    public IReadOnlyList<PendingOnboardTerminalStatus> Pending()
    {
        lock (gate)
        {
            return pending.Values
                .OrderBy(p => p.FirstSeenAt)
                .Select(p => new PendingOnboardTerminalStatus(
                    p.KeyPin,
                    p.Claim,
                    p.Code,
                    Connected: p.Link is not null,
                    p.Link?.RemoteAddress,
                    p.Link?.Version ?? p.Version,
                    p.FirstSeenAt,
                    vehicles.Contains(p.Claim),
                    VehicleOnlineWithAnotherKey(p),
                    registrations.Values.FirstOrDefault(r => r.KeyPin == p.KeyPin && r.VehicleId != p.Claim)?.VehicleId,
                    p.CloneSuspected))
                .ToList();
        }
    }

    /// <summary>Every vehicle's registered key.</summary>
    public IReadOnlyList<OnboardTerminalKeyStatus> Registrations()
    {
        lock (gate)
        {
            return registrations.Values
                .OrderBy(r => r.VehicleId, StringComparer.Ordinal)
                .Select(r => new OnboardTerminalKeyStatus(r.VehicleId, r.KeyPin, r.ConfirmedAt, r.ConfirmedBy))
                .ToList();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var terminal in terminals.Values)
            {
                terminal.Timer?.Dispose();
            }

            queue.Writer.TryComplete();
        }

        // What is queued still gets written; the database outlives this.
        writing.Wait(TimeSpan.FromSeconds(10));
    }

    private bool IsRegistered(string keyPin) => registrations.Values.Any(r => r.KeyPin == keyPin);

    private bool VehicleOnlineWithAnotherKey(PendingTerminal waiting) =>
        registrations.TryGetValue(waiting.Claim, out var key)
        && key.KeyPin != waiting.KeyPin
        && terminals.TryGetValue(waiting.Claim, out var terminal)
        && terminal.Link is not null;

    private string ScreenText(TerminalLink link)
    {
        if (link.Terminal is not null)
        {
            return ConnectedScreenText;
        }

        return link.Claim is not null && pending.TryGetValue(link.KeyPin, out var waiting)
            ? PairingScreenText(waiting.Claim, waiting.Code)
            : IdentifyingScreenText;
    }

    /// <summary>The whole screen while a terminal waits for confirmation (ADR 0025, 0038): the code the engineer checks, and the vehicle it is configured for.</summary>
    public static string PairingScreenText(string vehicleId, string code) =>
        $"等待在控制台确认这是哪台车（本机配置为 {vehicleId}），配对码 {code}";

    /// <summary>Uses <paramref name="link"/>'s reports when its key is registered for the vehicle it claims, otherwise puts it on the pending list.</summary>
    private void Place(TerminalLink link, DateTimeOffset now)
    {
        if (registrations.TryGetValue(link.Claim!, out var key) && key.KeyPin == link.KeyPin)
        {
            Accept(link, now);
        }
        else
        {
            Wait(link, now, cloneSuspected: false);
        }
    }

    /// <summary>After a registration changed: a stream whose key no longer fits its claim waits, one whose key now fits is used.</summary>
    private void Reevaluate(TerminalLink link, string reason)
    {
        if (link.Claim is null || link.Replaced)
        {
            return;
        }

        var registered = registrations.TryGetValue(link.Claim, out var key) && key.KeyPin == link.KeyPin;
        if (registered && link.Terminal is null)
        {
            Accept(link, time.GetUtcNow());
        }
        else if (!registered && link.Terminal is not null)
        {
            Release(link, reason, cloneSuspected: false);
        }
    }

    private void Accept(TerminalLink link, DateTimeOffset now)
    {
        pending.Remove(link.KeyPin);
        if (!terminals.TryGetValue(link.Claim!, out var terminal))
        {
            terminal = new KnownTerminal(link.Claim!, now);
            terminal.Timer = time.CreateTimer(OnStaleTimer, terminal, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            terminals.Add(terminal.VehicleId, terminal);
            Arm(terminal, now);
        }

        terminal.Link = link;
        terminal.KeyPin = link.KeyPin;
        terminal.LastScreenText = link.LastScreenText;
        terminal.Version = link.Version;
        link.Terminal = terminal;
        link.Wake();
        Record(EventTypes.OnboardConnected, EventSources.Server, new
        {
            terminal.VehicleId,
            link.KeyPin,
            TerminalVersion = link.Version,
            Connection = link.Id,
            Remote = link.RemoteAddress,
        });
    }

    /// <summary>The vehicle no longer uses <paramref name="link"/>'s reports: to the vehicle it is gone, and the stream waits for confirmation.</summary>
    private void Release(TerminalLink link, string reason, bool cloneSuspected)
    {
        var terminal = link.Terminal!;
        link.Terminal = null;
        if (terminal.Link == link)
        {
            terminal.Link = null;
        }

        Record(EventTypes.OnboardDisconnected, EventSources.Server, new { terminal.VehicleId, Connection = link.Id, Reason = reason });
        Wait(link, time.GetUtcNow(), cloneSuspected);
    }

    private void Wait(TerminalLink link, DateTimeOffset now, bool cloneSuspected)
    {
        if (pending.TryGetValue(link.KeyPin, out var waiting))
        {
            // The same key again, perhaps claiming another vehicle now: it keeps its code.
            waiting.Claim = link.Claim!;
            waiting.Link = link;
            waiting.CloneSuspected |= cloneSuspected;
            link.Wake();
            return;
        }

        MakeRoom();
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        while (pending.Values.Any(p => p.Code == code));

        waiting = new PendingTerminal(link.KeyPin, code, now) { Claim = link.Claim!, Link = link, Version = link.Version, CloneSuspected = cloneSuspected };
        pending.Add(link.KeyPin, waiting);
        link.Wake();
        // Not the pairing code: it is treated as a credential (ADR 0034), and
        // the event record is kept, exported and read by the on-duty AI.
        Record(EventTypes.OnboardPending, EventSources.Server, new
        {
            VehicleId = waiting.Claim,
            waiting.KeyPin,
            Remote = link.RemoteAddress,
            InVehicleList = vehicles.Contains(waiting.Claim),
            VehicleOnlineWithAnotherKey = VehicleOnlineWithAnotherKey(waiting),
            RegisteredForVehicle = registrations.Values.FirstOrDefault(r => r.KeyPin == waiting.KeyPin && r.VehicleId != waiting.Claim)?.VehicleId,
            CloneSuspected = cloneSuspected,
        });
    }

    /// <summary>Makes room for one more pending key, dropping the oldest one without a stream; false when every one has a stream.</summary>
    private bool MakeRoom()
    {
        if (pending.Count < MaxPending)
        {
            return true;
        }

        if (pending.Values.Where(p => p.Link is null).OrderBy(p => p.FirstSeenAt).FirstOrDefault() is not { } oldest)
        {
            return false;
        }

        pending.Remove(oldest.KeyPin);
        return true;
    }

    /// <summary>One report of a stream whose key is registered for the vehicle it claims.</summary>
    private void Use(TerminalLink link, KnownTerminal terminal, OnboardReport report, DateTimeOffset now)
    {
        var written = link.WrittenNoEarlierThan(report.LastReceivedSeq) is { } at && now - at <= StaleAfter ? at : (DateTimeOffset?)null;
        if (written is not null && IsClone(link.KeyPin, report.RunId))
        {
            SuspectClone(link, terminal, report.RunId);
            return;
        }

        terminal.Version = report.TerminalVersion;
        if (report.IoModuleReadable != terminal.IoModuleReadable && (terminal.IoModuleReadable is not null || !report.IoModuleReadable))
        {
            Record(
                report.IoModuleReadable ? EventTypes.OnboardIoReadable : EventTypes.OnboardIoUnreadable,
                EventSources.Server,
                new { terminal.VehicleId, Connection = link.Id, ReadAgeMs = report.HasIoReadAgeMs ? report.IoReadAgeMs : (ulong?)null });
        }

        terminal.IoModuleReadable = report.IoModuleReadable;
        terminal.Io = Io(link, report);
        if (written is { } fresh && (terminal.FreshWrittenAt is null || fresh > terminal.FreshWrittenAt))
        {
            terminal.FreshWrittenAt = fresh;
            if (terminal.Stale)
            {
                terminal.Stale = false;
                Record(EventTypes.OnboardFresh, EventSources.Server, new { terminal.VehicleId, Connection = link.Id });
            }

            Arm(terminal, now);
        }

        if (report.Inputs.Count > MaxInputsPerReport)
        {
            LogInputsSkipped(logger, link.Id, report.Inputs.Count - MaxInputsPerReport, $"more than {MaxInputsPerReport} in one report");
        }

        foreach (var input in report.Inputs.Take(MaxInputsPerReport))
        {
            if ((input.Scan?.Text.Length ?? 0) > MaxInputTextLength || (input.ButtonPress?.Button.Length ?? 0) > MaxInputTextLength)
            {
                LogInputsSkipped(logger, link.Id, 1, $"longer than {MaxInputTextLength} characters");
            }
            else if (link.SeenInputs.Add(input.Id))
            {
                Record(EventTypes.OnboardInputReceived, EventSources.OnboardTerminal, Describe(terminal, link, input));
            }
        }
    }

    /// <summary>
    /// Whether a fresh report from <paramref name="run"/> means two computers
    /// run with <paramref name="keyPin"/>: a run whose stream another run
    /// replaced is sending again. A terminal that reconnects keeps its run; a
    /// restarted program starts a new one and its old run never comes back.
    /// </summary>
    private bool IsClone(string keyPin, string run)
    {
        if (string.IsNullOrEmpty(run))
        {
            return false;
        }

        if (!runs.TryGetValue(keyPin, out var seen))
        {
            seen = new Runs();
            runs.Add(keyPin, seen);
        }

        if (seen.Replaced.Contains(run))
        {
            return true;
        }

        if (seen.Current is { } current && current != run)
        {
            seen.Replaced.Add(current);
            if (seen.Replaced.Count > MaxReplacedRuns)
            {
                seen.Replaced.RemoveAt(0);
            }
        }

        seen.Current = run;
        return false;
    }

    /// <summary>ADR 0025: the key's registration ends, neither computer's reports are used, and an engineer has to confirm the key again.</summary>
    private void SuspectClone(TerminalLink link, KnownTerminal terminal, string run)
    {
        var key = registrations[terminal.VehicleId];
        registrations.Remove(terminal.VehicleId);
        var other = runs.Remove(link.KeyPin, out var seen) ? seen.Current : null;
        var data = new { terminal.VehicleId, link.KeyPin, Runs = new[] { other, run }, Connection = link.Id, Remote = link.RemoteAddress };
        LogCloneSuspected(logger, terminal.VehicleId, link.KeyPin, other ?? "-", run);
        Enqueue(async services =>
        {
            try
            {
                await services.GetRequiredService<IOnboardTerminalKeyStore>().RemoveAsync(key.Id, EventTypes.OnboardCloneSuspected, null, data, CancellationToken.None);
            }
            catch (Exception e)
            {
                LogNotSaved(logger, e, terminal.VehicleId);
            }
        });
        Release(link, "its key sends fresh reports from two computers at once (a cloned disk); both wait for confirmation again", cloneSuspected: true);
    }

    /// <summary>
    /// The report's IO points as raw values, by kind and number; a point the
    /// server cannot use is skipped and logged (ADR 0024), and for a number
    /// reported twice the last one counts.
    /// </summary>
    private OnboardIoStatus Io(TerminalLink link, OnboardReport report)
    {
        var inputs = new SortedDictionary<int, OnboardIoPointStatus>();
        var outputs = new SortedDictionary<int, OnboardIoPointStatus>();
        var skipped = Math.Max(0, report.IoPoints.Count - MaxIoPointsPerReport);
        foreach (var point in report.IoPoints.Take(MaxIoPointsPerReport))
        {
            var into = point.Kind switch
            {
                IoPointKind.Input => inputs,
                IoPointKind.Output => outputs,
                _ => null,
            };
            if (into is null || point.Number is 0 or > MaxIoPointNumber)
            {
                skipped++;
                continue;
            }

            into[(int)point.Number] = new OnboardIoPointStatus((int)point.Number, point.Value ? 1 : 0, point.Changes, point.MsSinceChange);
        }

        if (skipped > 0)
        {
            LogIoPointsSkipped(logger, link.Id, skipped, MaxIoPointsPerReport, MaxIoPointNumber);
        }

        return new OnboardIoStatus(report.HasIoReadAgeMs ? report.IoReadAgeMs : null, [.. inputs.Values], [.. outputs.Values]);
    }

    private static object Describe(KnownTerminal terminal, TerminalLink link, OnboardInput input) => input.KindCase switch
    {
        OnboardInput.KindOneofCase.Scan => new { terminal.VehicleId, Connection = link.Id, InputId = input.Id, Kind = "scan", input.Scan.Text },
        OnboardInput.KindOneofCase.ButtonPress => new { terminal.VehicleId, Connection = link.Id, InputId = input.Id, Kind = "button", input.ButtonPress.Button },
        _ => new { terminal.VehicleId, Connection = link.Id, InputId = input.Id, Kind = "unknown" },
    };

    /// <summary>Sets the stale timer to when the freshest report (or, before any, the first one) is <see cref="StaleAfter"/> old.</summary>
    private static void Arm(KnownTerminal terminal, DateTimeOffset now)
    {
        var deadline = (terminal.FreshWrittenAt ?? terminal.FirstSeenAt) + StaleAfter;
        var due = deadline - now;
        terminal.Timer!.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void OnStaleTimer(object? state)
    {
        var terminal = (KnownTerminal)state!;
        lock (gate)
        {
            if (disposed || terminal.Stale)
            {
                return;
            }

            var now = time.GetUtcNow();
            var deadline = (terminal.FreshWrittenAt ?? terminal.FirstSeenAt) + StaleAfter;
            if (now < deadline)
            {
                Arm(terminal, now);
                return;
            }

            terminal.Stale = true;
            Record(EventTypes.OnboardStale, EventSources.Server, new
            {
                terminal.VehicleId,
                LastFreshReportWrittenNoEarlierThan = terminal.FreshWrittenAt,
                Connected = terminal.Link is not null,
            });
        }
    }

    /// <summary>Queues one event for the writer. Called under the lock, so the queue's order is the order of decisions.</summary>
    private void Record(string type, string source, object data) =>
        queue.Writer.TryWrite(new QueueItem(type, source, data, null, null));

    /// <summary>Queues a change to the registrations for the writer, under the lock like <see cref="Record"/>; false once the registry is disposed.</summary>
    private bool Enqueue(Func<IServiceProvider, Task> work) =>
        queue.Writer.TryWrite(new QueueItem(null, null, null, null, work));

    /// <summary>
    /// The one writer. A failure is logged, not thrown: the event record
    /// being unreachable must not end a terminal's stream.
    /// </summary>
    private async Task WriteAsync()
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            if (item.Flushed is { } flushed)
            {
                flushed.TrySetResult();
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (item.Work is { } work)
                {
                    await work(scope.ServiceProvider);
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(item.Type!, item.Source!, item.Data, CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                LogNotRecorded(logger, e, item.Type ?? "a change to the registered keys");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection} of vehicle {Vehicle} now reports vehicle {Reported}; it stays {Vehicle}")]
    private static partial void LogVehicleChanged(ILogger logger, string connection, string vehicle, string reported);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream {Connection} of vehicle {Vehicle} ended: {Reason}")]
    private static partial void LogClosed(ILogger logger, string connection, string vehicle, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Type} in the event record")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not save the change to vehicle {Vehicle}'s terminal key")]
    private static partial void LogNotSaved(ILogger logger, Exception exception, string vehicle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection}: skipped {Count} IO point(s): of unknown kind, numbered outside 1-{MaxNumber}, or beyond {MaxCount} in one report")]
    private static partial void LogIoPointsSkipped(ILogger logger, string connection, int count, int maxCount, int maxNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection}: skipped {Count} onboard input(s), {Why}")]
    private static partial void LogInputsSkipped(ILogger logger, string connection, int count, string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal key {KeyPin} confirmed for vehicle {Vehicle} by {Account}")]
    private static partial void LogConfirmed(ILogger logger, string vehicle, string keyPin, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal key {KeyPin} of vehicle {Vehicle} revoked by {Account}")]
    private static partial void LogRevoked(ILogger logger, string vehicle, string keyPin, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vehicle {Vehicle}'s terminal key {KeyPin} sends fresh reports from runs {Other} and {Run} alternately: two computers with one key. Its registration ended; both wait for confirmation.")]
    private static partial void LogCloneSuspected(ILogger logger, string vehicle, string keyPin, string other, string run);

    /// <summary>An event to append, a change to the registrations (<c>Work</c>), or (with <c>Flushed</c>) a marker that completes once everything before it is written.</summary>
    private sealed record QueueItem(string? Type, string? Source, object? Data, TaskCompletionSource? Flushed, Func<IServiceProvider, Task>? Work);

    /// <summary>The run a registered key's fresh reports come from now, and the runs that came before it.</summary>
    private sealed class Runs
    {
        public string? Current { get; set; }

        public List<string> Replaced { get; } = [];
    }
}

/// <summary>What became of a confirmation or a revocation; <see cref="Problem"/> is null when it was done.</summary>
/// <param name="Status">The HTTP status that says it: 200, 404, 409 or 503.</param>
/// <param name="ReplacedKeyPin">On a confirmation: the vehicle's earlier key, released by it.</param>
public sealed record PairingOutcome(int Status, string? Problem, OnboardTerminalKey? Key = null, string? ReplacedKeyPin = null)
{
    public static PairingOutcome Done(OnboardTerminalKey key, string? replacedKeyPin) => new(StatusCodes.Status200OK, null, key, replacedKeyPin);

    public static PairingOutcome NotFound(string problem) => new(StatusCodes.Status404NotFound, problem);

    public static PairingOutcome Conflict(string problem) => new(StatusCodes.Status409Conflict, problem);

    public static PairingOutcome NotSaved(string message) => new(StatusCodes.Status503ServiceUnavailable, $"The change could not be saved: {message}");
}

/// <summary>One stream from one terminal. Its sequence numbers start at 1 on every stream.</summary>
public sealed class TerminalLink(string id, string remoteAddress, string keyPin, DateTimeOffset openedAt) : IDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = id;

    public string RemoteAddress { get; } = remoteAddress;

    /// <summary>The pin of the certificate the terminal presented: which key it has.</summary>
    public string KeyPin { get; } = keyPin;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    /// <summary>Cancelled when the server ends this stream itself (see <see cref="StopReason"/>).</summary>
    public CancellationToken Stopped => stopped.Token;

    /// <summary>
    /// Completes when what the stream's next desired state says has changed
    /// (ADR 0024: a change goes out at once). Take it before the state it is
    /// compared with, so that no change falls in between.
    /// </summary>
    public Task Changed => Volatile.Read(ref changed).Task;

    public string? StopReason { get; private set; }

    internal ulong LastSentSeq { get; set; }

    internal ulong LastReceivedSeq { get; set; }

    internal string? LastScreenText { get; set; }

    /// <summary>The vehicle its first report claimed; null before that report.</summary>
    internal string? Claim { get; set; }

    internal string Version { get; set; } = string.Empty;

    /// <summary>A newer stream of the same key took over.</summary>
    internal bool Replaced { get; set; }

    internal Queue<(ulong Seq, DateTimeOffset SentAt)> Sent { get; } = new();

    internal HashSet<ulong> SeenInputs { get; } = [];

    /// <summary>The vehicle whose terminal this stream is while its reports are used; null while it waits.</summary>
    internal KnownTerminal? Terminal { get; set; }

    public void Dispose() => stopped.Dispose();

    internal void Wake() => Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    internal void Stop(string reason)
    {
        StopReason = reason;
        try
        {
            stopped.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its stream had ended already.
        }
    }

    /// <summary>When the desired state <paramref name="seq"/> was sent on this stream, if it is still remembered.</summary>
    internal DateTimeOffset? WrittenNoEarlierThan(ulong seq)
    {
        foreach (var (sent, at) in Sent)
        {
            if (sent == seq)
            {
                return at;
            }
        }

        return null;
    }
}

/// <summary>A vehicle whose terminal's reports the server has used.</summary>
internal sealed class KnownTerminal(string vehicleId, DateTimeOffset firstSeenAt)
{
    public string VehicleId { get; } = vehicleId;

    public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

    public string Version { get; set; } = string.Empty;

    /// <summary>The key its reports were last used from.</summary>
    public string KeyPin { get; set; } = string.Empty;

    /// <summary>Whether its latest report said it can read its IO module; null before its first report.</summary>
    public bool? IoModuleReadable { get; set; }

    /// <summary>The IO points of its latest report.</summary>
    public OnboardIoStatus Io { get; set; } = OnboardIoStatus.None;

    public TerminalLink? Link { get; set; }

    /// <summary>The freshest report so far was written no earlier than this.</summary>
    public DateTimeOffset? FreshWrittenAt { get; set; }

    public bool Stale { get; set; }

    /// <summary>The screen text of the latest desired state sent to it, kept after its stream ends.</summary>
    public string? LastScreenText { get; set; }

    public ITimer? Timer { get; set; }
}

/// <summary>A key waiting for confirmation, with the code its screen and the pending list show.</summary>
internal sealed class PendingTerminal(string keyPin, string code, DateTimeOffset firstSeenAt)
{
    public string KeyPin { get; } = keyPin;

    public string Code { get; } = code;

    public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

    /// <summary>The vehicle its newest stream claims.</summary>
    public required string Claim { get; set; }

    public TerminalLink? Link { get; set; }

    public string Version { get; set; } = string.Empty;

    /// <summary>It waits because its reports came from two computers at once.</summary>
    public bool CloneSuspected { get; set; }
}
