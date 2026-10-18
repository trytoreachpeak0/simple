using System.Globalization;
using Grpc.Core;
using Simple.Protocol;
using Simple.Protocol.V1;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// The server's end of a terminal's stream (ADR 0024): a desired state at
/// once, then once a second; every report goes to the registry. The stream
/// ends when the terminal ends it, the connection is lost, a newer stream
/// with the same key replaces it, or the server stops.
/// </summary>
public sealed partial class OnboardLinkService(
    OnboardTerminalRegistry terminals,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<OnboardLinkService> logger) : OnboardLink.OnboardLinkBase
{
    /// <summary>The majors this server serves (ADR 0026): the current one, and later also the one before it.</summary>
    public static readonly IReadOnlyList<int> SupportedMajors = [ProtocolVersion.Major];

    public static readonly TimeSpan DesiredStateInterval = TimeSpan.FromSeconds(1);

    public override async Task Connect(
        IAsyncStreamReader<OnboardReport> requestStream,
        IServerStreamWriter<DesiredState> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var http = context.GetHttpContext();
        var remote = $"{http.Connection.RemoteIpAddress}:{http.Connection.RemotePort.ToString(CultureInfo.InvariantCulture)}";
        var header = context.RequestHeaders.GetValue(ProtocolVersion.MajorHeader);
        if (!int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !SupportedMajors.Contains(major))
        {
            var reason = header is null
                ? $"the request names no protocol major ({ProtocolVersion.MajorHeader}); this server serves {string.Join(',', SupportedMajors)}"
                : $"protocol major {header} is not served; this server serves {string.Join(',', SupportedMajors)}";
            LogRefused(logger, remote, reason);
            terminals.RecordRefusal(remote, header, reason);
            throw new RpcException(
                new Grpc.Core.Status(StatusCode.FailedPrecondition, reason),
                new Metadata { { ProtocolVersion.SupportedMajorsTrailer, string.Join(',', SupportedMajors) } });
        }

        // The terminal endpoint requires a certificate (OnboardEndpoint), so
        // there is one; a stream without one never reaches here.
        if (http.Connection.ClientCertificate is not { } certificate)
        {
            throw new RpcException(new Grpc.Core.Status(StatusCode.Unauthenticated, "the terminal presented no certificate."));
        }

        using var link = terminals.Open(remote, CertificatePin.Of(certificate));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping, link.Stopped);
        var sending = SendAsync(link, responseStream, stop.Token);
        var closeReason = "the terminal ended the stream";
        string? refusal = null;
        try
        {
            await foreach (var report in requestStream.ReadAllAsync(stop.Token))
            {
                refusal = terminals.Receive(link, report);
                if (refusal is not null)
                {
                    closeReason = $"refused: {refusal}";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            closeReason = link.StopReason
                ?? (lifetime.ApplicationStopping.IsCancellationRequested ? "the server is stopping" : "the connection was lost");
        }
        catch (IOException e)
        {
            closeReason = $"the connection was lost: {e.Message}";
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await sending;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or RpcException)
            {
                // The stream is gone; why is recorded below.
            }

            terminals.Close(link, closeReason);
        }

        if (refusal is not null)
        {
            terminals.RecordRefusal(remote, header, refusal);
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, refusal));
        }
    }

    /// <summary>A desired state once a second, and at once when it changes (ADR 0024), for example when the terminal's key is confirmed.</summary>
    private async Task SendAsync(TerminalLink link, IServerStreamWriter<DesiredState> responseStream, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DesiredStateInterval, time);
        Task<bool>? tick = null;
        // A write with a cancelled token resets the stream (gRPC aborts the
        // call), so a stopping stream never gets one: the terminal's own end
        // of the stream, or the refusal, closes it as it should.
        while (!cancellationToken.IsCancellationRequested)
        {
            var changed = link.Changed;
            await responseStream.WriteAsync(terminals.NextDesiredState(link), cancellationToken);
            tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
            // The tick also ends the wait when the stream stops: it is cancelled.
            if (await Task.WhenAny(tick, changed) == tick)
            {
                if (!await tick)
                {
                    return;
                }

                tick = null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a terminal stream from {Remote}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string remote, string reason);
}
