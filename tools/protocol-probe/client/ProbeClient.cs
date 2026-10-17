using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Serilog;
using Simple.ProtocolProbe.V1;
using Status = Simple.ProtocolProbe.V1.Status;

namespace Simple.ProtocolProbe.Client;

internal enum TerminalState
{
    Connecting,
    Pending,
    Connected,
    Disconnected,
    VersionMismatch,
    Refused,
    ServerNotTrusted,
}

/// <summary>
/// One duplex stream to the server at a time: a report every interval (1 s by
/// default), command ids echoed in the next report, and reconnects with
/// jittered exponential backoff from 1 s to 30 s (Polly). Keepalive and
/// connect timeouts are SocketsHttpHandler's; nothing here frames or pings.
/// </summary>
internal sealed class ProbeClient : IDisposable
{
    private static readonly TimeSpan ShortSession = TimeSpan.FromSeconds(5);

    private readonly ClientOptions options;
    private readonly TerminalIdentity identity;
    private readonly GrpcChannel channel;
    private readonly Probe.ProbeClient stub;
    private readonly string instanceId = Guid.NewGuid().ToString("N")[..12];
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<ulong> receivedCommands = new();
    private ulong seq;
    private TerminalState state = TerminalState.Connecting;
    private string? pinProblem;

    public ProbeClient(ClientOptions options, TerminalIdentity identity, ILoggerFactory loggerFactory)
    {
        this.options = options;
        this.identity = identity;
        ReportInterval = options.ReportInterval;
        Extra = options.MinorExtra;
        var handler = new SocketsHttpHandler
        {
            // The terminal talks to the server directly on the plant network.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingDelay = options.KeepAlivePingDelay,
            KeepAlivePingTimeout = options.KeepAlivePingTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            SslOptions = new SslClientAuthenticationOptions
            {
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => CheckPin(certificate),
            },
        };
        channel = GrpcChannel.ForAddress(options.ServerUrl, new GrpcChannelOptions
        {
            HttpHandler = handler,
            LoggerFactory = loggerFactory,
            DisposeHttpClient = true,
        });
        stub = new Probe.ProbeClient(channel);
        Log.Information("CLIENT instance={Instance} server={Server} major={Major} interval={Interval}s keepAlive={Ping}/{PingTimeout} connectTimeout=10s backoff=1s..30s jittered",
            instanceId, options.ServerUrl, options.Major, ReportInterval.TotalSeconds, options.KeepAlivePingDelay, options.KeepAlivePingTimeout);
    }

    public TimeSpan ReportInterval { get; set; }

    public bool Extra { get; set; }

    public TerminalState State => state;

