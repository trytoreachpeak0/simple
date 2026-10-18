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
        Process[] processes = [];
        try
        {
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            var running = Run(sandbox, machine).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", SleepWithAChild], json: false, cut.Token);
            processes = await ProcessesAsync(sandbox, running);

            await cut.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

            Assert.True(await GoneWithinAsync(processes, TimeSpan.FromSeconds(15)), $"Still running 15 s after the cut: {Running(processes)}");
            var next = await Run(sandbox, machine).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", "'next'"], json: false, Cancel);
            Assert.True(next.ExitCode == ExitCodes.Success, $"{next.ExitCode}: {next.Error}");
        }
        finally
        {
            CleanUp(machine, processes);
        }
    }

    [Fact]
    public async Task ASilentConnectionEndsTheRunAfterTheAliveTimeout()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        var machine = new DirectoryMachine(sandbox.RemoteRoot);
        Process[] processes = [];
        try
        {
            var running = Run(sandbox, machine, alive: TimeSpan.FromSeconds(3)).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", SleepWithAChild], json: false, Cancel);
            processes = await ProcessesAsync(sandbox, running);

            machine.GoSilent();
            var watch = Stopwatch.StartNew();
            var outcome = await running;

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The run went on for {watch.Elapsed.TotalSeconds} s without heartbeats.");
            Assert.Equal(ExitCodes.RemoteFailed, outcome.ExitCode);
            Assert.True(await GoneWithinAsync(processes, TimeSpan.FromSeconds(5)), $"Still running: {Running(processes)}");
        }
        finally
        {
            CleanUp(machine, processes);
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

    /// <summary>
    /// Waits for the command to write the process ids of itself and its
    /// child, and opens both at once, while they sleep. The handles held keep
    /// Windows from giving those ids to another process once these two have
    /// ended, so the checks and the clean-up below only ever see and kill
    /// these two: by bare id, the clean-up could kill whatever process had
    /// been given a recycled id by then, another test's component included
    /// (#85).
    /// </summary>
    private static async Task<Process[]> ProcessesAsync(GitSandbox sandbox, Task<Outcome> running)
    {
        var runs = Path.Combine(sandbox.RemoteRoot, "runs", sandbox.Instance);
        for (var deadline = Stopwatch.StartNew(); deadline.Elapsed < TimeSpan.FromSeconds(60); await Task.Delay(200, Cancel))
        {
            Assert.False(running.IsCompleted, "The run ended before the command started its child.");
            var file = Directory.Exists(runs) ? Directory.EnumerateFiles(runs, "pids.txt", SearchOption.AllDirectories).FirstOrDefault() : null;
            string written;
            try
            {
                written = file is null ? string.Empty : File.ReadAllText(file);
            }
            catch (IOException)
            {
                // Set-Content is still writing it; the next round reads it whole.
                continue;
            }

            // Set-Content ends the line: without its newline the file may be half written.
            if (written.EndsWith('\n') && written.Trim() is { Length: > 0 } text && text.Contains(' ', StringComparison.Ordinal))
            {
                return OpenAll(text.Split(' ').Select(int.Parse));
            }
        }

        throw new TimeoutException("The command did not start its child within 60 s.");
    }

    /// <summary>Opens every process, or none: one that cannot be opened closes those opened before it.</summary>
    private static Process[] OpenAll(IEnumerable<int> pids)
    {
        var opened = new List<Process>();
        try
        {
            foreach (var pid in pids)
            {
                opened.Add(Open(pid));
            }

            return [.. opened];
        }
        catch
        {
            foreach (var process in opened)
            {
                process.Dispose();
            }

            throw;
        }
    }

    private static Process Open(int pid)
    {
        var process = Process.GetProcessById(pid);
        // Opens the handle now; the Process keeps it until disposed.
        _ = process.SafeHandle;
        return process;
    }

    private static async Task<bool> GoneWithinAsync(Process[] processes, TimeSpan wait)
    {
        for (var watch = Stopwatch.StartNew(); watch.Elapsed < wait; await Task.Delay(200, Cancel))
        {
            if (processes.All(p => p.HasExited))
            {
                return true;
            }
        }

        return processes.All(p => p.HasExited);
    }

    private static string Running(Process[] processes) =>
        string.Join(", ", processes.Where(p => !p.HasExited).Select(p => p.Id));

    private static void CleanUp(DirectoryMachine machine, Process[] processes)
    {
        machine.KillAbandoned();
        foreach (var process in processes)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }
}
