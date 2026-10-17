using System.Diagnostics;
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
        var machine = new DirectoryMachine(sandbox.RemoteRoot) { SilenceAfter = 2 };
        int[] pids = [];
        try
        {
            var watch = Stopwatch.StartNew();
            var running = Run(sandbox, machine, alive: TimeSpan.FromSeconds(3)).RunAsync("exec", ["pwsh", "-NoProfile", "-Command", SleepWithAChild], json: false, Cancel);
            pids = await PidsAsync(sandbox, running);

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
