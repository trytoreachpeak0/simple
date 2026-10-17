using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using FluentModbus;

namespace Simple.IoSimulator.Core;

/// <summary>A communication fault the simulated IO module shows its clients.</summary>
public enum ModbusFault
{
    /// <summary>Requests are answered.</summary>
    None,

    /// <summary>
    /// Requests are never answered; connections are not closed for it. A
    /// connection that sends nothing the library sees for its
    /// ConnectionTimeout (FluentModbus's default, one minute) is reset by
    /// the library all the same, and the client's end with it.
    /// </summary>
    NoResponse,

    /// <summary>Open connections are closed, and new ones are closed as soon as they are accepted.</summary>
    Disconnect,

    /// <summary>Every request is answered, late by the fault's delay.</summary>
    Delay,
}

/// <summary>
/// Where FluentModbus's server gets its connections from, with communication
/// faults put in between. The library has no hook for faults below Modbus:
/// its request validator can only answer with an exception code, and it runs
/// under the server's one lock, so holding it back would stall every reader
/// of the simulator. Its <see cref="ITcpClientProvider"/> extension point is
/// where connections come in, so this provider listens on the public
/// endpoint itself and hands the library the inner end of a loopback socket
/// pair for each client, moving bytes between the two ends. It knows nothing
/// about Modbus; a fault only drops, holds back or closes what it moves.
/// </summary>
internal sealed class ModbusFaultLink : ITcpClientProvider
{
    /// <summary>How long joining a client to the library may take before the client is dropped.</summary>
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener outer;
    private readonly TcpListener inner;
    private readonly Channel<TcpClient> forServer = Channel.CreateUnbounded<TcpClient>();
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly HashSet<Relay> relays = [];
    private readonly Task accepting;
    private ModbusFault fault;
    private TimeSpan delay;

    public ModbusFaultLink(IPEndPoint endpoint)
    {
        outer = new TcpListener(endpoint);
        inner = new TcpListener(IPAddress.Loopback, 0);
        outer.Start();
        inner.Start();
        accepting = Task.Run(AcceptAsync);
    }

    public IPEndPoint Endpoint => (IPEndPoint)outer.LocalEndpoint;

    /// <summary>Where the inner ends of the socket pairs connect; for tests.</summary>
    internal IPEndPoint InnerEndpoint => (IPEndPoint)inner.LocalEndpoint;

    public (ModbusFault Fault, TimeSpan Delay) Fault
    {
        get
        {
            lock (gate)
            {
                return (fault, delay);
            }
        }
    }

    public void SetFault(ModbusFault value, TimeSpan delayBy)
    {
        List<Relay> closing;
        lock (gate)
        {
            fault = value;
            delay = value == ModbusFault.Delay ? delayBy : TimeSpan.Zero;
            closing = value == ModbusFault.Disconnect ? [.. relays] : [];
        }

        foreach (var relay in closing)
        {
            relay.Close();
        }
    }

    public async Task<TcpClient> AcceptTcpClientAsync() => await forServer.Reader.ReadAsync(stopping.Token);

    public void Dispose()
    {
        stopping.Cancel();
        outer.Stop();
        inner.Stop();
        List<Relay> closing;
        lock (gate)
        {
            closing = [.. relays];
        }

        foreach (var relay in closing)
        {
            relay.Close();
        }

        try
        {
            accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was stopped under it.
        }

        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await outer.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (Fault.Fault == ModbusFault.Disconnect)
            {
                client.Dispose();
                continue;
            }

            var toServer = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            TcpClient? serverSide = null;
            client.NoDelay = true;
            try
            {
                using var pairing = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                pairing.CancelAfter(PairingTimeout);
                await toServer.ConnectAsync((IPEndPoint)inner.LocalEndpoint, pairing.Token);
                serverSide = await AcceptOwnAsync(toServer.Client.LocalEndPoint, pairing.Token);
                var relay = new Relay(this, client, toServer);
                bool refused;
                lock (gate)
                {
                    // Disconnect may have come while this client was being
                    // joined, after the check above and before SetFault
                    // could see it among the relays.
                    refused = fault == ModbusFault.Disconnect;
                    if (!refused)
                    {
                        relays.Add(relay);
                    }
                }

                if (refused)
                {
                    client.Dispose();
                    toServer.Dispose();
                    serverSide.Dispose();
                    continue;
                }

                await forServer.Writer.WriteAsync(serverSide, stopping.Token);
                relay.Start();
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                client.Dispose();
                toServer.Dispose();
                serverSide?.Dispose();
            }
        }
    }

    /// <summary>
    /// Accepts the inner end of the connection from <paramref name="own"/>.
    /// The inner listener is on loopback, so another program on this machine
    /// can connect to it too; such a connection is closed, never handed to
    /// the library in a client's place.
    /// </summary>
    private async Task<TcpClient> AcceptOwnAsync(EndPoint? own, CancellationToken cancellationToken)
    {
        while (true)
        {
            var accepted = await inner.AcceptTcpClientAsync(cancellationToken);
            if (Equals(accepted.Client.RemoteEndPoint, own))
            {
                return accepted;
            }

            accepted.Dispose();
        }
    }

    private void Remove(Relay relay)
    {
        lock (gate)
        {
            relays.Remove(relay);
        }
    }

    /// <summary>One client's connection: requests go from the client to the server, answers back.</summary>
    private sealed class Relay(ModbusFaultLink link, TcpClient client, TcpClient toServer)
    {
        private int closed;

        public void Start()
        {
            _ = Task.Run(() => MoveAsync(client, toServer, requests: true));
            _ = Task.Run(() => MoveAsync(toServer, client, requests: false));
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                client.Dispose();
                toServer.Dispose();
                link.Remove(this);
            }
        }

        private async Task MoveAsync(TcpClient from, TcpClient to, bool requests)
        {
            var buffer = new byte[1024];
            try
            {
                var source = from.GetStream();
                var target = to.GetStream();
                while (true)
                {
                    var read = await source.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    if (requests)
                    {
                        var (fault, delay) = link.Fault;
                        if (fault == ModbusFault.NoResponse)
                        {
                            continue;
                        }

                        if (fault == ModbusFault.Delay)
                        {
                            await Task.Delay(delay);
                        }
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Either end went away.
            }
            finally
            {
                Close();
            }
        }
    }
}
