using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Simple.Cli;

/// <summary>
/// Chooses the loopback ports of one instance. Every instance has a preferred
/// block derived from its name, so it gets the same ports on every <c>up</c>;
/// a block with a port in use or claimed by another instance is passed over
/// for the next one. The range stays below the Windows dynamic port range
/// (49152 and up).
/// </summary>
public static class PortAllocator
{
    public const int First = 20000;
    public const int BlockSize = 20;
    public const int Blocks = 500;

    public static IReadOnlyList<int> Allocate(string instance, int count, Func<int, bool> isFree)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(isFree);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, BlockSize * Blocks);
        if (count == 0)
        {
            return [];
        }

        var preferred = PreferredBlock(instance);
        for (var i = 0; i < Blocks; i++)
        {
            var start = First + ((preferred + i) % Blocks * BlockSize);
            if (start + count > First + (BlockSize * Blocks))
            {
                continue;
            }

            var ports = Enumerable.Range(start, count).ToList();
            if (ports.All(isFree))
            {
                return ports;
            }
        }

        throw new InvalidOperationException(
            $"No {count} consecutive free ports between {First} and {First + (BlockSize * Blocks) - 1}.");
    }

    /// <summary>A stable hash (FNV-1a) of the instance name; string.GetHashCode differs from run to run.</summary>
    public static int PreferredBlock(string instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(instance.ToUpperInvariant()))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash % Blocks);
    }

    /// <summary>True when nothing listens on the loopback port and it can be bound now.</summary>
    public static bool CanBind(int port)
    {
        try
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.ExclusiveAddressUse = true;
            listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
