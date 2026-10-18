using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// A started process keeps its handle (#85): its exit code is there after it
/// is gone, and tells a process that ended on its own from one another
/// process killed.
/// </summary>
public sealed class ChildProcessTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("simple-child-process-test-").FullName;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;


    [Fact]
    public async Task AProcessThatEndsOnItsOwnLeavesItsExitCode()
    {
        using var started = Start("cmd.exe", "/c", "exit 3");

        await UntilExitedAsync(started);

        Assert.Equal(3u, started.ExitCode);
    }

    [Fact]
    public async Task AProcessKilledByAnotherSaysSo()
    {
        // ping, not cmd running it: a process without children of its own.
        using var started = Start("PING.EXE", "-n", "30", "127.0.0.1");
        Assert.Null(started.ExitCode);

        using (var other = ChildProcess.Find(started.Pid, started.StartedAt))
        {
            Assert.NotNull(other);
            other.Kill();
        }

        await UntilExitedAsync(started);
        Assert.Equal(uint.MaxValue, started.ExitCode);
        Assert.Equal("exited before it was ready, with exit code -1 (0xFFFFFFFF)", LocalSystem.ExitedBeforeReady(started.ExitCode));
    }

    [Fact]
    public void AProcessFoundKeepsItsHandle()
    {
        using var started = Start("PING.EXE", "-n", "30", "127.0.0.1");
        var (pid, startedAt) = (started.Pid, started.StartedAt);
        using var found = ChildProcess.Find(pid, startedAt);
        Assert.NotNull(found);
        // From here on only the process Find returned holds a handle.
        started.Dispose();

        using (var other = Process.GetProcessById(pid))
        {
            other.Kill();
            other.WaitForExit();
        }

        // Read through the handle Find opened: without it, the process
        // would be gone and its exit code with it.
        Assert.True(found.HasExited);
        Assert.Equal(-1, found.ExitCode);
    }

    [Fact]
    public void AProcessThatRefusesAFullHandleIsStillFoundNotTakenForGone()
    {
        using var started = Start("PING.EXE", "-n", "30", "127.0.0.1");
        try
        {
            // As a process started elevated answers an ordinary terminal.
            using var found = ChildProcess.Find(started.Pid, started.StartedAt, _ => throw new Win32Exception(5));

            Assert.NotNull(found);
            Assert.Equal(started.Pid, found.Id);
        }
        finally
        {
            using var other = Process.GetProcessById(started.Pid);
            other.Kill();
            other.WaitForExit();
        }
    }

    [Fact]
    public void WithoutAnExitCodeTheReasonSaysOnlyThatItExited() =>
        Assert.Equal("exited before it was ready", LocalSystem.ExitedBeforeReady(null));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private StartedProcess Start(string executable, params string[] arguments)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
        return ChildProcess.Start(Path.Combine(Environment.SystemDirectory, executable), arguments, directory, environment, Path.Combine(directory, $"{Guid.NewGuid():N}.log"));
    }

    private static async Task UntilExitedAsync(StartedProcess started)
    {
        for (var watch = Stopwatch.StartNew(); started.ExitCode is null; await Task.Delay(50, Cancel))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Process {started.Pid} still runs after 30 s.");
        }
    }
}
