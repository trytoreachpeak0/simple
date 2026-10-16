using Grpc.Core;
using Serilog;
using Simple.ProtocolProbe.V1;
using Status = Grpc.Core.Status;
using TerminalStatus = Simple.ProtocolProbe.V1.Status;

namespace Simple.ProtocolProbe.Server;

/// <summary>The <c>simple.probe.v1</c> duplex stream: one per terminal at a time.</summary>
internal sealed class ProbeV1Service(TerminalHub hub, ProbeServerOptions options) : Probe.ProbeBase
{
    private static readonly TimeSpan FirstReportWithin = TimeSpan.FromSeconds(15);

    public override async Task Connect(
        IAsyncStreamReader<Report> requestStream,
        IServerStreamWriter<Downstream> responseStream,
        ServerCallContext context)
    {
        var info = ConnectionInfo.From(context);
        Log.Information("CALL v1 conn={Conn} remote={Remote} http={Http} tls={Tls} cipher={Cipher} alpn={Alpn} key={Key} major={Major}",
            info.ConnectionId, info.Remote, info.HttpProtocol, info.TlsProtocol, info.CipherSuite, info.Alpn,
            info.Key is null ? "none" : TerminalHub.Short(info.Key), info.Major);
        if (info.Key is null)
        {
            Log.Warning("REFUSE conn={Conn} status=Unauthenticated detail=no client certificate", info.ConnectionId);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "no client certificate"));
        }

        Report first;
        using (var firstWait = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
        {
            firstWait.CancelAfter(FirstReportWithin);
            try
            {
                if (!await requestStream.MoveNext(firstWait.Token))
                {
                    Log.Information("CALL-END conn={Conn} before the first report", info.ConnectionId);
                    return;
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "no first report within 15 s"));
            }

            first = requestStream.Current;
        }

        var session = hub.Admit(info, first);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var writing = WriteAsync(session, responseStream, stop.Token);
        var commanding = CommandAsync(session, stop.Token);
        var reading = Task.CompletedTask;
        Status? killed = null;
        var reason = "client completed its stream";
        try
        {
            hub.OnReport(session, first);
            reading = ReadAsync(session, requestStream, stop.Token);
            if (await Task.WhenAny(reading, session.Killed) == session.Killed)
            {
                killed = await session.Killed;
                reason = $"terminated by server: {killed.Value.StatusCode} {killed.Value.Detail}";
            }
            else
            {
                await reading;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message} (callCancelled={context.CancellationToken.IsCancellationRequested})";
        }
        finally
        {
            await stop.CancelAsync();
            hub.Detach(session, reason);
            await Task.WhenAll(Quietly(reading), Quietly(writing), Quietly(commanding));
        }

        if (killed is { } status)
        {
            throw new RpcException(status);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            // The stream is already being torn down; the reason was logged.
        }
    }

    private async Task ReadAsync(TerminalHub.Session session, IAsyncStreamReader<Report> requestStream, CancellationToken token)
    {
        await foreach (var report in requestStream.ReadAllAsync(token))
        {
            hub.OnReport(session, report);
        }
    }

    private async Task WriteAsync(TerminalHub.Session session, IServerStreamWriter<Downstream> responseStream, CancellationToken token)
    {
        await foreach (var message in session.Outbox.ReadAllAsync(token))
        {
            await responseStream.WriteAsync(hub.Decorate(message), token);
            Log.Information("TX s{Session} {Kind} {Body}", session.Id, message.BodyCase, message.BodyCase == Downstream.BodyOneofCase.Command
                ? $"command={message.Command.Id}"
                : $"state={message.Status.State} code={message.Status.PairingCode} vehicle={message.Status.VehicleId}");
        }
    }

    private async Task CommandAsync(TerminalHub.Session session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(options.CommandInterval);
        while (await timer.WaitForNextTickAsync(token))
        {
            hub.SendCommandIfDue(session);
        }
    }
}

/// <summary>The <c>simple.probe.v2</c> stream: only shows that a second major is
/// served side by side. It accepts any client certificate and keeps no state.</summary>
internal sealed class ProbeV2Service : V2.Probe.ProbeBase
{
    public override async Task Connect(
        IAsyncStreamReader<V2.Report> requestStream,
        IServerStreamWriter<V2.Downstream> responseStream,
        ServerCallContext context)
    {
        var info = ConnectionInfo.From(context);
        Log.Information("CALL v2 conn={Conn} tls={Tls} cipher={Cipher} alpn={Alpn}", info.ConnectionId, info.TlsProtocol, info.CipherSuite, info.Alpn);
        await foreach (var report in requestStream.ReadAllAsync(context.CancellationToken))
        {
            Log.Information("RX v2 conn={Conn} seq={Seq} vehicleNumber={Vehicle}", info.ConnectionId, report.Seq, report.VehicleNumber);
            await responseStream.WriteAsync(new V2.Downstream { Message = $"v2 got {report.Seq}" }, context.CancellationToken);
        }
    }
}
