using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.Status;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// What the server knows about every onboard terminal that has reported: its
/// stream (if one is open), its version, and whether its reports are fresh.
/// A terminal is known by the vehicle it reports (ADR 0007); confirming that
/// claim with a pairing code is not built yet.
/// </summary>
/// <remarks>
/// Freshness is judged on the server's own clock (ADR 0024): a report that
/// names, as the latest state it had received, the desired state with seq S
/// was written no earlier than the server sent S. A report is fresh when that
/// bound is at most <see cref="StaleAfter"/> old; the terminal's reports are
/// stale once the freshest one is older than that. Whether the stream is open
/// does not enter into it: a stream can stay open while nothing gets through,
/// and a terminal can be reconnecting while its last report is still fresh.
/// </remarks>
public sealed partial class OnboardTerminalRegistry(
    TimeProvider time,
    IServiceScopeFactory scopes,
    ILogger<OnboardTerminalRegistry> logger) : IDisposable
{
    /// <summary>ADR 0009: a reading older than this is not a reading.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>What the screen shows in this version, where the server has nothing else to say yet.</summary>
    public const string ConnectedScreenText = "已连接服务端";

    private readonly Lock gate = new();
    private readonly Dictionary<string, KnownTerminal> terminals = new(StringComparer.Ordinal);
    private bool disposed;

    public TerminalLink Open(string remoteAddress) =>
        new(Guid.NewGuid().ToString("N")[..12], remoteAddress, time.GetUtcNow());

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
                Screen = new Screen { Text = ConnectedScreenText },
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
    /// Takes in one report. Returns null when it is accepted, or why the
    /// stream must end.
    /// </summary>
    public async Task<string?> ReceiveAsync(TerminalLink link, OnboardReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(report);
        var events = new List<(string Type, string Source, object Data)>();
        TerminalLink? replaced = null;
        lock (gate)
        {
            var now = time.GetUtcNow();
            link.LastReceivedSeq = report.Seq;
            var terminal = link.Terminal;
            if (terminal is null)
            {
                if (string.IsNullOrWhiteSpace(report.VehicleId))
                {
                    return "the first report names no vehicle. Confirming a terminal with a pairing code is not built yet, so every terminal is configured with its vehicle id.";
                }

                if (!terminals.TryGetValue(report.VehicleId, out terminal))
                {
                    terminal = new KnownTerminal(report.VehicleId, now);
                    terminal.Timer = time.CreateTimer(OnStaleTimer, terminal, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    terminals.Add(terminal.VehicleId, terminal);
                    Arm(terminal, now);
                }

                if (terminal.Link is { } previous && previous != link)
                {
                    replaced = previous;
                }

                terminal.Link = link;
                terminal.LastScreenText = link.LastScreenText;
                link.Terminal = terminal;
                events.Add((EventTypes.OnboardConnected, EventSources.Server, new
                {
                    terminal.VehicleId,
                    report.TerminalVersion,
                    Connection = link.Id,
                    Remote = link.RemoteAddress,
                }));
            }
            else if (!string.Equals(report.VehicleId, terminal.VehicleId, StringComparison.Ordinal))
            {
                LogVehicleChanged(logger, link.Id, terminal.VehicleId, report.VehicleId);
            }

            terminal.Version = report.TerminalVersion;
            terminal.IoModuleReadable = report.IoModuleReadable;
            if (link.WrittenNoEarlierThan(report.LastReceivedSeq) is { } written
                && now - written <= StaleAfter
                && (terminal.FreshWrittenAt is null || written > terminal.FreshWrittenAt))
            {
                terminal.FreshWrittenAt = written;
                if (terminal.Stale)
                {
                    terminal.Stale = false;
                    events.Add((EventTypes.OnboardFresh, EventSources.Server, new { terminal.VehicleId, Connection = link.Id }));
                }

                Arm(terminal, now);
            }

            foreach (var input in report.Inputs)
            {
                if (link.SeenInputs.Add(input.Id))
                {
                    events.Add((EventTypes.OnboardInputReceived, EventSources.OnboardTerminal, Describe(terminal, link, input)));
                }
            }
        }

        replaced?.Stop("replaced by a newer stream from the same vehicle");
        await RecordAsync(events, cancellationToken);
        return null;
    }

    /// <summary>The stream of <paramref name="link"/> has ended.</summary>
    public async Task CloseAsync(TerminalLink link, string reason)
    {
        ArgumentNullException.ThrowIfNull(link);
        string? vehicle;
        lock (gate)
        {
            vehicle = link.Terminal?.VehicleId;
            if (link.Terminal is { } terminal && terminal.Link == link)
            {
                terminal.Link = null;
            }
        }

        LogClosed(logger, link.Id, vehicle ?? "-", reason);
        if (vehicle is not null)
        {
            await RecordAsync(
                [(EventTypes.OnboardDisconnected, EventSources.Server, new { VehicleId = vehicle, Connection = link.Id, Reason = reason })],
                CancellationToken.None);
        }
    }

    public Task RecordRefusalAsync(string remoteAddress, string? major, string reason, CancellationToken cancellationToken) =>
        RecordAsync([(EventTypes.OnboardRefused, EventSources.Server, new { Remote = remoteAddress, ProtocolMajor = major, Reason = reason })], cancellationToken);

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
                    t.IoModuleReadable,
                    t.LastScreenText))
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
        }
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
        object? stale = null;
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
            stale = new
            {
                terminal.VehicleId,
                LastFreshReportWrittenNoEarlierThan = terminal.FreshWrittenAt,
                Connected = terminal.Link is not null,
            };
        }

        _ = RecordAsync([(EventTypes.OnboardStale, EventSources.Server, stale!)], CancellationToken.None);
    }

    /// <summary>Appends in order. A failure is logged, not thrown: it must not end a terminal's stream.</summary>
    private async Task RecordAsync(List<(string Type, string Source, object Data)> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<EventStore>();
            foreach (var (type, source, data) in events)
            {
                await store.AppendAsync(type, source, data, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogNotRecorded(logger, e, string.Join(", ", events.Select(e => e.Type)));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection} of vehicle {Vehicle} now reports vehicle {Reported}; it stays {Vehicle}")]
    private static partial void LogVehicleChanged(ILogger logger, string connection, string vehicle, string reported);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream {Connection} of vehicle {Vehicle} ended: {Reason}")]
    private static partial void LogClosed(ILogger logger, string connection, string vehicle, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Types} in the event record")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string types);
}

/// <summary>One stream from one terminal. Its sequence numbers start at 1 on every stream.</summary>
public sealed class TerminalLink(string id, string remoteAddress, DateTimeOffset openedAt) : IDisposable
{
    private readonly CancellationTokenSource stopped = new();

    public string Id { get; } = id;

    public string RemoteAddress { get; } = remoteAddress;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    /// <summary>Cancelled when the server ends this stream itself (see <see cref="StopReason"/>).</summary>
    public CancellationToken Stopped => stopped.Token;

    public string? StopReason { get; private set; }

    internal ulong LastSentSeq { get; set; }

    internal ulong LastReceivedSeq { get; set; }

    internal string? LastScreenText { get; set; }

    internal Queue<(ulong Seq, DateTimeOffset SentAt)> Sent { get; } = new();

    internal HashSet<ulong> SeenInputs { get; } = [];

    internal KnownTerminal? Terminal { get; set; }

    public void Dispose() => stopped.Dispose();

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

/// <summary>A terminal the server has had a report from, known by the vehicle it reports.</summary>
internal sealed class KnownTerminal(string vehicleId, DateTimeOffset firstSeenAt)
{
    public string VehicleId { get; } = vehicleId;

    public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

    public string Version { get; set; } = string.Empty;

    public bool IoModuleReadable { get; set; }

    public TerminalLink? Link { get; set; }

    /// <summary>The freshest report so far was written no earlier than this.</summary>
    public DateTimeOffset? FreshWrittenAt { get; set; }

    public bool Stale { get; set; }

    /// <summary>The screen text of the latest desired state sent to it, kept after its stream ends.</summary>
    public string? LastScreenText { get; set; }

    public ITimer? Timer { get; set; }
}
