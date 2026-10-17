using System.Threading.Channels;
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
///
/// Events go into the event record one at a time, in the order the registry
/// decided them (under its lock), through one writer: a stale terminal's
/// fresh-again never lands before its stale, a replaced stream's disconnect
/// never after the new stream's connect.
/// </remarks>
public sealed partial class OnboardTerminalRegistry : IDisposable
{
    /// <summary>ADR 0009: a reading older than this is not a reading.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Limits on what one report may bring in. Until terminals have keys
    /// (ADR 0025, pairing), anyone on the plant network can open a stream, so
    /// these keep a stranger from filling memory and the event record with
    /// one report. A vehicle id over the limit ends the stream; an input
    /// over a limit is skipped, as ADR 0024 skips any item it cannot use.
    /// </summary>
    public const int MaxVehicleIdLength = 64;

    public const int MaxInputsPerReport = 16;

    public const int MaxInputTextLength = 256;

    /// <summary>A vehicle's IO module has 16 inputs and 16 outputs; these leave room for a larger one.</summary>
    public const int MaxIoPointsPerReport = 128;

    public const int MaxIoPointNumber = 64;

    /// <summary>What the screen shows in this version, where the server has nothing else to say yet.</summary>
    public const string ConnectedScreenText = "已连接服务端";

    private readonly TimeProvider time;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<OnboardTerminalRegistry> logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, KnownTerminal> terminals = new(StringComparer.Ordinal);
    private readonly Channel<Pending> pending = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writing;
    private bool disposed;

    public OnboardTerminalRegistry(TimeProvider time, IServiceScopeFactory scopes, ILogger<OnboardTerminalRegistry> logger)
    {
        this.time = time;
        this.scopes = scopes;
        this.logger = logger;
        writing = Task.Run(WriteAsync);
    }

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
    public string? Receive(TerminalLink link, OnboardReport report)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(report);
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

                if (report.VehicleId.Length > MaxVehicleIdLength)
                {
                    return $"the vehicle id is longer than {MaxVehicleIdLength} characters.";
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
                Record(EventTypes.OnboardConnected, EventSources.Server, new
                {
                    terminal.VehicleId,
                    report.TerminalVersion,
                    Connection = link.Id,
                    Remote = link.RemoteAddress,
                });
            }
            else if (!string.Equals(report.VehicleId, terminal.VehicleId, StringComparison.Ordinal))
            {
                LogVehicleChanged(logger, link.Id, terminal.VehicleId, report.VehicleId);
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
            if (link.WrittenNoEarlierThan(report.LastReceivedSeq) is { } written
                && now - written <= StaleAfter
                && (terminal.FreshWrittenAt is null || written > terminal.FreshWrittenAt))
            {
                terminal.FreshWrittenAt = written;
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

        replaced?.Stop("replaced by a newer stream from the same vehicle");
        return null;
    }

    /// <summary>The stream of <paramref name="link"/> has ended.</summary>
    public void Close(TerminalLink link, string reason)
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

            if (vehicle is not null)
            {
                Record(EventTypes.OnboardDisconnected, EventSources.Server, new { VehicleId = vehicle, Connection = link.Id, Reason = reason });
            }
        }

        LogClosed(logger, link.Id, vehicle ?? "-", reason);
    }

    public void RecordRefusal(string remoteAddress, string? major, string reason)
    {
        lock (gate)
        {
            Record(EventTypes.OnboardRefused, EventSources.Server, new { Remote = remoteAddress, ProtocolMajor = major, Reason = reason });
        }
    }

    /// <summary>Waits until every event decided so far is in the event record (or failed to get there and was logged).</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!pending.Writer.TryWrite(new Pending(null, null, null, done)))
            {
                return Task.CompletedTask;
            }
        }

        return done.Task.WaitAsync(cancellationToken);
    }

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
                    t.Io))
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

            pending.Writer.TryComplete();
        }

        // What is queued still gets written; the database outlives this.
        writing.Wait(TimeSpan.FromSeconds(10));
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
        pending.Writer.TryWrite(new Pending(type, source, data, null));

    /// <summary>
    /// The one writer. A failure is logged, not thrown: the event record
    /// being unreachable must not end a terminal's stream.
    /// </summary>
    private async Task WriteAsync()
    {
        await foreach (var item in pending.Reader.ReadAllAsync())
        {
            if (item.Flushed is { } flushed)
            {
                flushed.TrySetResult();
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(item.Type!, item.Source!, item.Data, CancellationToken.None);
            }
            catch (Exception e)
            {
                LogNotRecorded(logger, e, item.Type!);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection} of vehicle {Vehicle} now reports vehicle {Reported}; it stays {Vehicle}")]
    private static partial void LogVehicleChanged(ILogger logger, string connection, string vehicle, string reported);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream {Connection} of vehicle {Vehicle} ended: {Reason}")]
    private static partial void LogClosed(ILogger logger, string connection, string vehicle, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Type} in the event record")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection}: skipped {Count} IO point(s): of unknown kind, numbered outside 1-{MaxNumber}, or beyond {MaxCount} in one report")]
    private static partial void LogIoPointsSkipped(ILogger logger, string connection, int count, int maxCount, int maxNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Connection}: skipped {Count} onboard input(s), {Why}")]
    private static partial void LogInputsSkipped(ILogger logger, string connection, int count, string why);

    /// <summary>An event to append, or (with <c>Flushed</c>) a marker that completes once everything before it is written.</summary>
    private sealed record Pending(string? Type, string? Source, object? Data, TaskCompletionSource? Flushed);
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
