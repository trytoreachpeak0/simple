using System.Globalization;
using Grpc.Core;
using Simple.Protocol;
using Simple.Protocol.V1;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// The server's end of a terminal's stream (ADR 0024): a desired state at
/// once, then once a second; every report goes to the registry. The stream
/// ends when the terminal ends it, the connection is lost, a newer stream
/// from the same vehicle replaces it, or the server stops.
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
            await terminals.RecordRefusalAsync(remote, header, reason, context.CancellationToken);
            throw new RpcException(
                new Grpc.Core.Status(StatusCode.FailedPrecondition, reason),
                new Metadata { { ProtocolVersion.SupportedMajorsTrailer, string.Join(',', SupportedMajors) } });
        }

        using var link = terminals.Open(remote);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping, link.Stopped);
        var sending = SendAsync(link, responseStream, stop.Token);
        var closeReason = "the terminal ended the stream";
        string? refusal = null;
        try
        {
            await foreach (var report in requestStream.ReadAllAsync(stop.Token))
            {
                refusal = await terminals.ReceiveAsync(link, report, stop.Token);
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

            await terminals.CloseAsync(link, closeReason);
        }

        if (refusal is not null)
        {
            await terminals.RecordRefusalAsync(remote, header, refusal, CancellationToken.None);
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, refusal));
        }
    }

    private async Task SendAsync(TerminalLink link, IServerStreamWriter<DesiredState> responseStream, CancellationToken cancellationToken)
    {
        // Nothing on the screen changes in this version, so only the
        // once-a-second state is sent; a change will also send at once.
        using var timer = new PeriodicTimer(DesiredStateInterval, time);
        do
        {
            await responseStream.WriteAsync(terminals.NextDesiredState(link), cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a terminal stream from {Remote}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string remote, string reason);
}
