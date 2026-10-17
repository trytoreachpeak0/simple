using System.Diagnostics;
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
    private readonly List<Process> sessions = [];

    public string Name => "test-remote";

    public string Root { get; } = root.Replace('\\', '/');

    public int Slots { get; init; } = 2;

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>How often the client side writes a heartbeat line.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromMilliseconds(500);

    private volatile bool silent;

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

    public Task<int> RunAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        // As sshd does with DefaultShell pwsh: the command line SSH would send,
        // run by an outer pwsh -c, which turns every non-zero exit code into 1.
        // And, as sshd gives each session, a console of its own, in the
        // machine's code page (936 on a Chinese Windows), not this test's.
        var start = new ProcessStartInfo("pwsh") { CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-c", SshMachine.Command(script) })
        {
            start.ArgumentList.Add(argument);
        }

        // The heartbeats are the product's own (LineProcess), at this
        // machine's interval; on cancel, as sshd, the session's input ends
        // but nothing there is killed.
        return LineProcess.RunAsync(
            start,
            onOutput,
            onError,
            cancellationToken,
            SshMachine.RunOptions with
            {
                Heartbeat = Heartbeat,
                Silent = () => silent,
                KillOnCancel = false,
                Started = process =>
                {
                    lock (sessions)
                    {
                        sessions.Add(process);
                    }
                },
            });
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

    /// <summary>From now on the client writes no heartbeats but stays connected, as when the network is lost.</summary>
    public void GoSilent() => silent = true;

    /// <summary>Kills what sessions of abandoned runs left running, so a test that fails leaves nothing behind.</summary>
    public void KillAbandoned()
    {
        lock (sessions)
        {
            foreach (var process in sessions)
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
                    // Already gone, or its handle was let go once it ended.
                }
            }

            sessions.Clear();
        }
    }
}
