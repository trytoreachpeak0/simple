using System.Globalization;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Simple.Protocol;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core;

public enum TerminalState
{
    /// <summary>Not connected yet since the terminal started.</summary>
    Connecting,

    /// <summary>A stream is up and desired states arrive.</summary>
    Connected,

    /// <summary>No stream: the network, the server, or the server stopped.</summary>
    Disconnected,

    /// <summary>The server presented a certificate other than the pinned one; the terminal does not talk to it.</summary>
    ServerNotTrusted,

    /// <summary>The server does not serve this terminal's protocol major (ADR 0026).</summary>
    VersionMismatch,

    /// <summary>The server refused this terminal, for example for a missing vehicle id.</summary>
    Refused,
}

/// <param name="State">Where the connection stands.</param>
/// <param name="ScreenText">What the screen shows: the server's text while connected, otherwise why not.</param>
/// <param name="LastDesiredStateSeq">The latest desired state received on the current stream; 0 for none.</param>
public sealed record TerminalStatus(TerminalState State, string ScreenText, ulong LastDesiredStateSeq);

/// <summary>
/// The terminal's one stream to its server (ADR 0024, 0025). It sends a
/// report at once when the stream opens, when a desired state arrives and
/// when the person at the vehicle does something, and otherwise once a
/// second. The server is recognised by its certificate's pin only. When the
/// stream fails, the terminal reconnects through <see cref="ReconnectPolicy"/>.
/// </summary>
public sealed partial class TerminalConnection : IDisposable
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    /// <summary>A stream that lasted at least this long starts the retry delays afresh when it fails.</summary>
    public static readonly TimeSpan SettledAfter = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopping terminal waits for the server to end the stream it closed.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private const string DisconnectedText = "与服务端断开";

    private readonly TerminalSettings settings;
    private readonly OnboardInputs inputs;
    private readonly IoModuleReader? io;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly GrpcChannel channel;
    private readonly OnboardLink.OnboardLinkClient client;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock gate = new();
    private TerminalStatus status = new(TerminalState.Connecting, DisconnectedText, 0);
    private string? pinProblem;

    /// <param name="io">The IO module's reader, whose readings go into every report; without one, every report says the IO module cannot be read.</param>
    public TerminalConnection(TerminalSettings settings, OnboardInputs inputs, TimeProvider time, ILoggerFactory loggers, string? version = null, IoModuleReader? io = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(loggers);
        this.settings = settings;
        this.inputs = inputs;
        this.time = time;
        logger = loggers.CreateLogger<TerminalConnection>();
        Version = version
            ?? typeof(TerminalConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        inputs.Added += () => wake.Writer.TryWrite(true);
        this.io = io;
        if (io is not null)
        {
            // A reading that changed goes out at once (ADR 0024).
            io.Changed += () => wake.Writer.TryWrite(true);
        }

        var handler = new SocketsHttpHandler
        {
            // The terminal talks to its server directly on the plant network;
            // a proxy configured on the PC must not get in between (#18).
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // Only to notice a dead connection and reconnect. Whether the
            // terminal is cut off is the server's call, made from how fresh
            // the reports are (ADR 0024).
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => CheckPin(certificate),
            },
        };
        channel = GrpcChannel.ForAddress(settings.ServerUri, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            LoggerFactory = loggers,
        });
        client = new OnboardLink.OnboardLinkClient(channel);
    }

    public string Version { get; }

    public TerminalStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    /// <summary>Keeps a stream to the server until <paramref name="stopping"/>, reconnecting whenever it fails.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        LogStarting(logger, settings.VehicleId!, settings.Server, Version);
        var pipeline = ReconnectPolicy.Create(time, (attempt, delay, _) => LogRetry(logger, attempt, delay.TotalSeconds));
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                // A stream that settled and then ended returns normally, so the
                // next one starts with the shortest delay again.
                await pipeline.ExecuteAsync(SessionAsync, stopping);
            }
            catch (Exception e) when (stopping.IsCancellationRequested && e is OperationCanceledException or RpcException or IOException)
            {
                // A stream that failed as the stop came is not retried, and
                // its failure comes back here as it was (ReconnectPolicy). It
                // was logged when it happened; the stop is what ends the run (#55).
                break;
            }
        }

        SetStatus(TerminalState.Disconnected, DisconnectedText, 0);
    }

    public void Dispose() => channel.Dispose();

    private async ValueTask SessionAsync(CancellationToken stopping)
    {
        var started = time.GetTimestamp();
        var established = false;
        pinProblem = null;
        // abort ends the call outright; stopping first closes our half of the
        // stream and gives the server CloseGrace to end its half.
        using var abort = new CancellationTokenSource();
        using var grace = stopping.Register(() => abort.CancelAfter(CloseGrace));
        var headers = new Metadata { { ProtocolVersion.MajorHeader, ProtocolVersion.Major.ToString(CultureInfo.InvariantCulture) } };
        using var call = client.Connect(headers, cancellationToken: abort.Token);
        var session = new Session();
        var writing = WriteAsync(call.RequestStream, session, stopping, abort.Token);
        Exception? failure = null;
        try
        {
            await foreach (var state in call.ResponseStream.ReadAllAsync(abort.Token))
            {
                if (!established)
                {
                    established = true;
                    inputs.Open();
                    var after = time.GetElapsedTime(started);
                    LogConnected(logger, settings.Server, after.TotalMilliseconds);
                }

                session.Received(state.Seq);
                inputs.Acknowledge(state.LastReceivedSeq);
                SetStatus(TerminalState.Connected, state.Screen?.Text ?? string.Empty, state.Seq);
                // Answer at once, so that the report the server gets next names
                // the state it just sent, and the server's bound on the
                // report's age stays short (ADR 0024).
                wake.Writer.TryWrite(true);
            }

            if (!stopping.IsCancellationRequested)
            {
                LogEnded(logger, "the server ended the stream");
            }
        }
        catch (Exception e) when (stopping.IsCancellationRequested && e is RpcException or OperationCanceledException)
        {
            // Stopping, and the server did not end the stream within the grace.
        }
        catch (RpcException e)
        {
            failure = e;
            Classify(e);
        }
        finally
        {
            inputs.Close();
            await abort.CancelAsync();
            try
            {
                await writing;
            }
            catch (Exception e) when (e is OperationCanceledException or RpcException or InvalidOperationException or IOException)
            {
                // The stream is gone; why was logged above.
            }
        }

        if (stopping.IsCancellationRequested)
        {
            return;
        }

        if (failure is null)
        {
            SetStatus(TerminalState.Disconnected, DisconnectedText, 0);
        }

        // A refusal is retried with growing delays like any failure, so a
        // refused terminal never storms the server (ADR 0026).
        var settled = established && time.GetElapsedTime(started) >= SettledAfter;
        if (!settled || Status.State != TerminalState.Disconnected)
        {
            throw failure ?? new IOException("The server ended the stream before it settled.");
        }
    }

    private async Task WriteAsync(IClientStreamWriter<OnboardReport> stream, Session session, CancellationToken stopping, CancellationToken abort)
    {
        using var stoppingOrAbort = CancellationTokenSource.CreateLinkedTokenSource(stopping, abort);
        while (true)
        {
            var seq = session.NextReportSeq();
            var report = new OnboardReport
            {
                Seq = seq,
                LastReceivedSeq = session.LastReceivedSeq,
                TerminalVersion = Version,
                VehicleId = settings.VehicleId,
            };
            AddReading(report);
            report.Inputs.Add(inputs.ForReport(seq));
            await stream.WriteAsync(report, abort);

            using var interval = new CancellationTokenSource(ReportInterval, time);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(interval.Token, stoppingOrAbort.Token);
            try
            {
                await wake.Reader.WaitToReadAsync(waiting.Token);
                wake.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) when (interval.IsCancellationRequested && !stoppingOrAbort.IsCancellationRequested)
            {
                // A second without news: report anyway.
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested && !abort.IsCancellationRequested)
            {
                await stream.CompleteAsync();
                return;
            }
        }
    }

    private void AddReading(OnboardReport report)
    {
        if (io?.Read() is not { } reading)
        {
            return;
        }

        report.IoModuleReadable = reading.Readable;
        if (reading.SinceRead is { } sinceRead)
        {
            report.IoReadAgeMs = (ulong)sinceRead.TotalMilliseconds;
        }

        report.IoPoints.Add(reading.Points.Select(p => new IoPoint
        {
            Kind = p.Kind,
            Number = (uint)p.Number,
            Value = p.Value,
            Changes = p.Changes,
            MsSinceChange = (ulong)p.SinceChange.TotalMilliseconds,
        }));
    }

    private void Classify(RpcException e)
    {
        if (pinProblem is { } problem)
        {
            LogNotTrusted(logger, settings.Server, problem);
            SetStatus(TerminalState.ServerNotTrusted, "服务端证书不是本机记下的那一张，拒绝连接，请联系系统维护人员", 0);
            return;
        }

        switch (e.StatusCode)
        {
            case StatusCode.FailedPrecondition:
                var supported = (e.Trailers.GetValue(ProtocolVersion.SupportedMajorsTrailer) ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
                    .OfType<int>()
                    .ToList();
                LogVersionRefused(logger, ProtocolVersion.Major, string.Join(',', supported), e.Status.Detail);
                // Too old: the terminal needs updating. Newer than the server
                // (for example after the server was rolled back): someone has
                // to look at both, not update the terminal (ADR 0026).
                SetStatus(
                    TerminalState.VersionMismatch,
                    supported.Count > 0 && ProtocolVersion.Major < supported.Min()
                        ? "车载终端版本需要更新，请联系系统维护人员"
                        : "车载终端与服务端版本不一致，请联系系统维护人员",
                    0);
                break;
            case StatusCode.InvalidArgument or StatusCode.Unauthenticated or StatusCode.PermissionDenied:
                var refusal = e.StatusCode.ToString();
                LogRefused(logger, refusal, e.Status.Detail);
                SetStatus(TerminalState.Refused, "服务端拒绝了本机，请联系系统维护人员", 0);
                break;
            default:
                var code = e.StatusCode.ToString();
                LogDisconnected(logger, code, e.Status.Detail, e.Status.DebugException?.Message ?? "-");
                SetStatus(TerminalState.Disconnected, DisconnectedText, 0);
                break;
        }
    }

    private bool CheckPin(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            pinProblem = "the server presented no certificate";
            return false;
        }

        using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var pin = CertificatePin.Of(presented);
        if (CertificatePin.Matches(settings.ServerCertificatePin!, pin))
        {
            return true;
        }

        pinProblem = $"its certificate is not the pinned one (pinned {settings.ServerCertificatePin}, presented {pin}, subject {presented.Subject})";
        return false;
    }

    private void SetStatus(TerminalState state, string screenText, ulong seq)
    {
        TerminalStatus previous;
        lock (gate)
        {
            previous = status;
            status = new TerminalStatus(state, screenText, seq);
        }

        if (previous.State != state || previous.ScreenText != screenText)
        {
            LogState(logger, state, screenText);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Onboard terminal for vehicle {Vehicle}, server {Server}, version {Version}")]
    private static partial void LogStarting(ILogger logger, string vehicle, string server, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Server} after {Milliseconds:F0} ms")]
    private static partial void LogConnected(ILogger logger, string server, double milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream ended: {Reason}")]
    private static partial void LogEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refusing the server at {Server}: {Problem}")]
    private static partial void LogNotTrusted(ILogger logger, string server, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server does not serve protocol major {Major} (it serves {Supported}): {Detail}")]
    private static partial void LogVersionRefused(ILogger logger, int major, string supported, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused this terminal ({Code}): {Detail}")]
    private static partial void LogRefused(ILogger logger, string code, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnected ({Code}): {Detail} {Debug}")]
    private static partial void LogDisconnected(ILogger logger, string code, string detail, string debug);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retry {Attempt} in {Seconds:F1} s")]
    private static partial void LogRetry(ILogger logger, int attempt, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "State {State}: {ScreenText}")]
    private static partial void LogState(ILogger logger, TerminalState state, string screenText);

    /// <summary>Sequence numbers of one stream: both start afresh on every stream.</summary>
    private sealed class Session
    {
        private ulong lastSent;
        private ulong lastReceived;

        public ulong LastReceivedSeq => Interlocked.Read(ref lastReceived);

        public ulong NextReportSeq() => ++lastSent;

        public void Received(ulong seq) => Interlocked.Exchange(ref lastReceived, seq);
    }
}
