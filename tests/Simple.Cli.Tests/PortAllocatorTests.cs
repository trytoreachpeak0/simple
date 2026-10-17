namespace Simple.Cli.Tests;

public sealed class PortAllocatorTests
{
    [Fact]
    public void AnInstanceGetsTheSamePortsEveryTime()
    {
        var first = PortAllocator.Allocate("repo", 3, _ => true);
        var second = PortAllocator.Allocate("REPO", 3, _ => true);

        // The preferred block comes from a stable hash of the name, not from
        // string.GetHashCode, which differs from one process to the next.
        Assert.Equal([23460, 23461, 23462], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TwoInstancesPreferDifferentBlocks()
    {
        var repo = PortAllocator.Allocate("repo", 1, _ => true);
        var worktree = PortAllocator.Allocate("29-simple-cli", 1, _ => true);

        Assert.Equal([24200], worktree);
        Assert.NotEqual(repo, worktree);
    }

    [Fact]
    public void ABlockWithABusyPortIsPassedOverForTheNext()
    {
        var ports = PortAllocator.Allocate("repo", 3, port => port != 23461);

        Assert.Equal([23480, 23481, 23482], ports);
    }

    [Fact]
    public void TheSearchWrapsAroundWithinTheRange()
    {
        // Only the first block is free; "repo" prefers block 173, so the search
        // runs to the end of the range and wraps around to block 0.
        var ports = PortAllocator.Allocate("repo", 2, port => port < PortAllocator.First + PortAllocator.BlockSize);

        Assert.Equal([PortAllocator.First, PortAllocator.First + 1], ports);
    }

    [Fact]
    public void PortsStayBelowTheDynamicRange()
    {
        var everyPort = PortAllocator.Allocate("repo", PortAllocator.BlockSize * PortAllocator.Blocks, _ => true);

        Assert.Equal(PortAllocator.First, everyPort[0]);
        Assert.True(everyPort[^1] < 49152);
    }

    [Fact]
    public void NoFreeBlockIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => PortAllocator.Allocate("repo", 1, _ => false));
    }

    [Fact]
    public void ABoundPortIsNotFree()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortAllocator.CanBind(port));
    }
}
