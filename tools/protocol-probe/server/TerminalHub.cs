using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Grpc.Core;
using Serilog;
using Simple.ProtocolProbe.V1;
using Status = Grpc.Core.Status;
using TerminalStatus = Simple.ProtocolProbe.V1.Status;

namespace Simple.ProtocolProbe.Server;

/// <summary>
/// What the server knows about each terminal key: its one active stream, the
/// report-staleness timer (ADR 0009's "no fresh report for 10 s"), commands
/// awaiting an echo, and the clone check of ADR 0025 (two client instances
/// alternately delivering fresh reports under one key).
///
/// Freshness here is a probe-sized stand-in for ADR 0024's sequence scheme: per
/// client instance the server keeps the smallest (server clock - client elapsed)
/// seen so far, and a report's age is how far its own offset exceeds that. It
/// never reads the client's wall clock.
/// </summary>
internal sealed class TerminalHub(ProbeServerOptions options, TerminalRegistry registry) : IDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, KeyRuntime> runtimes = [];
    private readonly long origin = Stopwatch.GetTimestamp();
    private long nextSessionId;
    private ulong nextCommandId;

    public bool Quiet { get; set; }

    public bool Hint { get; set; }

    public int ActiveSessionCount
    {
        get
        {
            lock (gate)
            {
                return runtimes.Values.Count(r => r.Active is not null);
            }
        }
    }

    private double NowMs => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;

    /// <summary>Decides on a new stream from its first report; throws the
    /// status the client should see when the stream is refused.</summary>
    public Session Admit(ConnectionInfo info, Report first)
    {
        lock (gate)
        {
            var key = info.Key!;
            var entry = registry.Find(key);
            var claim = first.VehicleId;
            switch (entry?.State)
            {
                case null when claim.Length > 0:
                    throw Refuse(info, StatusCode.Unauthenticated, $"terminal key is not registered (it claims vehicle '{claim}')");
                case null:
                    entry = new TerminalEntry { Key = key, State = TerminalState.Pending, PairingCode = NewPairingCode() };
                    registry.Add(entry);
                    Log.Warning("PENDING new terminal key={Key} pairingCode={Code} conn={Conn}", Short(key), entry.PairingCode, info.ConnectionId);
                    break;
                case TerminalState.Revoked:
                    throw Refuse(info, StatusCode.PermissionDenied, "terminal key has been revoked");
                case TerminalState.CloneSuspected:
                    Log.Warning("ALERT clone-suspected key={Key} vehicle={Vehicle} tried to connect again conn={Conn} instance={Instance}",
                        Short(key), entry.VehicleId, info.ConnectionId, first.InstanceId);
                    throw Refuse(info, StatusCode.PermissionDenied, "terminal key is suspected cloned; an engineer must re-confirm it");
                case TerminalState.Pending when claim.Length > 0:
                    throw Refuse(info, StatusCode.Unauthenticated, $"terminal key is pending and may not claim vehicle '{claim}'");
                case TerminalState.Confirmed when claim.Length > 0 && claim != entry.VehicleId:
                    throw Refuse(info, StatusCode.Unauthenticated, $"terminal key belongs to '{entry.VehicleId}', not '{claim}'");
                default:
                    break;
            }

            var runtime = Runtime(key);
            var session = new Session(++nextSessionId, key, info, first.InstanceId);
            if (runtime.Active is { } old)
            {
                Log.Information("SUPERSEDE key={Key} vehicle={Vehicle} old=s{Old}/{OldConn} new=s{New}/{NewConn} oldInstance={OldInstance} newInstance={NewInstance}",
                    Short(key), entry.VehicleId, old.Id, old.Info.ConnectionId, session.Id, info.ConnectionId, old.InstanceId, first.InstanceId);
                old.Kill(new Status(StatusCode.Aborted, $"superseded by a newer stream of the same key (s{session.Id})"));
            }

            runtime.Active = session;
            runtime.Sessions++;
            Log.Information("ADMIT s{Session} key={Key} state={State} vehicle={Vehicle} conn={Conn} instance={Instance} major={Major} minor={Minor}",
                session.Id, Short(key), entry.State, entry.VehicleId, info.ConnectionId, first.InstanceId, info.Major, first.ProtocolMinor);
            session.Send(StatusOf(entry));
            return session;
        }
    }

    public void OnReport(Session session, Report report)
    {
        lock (gate)
        {
            var now = NowMs;
            var gap = session.LastReportMs is { } last ? now - last : (double?)null;
            session.LastReportMs = now;
            session.Reports++;
            if (gap is { } g)
            {
                session.GapsMs.Add(g);
            }

            var runtime = Runtime(session.Key);
            var clock = runtime.Clock(report.InstanceId);
            var offset = now - report.ElapsedMs;
            clock.MinOffsetMs = Math.Min(clock.MinOffsetMs, offset);
            var age = offset - clock.MinOffsetMs;
            var fresh = age <= options.FreshWithin.TotalMilliseconds;
            var entry = registry.Find(session.Key);
            var unknownFields = UnknownFields.Of(report);
            Log.Information("RX s{Session} seq={Seq} elapsedMs={Elapsed} ageMs={Age:F0} fresh={Fresh} gapMs={Gap} echo=[{Echo}] state={State} unknownFields=[{Unknown}]",
                session.Id, report.Seq, report.ElapsedMs, age, fresh, gap?.ToString("F0", CultureInfo.InvariantCulture) ?? "-",
                string.Join(',', report.ReceivedCommandIds), entry?.State, unknownFields);

            if (entry?.State != TerminalState.Confirmed || runtime.Active != session)
            {
                return;
            }

            foreach (var id in report.ReceivedCommandIds)
            {
                if (runtime.Outstanding.Remove(id, out var sentAt))
                {
                    runtime.Echoed++;
                    Log.Information("ECHO s{Session} command={Id} roundTripMs={Ms:F0}", session.Id, id, now - sentAt);
                }
                else
                {
                    Log.Warning("ECHO-UNKNOWN s{Session} command={Id} (duplicate or not sent to this key)", session.Id, id);
                }
            }

            if (!fresh)
            {
                return;
            }

            if (clock.LastFreshMs is { } previous && now - previous <= options.CloneWindow.TotalMilliseconds)
            {
                var other = runtime.Instances.FirstOrDefault(i => i.Key != report.InstanceId && i.Value.LastFreshMs > previous);
                if (other.Key is not null)
                {
                    entry.State = TerminalState.CloneSuspected;
                    registry.Save();
                    Log.Warning("ALERT clone-suspected key={Key} vehicle={Vehicle}: instances {A} and {B} delivered fresh reports alternately within {Window}s; both refused until re-confirmed",
                        Short(session.Key), entry.VehicleId, report.InstanceId, other.Key, options.CloneWindow.TotalSeconds);
                    session.Kill(new Status(StatusCode.PermissionDenied, "terminal key is suspected cloned; an engineer must re-confirm it"));
                    return;
                }
            }

            clock.LastFreshMs = now;
            if (runtime.Stale)
            {
                Log.Warning("FRESH-AGAIN key={Key} vehicle={Vehicle} s{Session} after {Ms:F0} ms without a fresh report",
                    Short(session.Key), entry.VehicleId, session.Id, now - runtime.LastFreshMs);
                runtime.Stale = false;
            }

            runtime.LastFreshMs = now;
            runtime.StaleTimer ??= new Timer(_ => OnStale(session.Key), null, Timeout.Infinite, Timeout.Infinite);
            runtime.StaleTimer.Change(options.StaleAfter, Timeout.InfiniteTimeSpan);
        }
    }

    public bool IsConfirmed(string key)
    {
        lock (gate)
        {
            return registry.Find(key)?.State == TerminalState.Confirmed;
        }
    }

    public void SendCommandIfDue(Session session)
    {
        lock (gate)
        {
            var runtime = Runtime(session.Key);
            if (Quiet || runtime.Active != session || registry.Find(session.Key)?.State != TerminalState.Confirmed)
            {
                return;
            }

            var id = ++nextCommandId;
            runtime.Outstanding[id] = NowMs;
            runtime.Sent++;
            session.Send(new Downstream { Command = new Command { Id = id, Text = $"probe command {id}" } });
        }
    }

    public Downstream Decorate(Downstream message)
    {
        if (Hint)
        {
            message.DisplayHint = "1.1-server field the 1.0 client does not know";
        }

        return message;
    }

    public void Detach(Session session, string reason)
    {
        lock (gate)
        {
            var runtime = Runtime(session.Key);
            if (runtime.Active == session)
            {
                runtime.Active = null;
            }

            Log.Information("SESSION-END s{Session} key={Key} conn={Conn} lasted={Lasted:F1}s reports={Reports} gaps(p50/p99/max ms)={Gaps} reason={Reason}",
                session.Id, Short(session.Key), session.Info.ConnectionId, Stopwatch.GetElapsedTime(session.Started).TotalSeconds, session.Reports,
                Percentiles(session.GapsMs), reason);
        }
    }

    public void List()
    {
        lock (gate)
        {
            if (registry.All.Count == 0)
            {
                Log.Information("LIST (no terminals)");
            }

            foreach (var e in registry.All)
            {
                var runtime = Runtime(e.Key);
                Log.Information("LIST key={Key} state={State} vehicle={Vehicle} pairingCode={Code} connected={Connected} lastFreshAgoMs={Ago}",
                    Short(e.Key), e.State, e.VehicleId ?? "-", e.PairingCode ?? "-",
                    runtime.Active is { } s ? $"s{s.Id}/{s.Info.ConnectionId}" : "no",
                    runtime.StaleTimer is not null ? (NowMs - runtime.LastFreshMs).ToString("F0", CultureInfo.InvariantCulture) : "-");
            }
        }
    }

    public void Confirm(string code, string vehicleId)
    {
        lock (gate)
        {
            var entry = registry.FindByPairingCode(code);
            if (entry is null)
            {
                Log.Warning("CONFIRM refused: no pending terminal with pairing code {Code}", code);
                return;
            }

            var taken = registry.All.FirstOrDefault(e => e.State == TerminalState.Confirmed && e.VehicleId == vehicleId);
            if (taken is not null)
            {
                Log.Warning("CONFIRM refused: vehicle {Vehicle} is already bound to key {Key}; revoke or forget it first", vehicleId, Short(taken.Key));
                return;
            }

            entry.State = TerminalState.Confirmed;
            entry.VehicleId = vehicleId;
            entry.PairingCode = null;
            registry.Save();
            Log.Information("CONFIRM key={Key} is vehicle {Vehicle}", Short(entry.Key), vehicleId);
            Runtime(entry.Key).Active?.Send(StatusOf(entry));
        }
    }

    public void Revoke(string text)
    {
        lock (gate)
        {
            var entry = registry.Match(text, out var problem);
            if (entry is null)
            {
                Log.Warning("REVOKE refused: {Problem}", problem);
                return;
            }

            entry.State = TerminalState.Revoked;
            registry.Save();
            Log.Information("REVOKE key={Key} vehicle={Vehicle}", Short(entry.Key), entry.VehicleId);
            Runtime(entry.Key).Active?.Kill(new Status(StatusCode.PermissionDenied, "terminal key has been revoked"));
        }
    }

    public void Forget(string text)
    {
        lock (gate)
        {
            var entry = registry.Match(text, out var problem);
            if (entry is null)
            {
                Log.Warning("FORGET refused: {Problem}", problem);
                return;
            }

            registry.Remove(entry.Key);
            var runtime = Runtime(entry.Key);
            runtime.Instances.Clear();
            Log.Information("FORGET key={Key} vehicle={Vehicle}: its next stream starts as pending", Short(entry.Key), entry.VehicleId);
            runtime.Active?.Kill(new Status(StatusCode.Aborted, "terminal registration was reset"));
        }
    }

    public void Stats()
    {
        lock (gate)
        {
            foreach (var (key, r) in runtimes)
            {
                Log.Information("STATS key={Key} vehicle={Vehicle} sessions={Sessions} commandsSent={Sent} echoed={Echoed} outstanding=[{Outstanding}] active={Active} reports={Reports} gaps(p50/p99/max ms)={Gaps}",
                    Short(key), registry.Find(key)?.VehicleId, r.Sessions, r.Sent, r.Echoed, string.Join(',', r.Outstanding.Keys),
                    r.Active is { } s ? $"s{s.Id}" : "no", r.Active?.Reports ?? 0, r.Active is { } a ? Percentiles(a.GapsMs) : "-");
            }
        }
    }

    public void Dispose()
    {
        foreach (var r in runtimes.Values)
        {
            r.StaleTimer?.Dispose();
        }
    }

    internal static string Short(string key) => key[..12];

    private static string Percentiles(List<double> values)
    {
        if (values.Count == 0)
        {
            return "-";
        }

        var sorted = values.Order().ToArray();
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"{At(0.5):F0}/{At(0.99):F0}/{sorted[^1]:F0} n={sorted.Length}");
    }

    private static Downstream StatusOf(TerminalEntry entry) => new()
    {
        Status = entry.State == TerminalState.Confirmed
            ? new TerminalStatus { State = TerminalStatus.Types.State.Accepted, VehicleId = entry.VehicleId, Message = "confirmed" }
            : new TerminalStatus { State = TerminalStatus.Types.State.Pending, PairingCode = entry.PairingCode, Message = "waiting for confirmation in the console" },
    };

    private static RpcException Refuse(ConnectionInfo info, StatusCode code, string detail)
    {
        Log.Warning("REFUSE key={Key} conn={Conn} status={Code} detail={Detail}", Short(info.Key!), info.ConnectionId, code, detail);
        return new RpcException(new Status(code, detail));
    }

    private string NewPairingCode()
    {
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        while (registry.FindByPairingCode(code) is not null);
        return code;
    }

    private void OnStale(string key)
    {
        lock (gate)
        {
            var runtime = Runtime(key);
            var since = NowMs - runtime.LastFreshMs;
            if (runtime.Stale || since < options.StaleAfter.TotalMilliseconds - 50)
            {
                return;
            }

            runtime.Stale = true;
            Log.Warning("STALE key={Key} vehicle={Vehicle} no fresh report for {Ms:F0} ms (limit {Limit}s) active={Active}",
                Short(key), registry.Find(key)?.VehicleId, since, options.StaleAfter.TotalSeconds,
                runtime.Active is { } s ? $"s{s.Id}/{s.Info.ConnectionId}" : "no");
        }
    }

    private KeyRuntime Runtime(string key)
    {
        if (!runtimes.TryGetValue(key, out var runtime))
        {
            runtime = new KeyRuntime();
            runtimes[key] = runtime;
        }

        return runtime;
    }

    private sealed class KeyRuntime
    {
        public Session? Active { get; set; }
        public int Sessions { get; set; }
        public Dictionary<string, InstanceClock> Instances { get; } = [];
        public Timer? StaleTimer { get; set; }
        public bool Stale { get; set; }
        public double LastFreshMs { get; set; }
        public Dictionary<ulong, double> Outstanding { get; } = [];
        public long Sent { get; set; }
        public long Echoed { get; set; }

        public InstanceClock Clock(string instance)
        {
            if (!Instances.TryGetValue(instance, out var clock))
            {
                clock = new InstanceClock();
                Instances[instance] = clock;
            }

            return clock;
        }
    }

    private sealed class InstanceClock
    {
        public double MinOffsetMs { get; set; } = double.MaxValue;
        public double? LastFreshMs { get; set; }
    }

    internal sealed class Session(long id, string key, ConnectionInfo info, string instanceId)
    {
        private readonly Channel<Downstream> outbox = Channel.CreateUnbounded<Downstream>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<Status> killed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Id { get; } = id;
        public string Key { get; } = key;
        public ConnectionInfo Info { get; } = info;
        public string InstanceId { get; } = instanceId;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public double? LastReportMs { get; set; }
        public long Reports { get; set; }
        public List<double> GapsMs { get; } = [];
        public Task<Status> Killed => killed.Task;
        public ChannelReader<Downstream> Outbox => outbox.Reader;

        public void Send(Downstream message) => outbox.Writer.TryWrite(message);

        public void Kill(Status status) => killed.TrySetResult(status);
    }
}
