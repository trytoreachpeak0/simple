using System.Diagnostics;
using System.Text;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// The "remote machine" of the tests: a directory on this machine. Scripts
/// run in a local pwsh, exactly as SSH would start them there; copies are file
/// copies. Everything above ssh and scp runs for real.
/// </summary>
/// <remarks>
/// It plays both ends of the connection the way they behave on the real
/// machine: the client writes a heartbeat line into the session's input; the
/// session runs under an outer <c>pwsh -c</c> in a console of its own; and
/// when the client goes away (the test cancels: a killed ssh, Ctrl+C), the
/// session's input ends but nothing there is killed, as with sshd on simple01
/// (2026-10-17).
/// </remarks>
public sealed class DirectoryMachine(string root) : IRemoteMachine
{
    private readonly List<string> sent = [];
    private readonly List<Process> abandoned = [];

    public string Name => "test-remote";

    public string Root { get; } = root.Replace('\\', '/');

    public int Slots { get; init; } = 2;

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>How often the client side writes a heartbeat line.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>After this many heartbeats the client goes silent but stays connected, as when the network is lost.</summary>
    public int? SilenceAfter { get; init; }

    /// <summary>The names of the files sent, in order.</summary>
    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (sent)
            {
                return [.. sent];
            }
        }
    }

    public async Task<int> RunAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        // As sshd does with DefaultShell pwsh: the command line SSH would send,
        // run by an outer pwsh -c, which turns every non-zero exit code into 1.
        // And, as sshd gives each session, a console of its own, in the
        // machine's code page (936 on a Chinese Windows), not this test's.
        var start = new ProcessStartInfo("pwsh")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-c", SshMachine.Command(script) })
        {
            start.ArgumentList.Add(argument);
        }

        using var stop = new CancellationTokenSource();
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && !stop.IsCancellationRequested)
            {
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && !stop.IsCancellationRequested)
            {
                onError(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var beating = BeatAsync(process, stop.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client is gone: the session's input ends; what runs there goes on.
            await stop.CancelAsync();
            CloseInput(process);
            lock (abandoned)
            {
                abandoned.Add(process);
            }

            throw;
        }

        await stop.CancelAsync();
        await beating;
        CloseInput(process);
        var exitCode = process.ExitCode;
        process.Dispose();
        return exitCode;
    }

    public Task SendAsync(string file, string directory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Root, directory);
        Directory.CreateDirectory(target);
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        lock (sent)
        {
            sent.Add(Path.GetFileName(file));
        }

        return Task.CompletedTask;
    }

    public Task ReceiveAsync(string directory, string localParent, CancellationToken cancellationToken)
    {
        var source = Path.Combine(Root, directory);
        var target = Path.Combine(localParent, Path.GetFileName(source));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>Kills what sessions of abandoned runs left running, so a test that fails leaves nothing behind.</summary>
    public void KillAbandoned()
    {
        lock (abandoned)
        {
            foreach (var process in abandoned)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                process.Dispose();
            }

            abandoned.Clear();
        }
    }

    private async Task BeatAsync(Process process, CancellationToken stop)
    {
        var beats = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (SilenceAfter is not { } silence || beats < silence)
                {
                    await process.StandardInput.WriteLineAsync("alive".AsMemory(), stop);
                    await process.StandardInput.FlushAsync(stop);
                    beats++;
                }

                await Task.Delay(Heartbeat, stop);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, or the session has ended.
        }
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The session has ended already.
        }
    }
}