    public async Task RunAsync(CancellationToken token)
    {
        var pipeline = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is not null && !args.Context.CancellationToken.IsCancellationRequested),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            MaxRetryAttempts = int.MaxValue,
            OnRetry = args =>
            {
                Log.Information("RETRY attempt={Attempt} in {Delay:F0} ms", args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
                return default;
            },
        }).Build();

        while (!token.IsCancellationRequested)
        {
            try
            {
                // A stream that was up for a while and then broke returns
                // normally, so the next attempt starts a fresh backoff series.
                await pipeline.ExecuteAsync(async t => await SessionAsync(t), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void Dispose() => channel.Dispose();

    private async Task SessionAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var established = false;
        pinProblem = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var headers = new Metadata { { "simple-protocol-major", options.Major }, { "simple-protocol-minor", "1" } };
        Log.Information("CONNECT starting a stream to {Server}", options.ServerUrl);
        using var call = stub.Connect(headers, cancellationToken: stop.Token);
        var writing = WriteAsync(call.RequestStream, stop.Token);
        try
        {
            await foreach (var message in call.ResponseStream.ReadAllAsync(stop.Token))
            {
                if (!established)
                {
                    established = true;
                    Log.Information("STREAM up after {Ms:F0} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                Handle(message);
            }

            throw new IOException("server ended the stream without a status");
        }
        catch (RpcException ex)
        {
            var lasted = Stopwatch.GetElapsedTime(started);
            Log.Warning("STREAM-END after {Lasted:F1}s status={Code} detail={Detail} debug={Debug}",
                lasted.TotalSeconds, ex.StatusCode, ex.Status.Detail, ex.Status.DebugException?.Message ?? "-");
            Classify(ex);
            if (established && lasted >= ShortSession && ex.StatusCode is not (StatusCode.PermissionDenied or StatusCode.Unauthenticated or StatusCode.FailedPrecondition))
            {
                return;
            }

            throw;
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await writing;
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException or InvalidOperationException or IOException)
            {
                // The stream is gone; the reason was logged above.
            }
        }
    }

    private async Task WriteAsync(IClientStreamWriter<Report> requestStream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var echo = new List<ulong>();
            while (receivedCommands.TryDequeue(out var id))
            {
                echo.Add(id);
            }

            var report = new Report
            {
                Seq = ++seq,
                ElapsedMs = (ulong)Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                InstanceId = instanceId,
                VehicleId = options.ClaimVehicle ?? identity.File.VehicleId ?? string.Empty,
                ClientVersion = "probe-1",
                ProtocolMinor = 1,
            };
            report.ReceivedCommandIds.Add(echo);
            if (Extra)
            {
                report.IoModuleNote = "1.1-client field the 1.0 server does not know";
            }

            try
            {
                await requestStream.WriteAsync(report, token);
            }
            catch
            {
                foreach (var id in echo)
                {
                    receivedCommands.Enqueue(id);
                }

                throw;
            }

            Log.Information("TX seq={Seq} echo=[{Echo}] extra={Extra}", report.Seq, string.Join(',', echo), Extra);
            await Task.Delay(ReportInterval, token);
        }
    }

    private void Handle(Downstream message)
    {
        var unknown = UnknownFields.Of(message);
        switch (message.BodyCase)
        {
            case Downstream.BodyOneofCase.Command:
                receivedCommands.Enqueue(message.Command.Id);
                Log.Information("RX command={Id} unknownFields=[{Unknown}]", message.Command.Id, unknown);
                break;
            case Downstream.BodyOneofCase.Status when message.Status.State == Status.Types.State.Pending:
                Log.Information("RX status=Pending code={Code} unknownFields=[{Unknown}]", message.Status.PairingCode, unknown);
                SetState(TerminalState.Pending, $"等待在控制台确认这是哪台车，配对码 {message.Status.PairingCode}");
                break;
            case Downstream.BodyOneofCase.Status when message.Status.State == Status.Types.State.Accepted:
                Log.Information("RX status=Accepted vehicle={Vehicle} unknownFields=[{Unknown}]", message.Status.VehicleId, unknown);
                if (identity.File.VehicleId != message.Status.VehicleId)
                {
                    identity.SaveVehicle(message.Status.VehicleId);
                }

                SetState(TerminalState.Connected, $"已连接，本车 {message.Status.VehicleId}");
                break;
            default:
                Log.Warning("RX unrecognised message {Case} unknownFields=[{Unknown}]", message.BodyCase, unknown);
                break;
        }
    }

    private void Classify(RpcException ex)
    {
        if (pinProblem is not null)
        {
            SetState(TerminalState.ServerNotTrusted, $"服务端证书不是钉住的那一张，拒绝连接（{pinProblem}）");
            return;
        }

        switch (ex.StatusCode)
        {
            case StatusCode.FailedPrecondition:
                var supported = ex.Trailers.GetValue("simple-supported-majors") ?? "?";
                var lowest = supported.Split(',').Select(s => int.TryParse(s, out var n) ? n : int.MaxValue).Min();
                var mine = int.TryParse(options.Major, out var m) ? m : 0;
                SetState(TerminalState.VersionMismatch, mine < lowest
                    ? $"车载终端版本需要更新，请联系系统维护人员（本机主版本 {options.Major}，服务端支持 {supported}）"
                    : $"车载终端与服务端版本不一致，请联系系统维护人员（本机主版本 {options.Major}，服务端支持 {supported}）");
                break;
            case StatusCode.Unauthenticated:
                // The server does not hold this key for the vehicle we claimed
                // (for example it was forgotten): drop the stored vehicle so the
                // next stream asks to be confirmed again.
                if (identity.File.VehicleId is not null && options.ClaimVehicle is null)
                {
                    Log.Warning("IDENTITY server does not know this key as {Vehicle}; forgetting the stored vehicle", identity.File.VehicleId);
                    identity.SaveVehicle(null);
                }

                SetState(TerminalState.Refused, $"服务端拒绝了本机：{ex.StatusCode} {ex.Status.Detail}");
                break;
            case StatusCode.PermissionDenied:
                SetState(TerminalState.Refused, $"服务端拒绝了本机：{ex.StatusCode} {ex.Status.Detail}");
                break;
            default:
                SetState(TerminalState.Disconnected, "与服务端断开");
                break;
        }
    }

    private bool CheckPin(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            pinProblem = "服务端没有出示证书";
            return false;
        }

        using var cert = new X509Certificate2(certificate);
        var pin = Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        if (pin == options.ServerPin)
        {
            return true;
        }

        pinProblem = $"expected pin {options.ServerPin[..12]}…, server presented {pin[..12]}…";
        Log.Warning("PIN-MISMATCH expected={Expected} presented={Presented} subject={Subject}", options.ServerPin, pin, cert.Subject);
        return false;
    }

    private void SetState(TerminalState next, string text)
    {
        if (next == state && next != TerminalState.Pending)
        {
            return;
        }

        state = next;
        Log.Information("STATE {State} {Text}", next, text);
    }
}

/// <summary>Field numbers in a message that this side's schema does not know.</summary>
internal static class UnknownFields
{
    public static string Of(IMessage message)
    {
        var numbers = new List<int>();
        var input = new CodedInputStream(message.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            if (message.Descriptor.FindFieldByNumber(number) is null)
            {
                numbers.Add(number);
            }

            input.SkipLastField();
        }

        return string.Join(',', numbers);
    }
}
