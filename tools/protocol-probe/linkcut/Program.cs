using System.Globalization;
using System.Net;
using System.Net.Sockets;

// A TCP relay between the probe client and server on the development machine.
// Commands on standard input:
//   cut      nothing passes in either direction, nothing is closed, new
//            connections hang before reaching the server: a silent drop.
//   restore  traffic flows again, including whatever was held.
//   orphan   resets every current connection on the client side only; the
//            server side stays open and silent (the server still believes in
//            it), as after a short Wi-Fi drop where only the client noticed.
//   status | quit
// It only stands in for a firewall or a radio switch, which this machine
// cannot use on loopback.
var map = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2)
{
    map[args[i].TrimStart('-')] = args[i + 1];
}

var listen = IPEndPoint.Parse(map.GetValueOrDefault("listen", "127.0.0.1:6443"));
var target = IPEndPoint.Parse(map.GetValueOrDefault("target", "127.0.0.1:5443"));
if (!IPAddress.IsLoopback(listen.Address) || !IPAddress.IsLoopback(target.Address))
{
    Console.Error.WriteLine($"linkcut only relays between loopback addresses; got --listen {listen} --target {target}");
    return 2;
}

using var log = map.TryGetValue("log", out var logPath) ? new StreamWriter(logPath, append: true) { AutoFlush = true } : null;
var gate = new Gate();
var pairs = new List<Pair>();
var nextId = 0;

var writeLock = new Lock();

void Write(string text)
{
    lock (writeLock)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)} {text}";
        Console.WriteLine(line);
        log?.WriteLine(line);
    }
}

using var listener = new TcpListener(listen);
listener.Start();
Write($"LINKCUT listening {listen} -> {target}");
_ = Task.Run(AcceptAsync);

while (Console.ReadLine() is { } command)
{
    switch (command.Trim())
    {
        case "cut":
            gate.Close();
            Write("CUT all traffic held, nothing closed");
            break;
        case "restore":
            gate.Open();
            Write("RESTORE traffic flows");
            break;
        case "orphan":
            lock (pairs)
            {
                foreach (var pair in pairs.Where(p => !p.Orphaned))
                {
                    pair.Orphaned = true;
                    pair.Client.LingerState = new LingerOption(true, 0);
                    pair.Client.Close();
                    Write($"ORPHAN c{pair.Id}: client side reset, server side left open and silent");
                }
            }

            break;
        case "status":
            lock (pairs)
            {
                Write($"STATUS open={gate.IsOpen} pairs={string.Join(',', pairs.Select(p => $"c{p.Id}{(p.Orphaned ? "(orphaned)" : string.Empty)}"))}");
            }

            break;
        case "quit":
            return 0;
        default:
            Write($"UNKNOWN {command}");
            break;
    }
}

return 0;

async Task AcceptAsync()
{
    while (true)
    {
        var client = await listener.AcceptSocketAsync();
        _ = Task.Run(() => RelayAsync(client, Interlocked.Increment(ref nextId)));
    }
}

async Task RelayAsync(Socket client, int id)
{
    Write($"ACCEPT c{id} from {client.RemoteEndPoint}");
    var server = new Socket(SocketType.Stream, ProtocolType.Tcp);
    try
    {
        await gate.WaitAsync();
        await server.ConnectAsync(target);
    }
    catch (SocketException ex)
    {
        Write($"UPSTREAM-FAIL c{id} {ex.SocketErrorCode}");
        client.Close();
        server.Dispose();
        return;
    }

    var pair = new Pair(id, client, server);
    lock (pairs)
    {
        pairs.Add(pair);
    }

    await Task.WhenAll(PumpAsync(pair, client, server, "up"), PumpAsync(pair, server, client, "down"));
    lock (pairs)
    {
        pairs.Remove(pair);
    }

    if (!pair.Orphaned)
    {
        client.Dispose();
    }

    server.Dispose();
    Write($"CLOSED c{id}");
}

async Task PumpAsync(Pair pair, Socket from, Socket to, string direction)
{
    var buffer = new byte[16384];
    try
    {
        while (true)
        {
            var n = await from.ReceiveAsync(buffer);
            await gate.WaitAsync();
            if (pair.Orphaned)
            {
                if (n == 0 || direction == "up")
                {
                    break;
                }

                continue;
            }

            if (n == 0)
            {
                to.Shutdown(SocketShutdown.Send);
                break;
            }

            await to.SendAsync(buffer.AsMemory(0, n));
        }
    }
    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
    {
        Write($"PUMP-END c{pair.Id} {direction} {(ex as SocketException)?.SocketErrorCode.ToString() ?? "disposed"}");
        if (!pair.Orphaned || direction == "down")
        {
            to.Close();
        }
    }
}

internal sealed class Pair(int id, Socket client, Socket server)
{
    public int Id { get; } = id;
    public Socket Client { get; } = client;
    public Socket Server { get; } = server;
    public bool Orphaned { get; set; }
}

internal sealed class Gate
{
    private volatile TaskCompletionSource open = Opened();

    public bool IsOpen => open.Task.IsCompleted;

    public Task WaitAsync() => open.Task;

    public void Close()
    {
        if (IsOpen)
        {
            open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Open() => open.TrySetResult();

    private static TaskCompletionSource Opened()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
