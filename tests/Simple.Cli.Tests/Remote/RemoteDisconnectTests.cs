using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// When the laptop's end goes away (ssh killed, Ctrl+C, the network lost),
/// the remote side ends the run with everything it started and frees the
/// instance: sshd on the remote machine does neither on its own.
/// </summary>
public sealed class RemoteDisconnectTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The command starts a child of its own, writes both process ids where
    // the test finds them, and sleeps.
    private const string SleepWithAChild =
        "$child = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -PassThru -NoNewWindow; "
        + "Set-Content (Join-Path $env:SIMPLE_REMOTE_RUN 'pids.txt') \"$PID $($child.Id)\"; Start-Sleep 120";

    [Fact]
    public async Task CuttingTheConnectionEndsTheRunWithItsChildrenAndFreesTheInstance()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        int[] pids = [];
        try
        {
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            var running = Run(sandbox, machine).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", SleepWithAChild], json: false, cut.Token);
            pids = await PidsAsync(sandbox, running);

            await cut.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

            Assert.True(await GoneWithinAsync(pids, TimeSpan.FromSeconds(15)), $"Still running 15 s after the cut: {string.Join(", ", pids.Where(Alive))}");
            var next = await Run(sandbox, machine).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", "'next'"], json: false, Cancel);
            Assert.True(next.ExitCode == ExitCodes.Success, $"{next.ExitCode}: {next.Error}");
        }
        finally
        {
            CleanUp(machine, pids);
        }
    }

    [Fact]
    public async Task ASilentConnectionEndsTheRunAfterTheAliveTimeout()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        int[] pids = [];
        try
        {
            var running = Run(sandbox, machine, alive: TimeSpan.FromSeconds(3)).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", SleepWithAChild], json: false, Cancel);
            pids = await PidsAsync(sandbox, running);

            machine.GoSilent();
            var watch = Stopwatch.StartNew();
            var outcome = await running;

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The run went on for {watch.Elapsed.TotalSeconds} s without heartbeats.");
            Assert.Equal(ExitCodes.RemoteFailed, outcome.ExitCode);
            Assert.True(await GoneWithinAsync(pids, TimeSpan.FromSeconds(5)), $"Still running: {string.Join(", ", pids.Where(Alive))}");
        }
        finally
        {
            CleanUp(machine, pids);
        }
    }

    [Fact]
    public async Task ASilentConnectionEndsAFetchThatHangs()
    {
        // Before the command starts, the remote side may fetch from the
        // origin (or from a large bundle) for minutes; a cut connection must
        // end that as well. The origin here accepts the connection and never
        // answers, so the fetch would wait forever.
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        // The snapshot is HEAD, which is on origin/main: nothing is sent, and
        // the remote side, which lacks it, fetches it from the origin.
        await sandbox.GitAsync(Cancel, "remote", "set-url", "origin", $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/simple.git");
        try
        {
            var running = Run(sandbox, machine, alive: TimeSpan.FromSeconds(3)).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", "'must not run'"], json: false, Cancel);
            using var fetching = await origin.AcceptTcpClientAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Cancel);

            machine.GoSilent();
            var watch = Stopwatch.StartNew();
            var outcome = await running.WaitAsync(TimeSpan.FromSeconds(60), Cancel);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The fetch went on for {watch.Elapsed.TotalSeconds} s without heartbeats.");
            Assert.Equal(ExitCodes.RemoteFailed, outcome.ExitCode);
            // The fetch itself was ended: its end of the connection closes,
            // or is reset when the process is killed.
            using var closed = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            closed.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = fetching.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer, closed.Token) > 0)
                {
                    // The request git sent before it waited.
                }
            }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
            }
        }
        catch (OperationCanceledException) when (!Cancel.IsCancellationRequested)
        {
            Assert.Fail("The fetch's connection to the origin was still open 10 s after the run ended.");
        }
        catch (TimeoutException)
        {
            Assert.Fail("The run went on for over 60 s: a fetch that hangs is not ended when the connection goes silent.");
        }
        finally
        {
            machine.KillAbandoned();
        }
    }

    [Fact]
    public async Task WhileHeartbeatsComeARunOutlivesTheAliveTimeout()
    {
        // The other side of the cut: a build or a test run takes far longer
        // than the timeout, and must not be ended while this end is there.
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        using var stdout = new StringWriter();
        try
        {
            var outcome = await Run(sandbox, machine, alive: TimeSpan.FromSeconds(3), stdout: stdout).RunAsync(
                "exec", ["pwsh", "-NoProfile", "-Command", "Start-Sleep 9; 'slept'"], json: false, Cancel);

            Assert.True(outcome.ExitCode == ExitCodes.Success, $"{outcome.ExitCode}: {outcome.Error}");
            Assert.Contains("slept", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            machine.KillAbandoned();
        }
    }

    [Fact]
    public async Task TheCommandReadsNoHeartbeats()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        using var stdout = new StringWriter();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var outcome = await Run(sandbox, machine, stdout: stdout).RunAsync(
                "exec", ["pwsh", "-NoProfile", "-Command", "$text = [Console]::In.ReadToEnd(); 'read ' + $text.Length"], json: false, limit.Token);

            Assert.True(outcome.ExitCode == ExitCodes.Success, $"{outcome.ExitCode}: {outcome.Error}");
            Assert.Contains("read 0", stdout.ToString(), StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!Cancel.IsCancellationRequested)
        {
            Assert.Fail("The command was still reading its input after 30 s: it was given the heartbeats.");
        }
        finally
        {
            machine.KillAbandoned();
        }
    }

    private static RemoteRun Run(GitSandbox sandbox, IRemoteMachine machine, TimeSpan? alive = null, TextWriter? stdout = null) =>
        new(sandbox.Clone, sandbox.Instance, machine, stdout ?? TextWriter.Null, TextWriter.Null)
        {
            Wait = TimeSpan.FromSeconds(5),
            Alive = alive ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>Waits for the command to write the process ids of itself and its child.</summary>
    private static async Task<int[]> PidsAsync(GitSandbox sandbox, Task<Outcome> running)
    {
        var runs = Path.Combine(sandbox.RemoteRoot, "runs", sandbox.Instance);
        for (var deadline = Stopwatch.StartNew(); deadline.Elapsed < TimeSpan.FromSeconds(60); await Task.Delay(200, Cancel))
        {
            Assert.False(running.IsCompleted, "The run ended before the command started its child.");
            var file = Directory.Exists(runs) ? Directory.EnumerateFiles(runs, "pids.txt", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (file is not null && File.ReadAllText(file).Trim() is { Length: > 0 } text && text.Contains(' ', StringComparison.Ordinal))
            {
                return text.Split(' ').Select(int.Parse).ToArray();
            }
        }

        throw new TimeoutException("The command did not start its child within 60 s.");
    }

    private static async Task<bool> GoneWithinAsync(int[] pids, TimeSpan wait)
    {
        for (var watch = Stopwatch.StartNew(); watch.Elapsed < wait; await Task.Delay(200, Cancel))
        {
            if (!pids.Any(Alive))
            {
                return true;
            }
        }

        return !pids.Any(Alive);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void CleanUp(DirectoryMachine machine, int[] pids)
    {
        machine.KillAbandoned();
        foreach (var pid in pids.Where(Alive))
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
    }
}
