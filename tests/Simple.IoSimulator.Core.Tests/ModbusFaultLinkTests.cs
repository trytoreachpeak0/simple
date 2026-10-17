using System.Net;
using System.Net.Sockets;

namespace Simple.IoSimulator.Core.Tests;

/// <summary>The relay between a client and FluentModbus, below Modbus.</summary>
public sealed class ModbusFaultLinkTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnotherProgramConnectingToTheInnerPortIsNeverHandedOverInAClientsPlace()
    {
        using var link = new ModbusFaultLink(new IPEndPoint(IPAddress.Loopback, 0));
        using var stray = new TcpClient();
        await stray.ConnectAsync(link.InnerEndpoint, Cancel);
        using var client = new TcpClient();
        await client.ConnectAsync(link.Endpoint, Cancel);

        using var serverSide = await link.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5), Cancel);

        // By port: a TcpClient's socket is dual-mode, so its address reads
        // as ::ffff:127.0.0.1 where the inner listener sees 127.0.0.1.
        Assert.NotEqual(((IPEndPoint)stray.Client.LocalEndPoint!).Port, ((IPEndPoint)serverSide.Client.RemoteEndPoint!).Port);
        await client.GetStream().WriteAsync(new byte[] { 1, 2, 3 }, Cancel);
        var received = new byte[3];
        await serverSide.GetStream().ReadExactlyAsync(received, Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Cancel);
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }
}
