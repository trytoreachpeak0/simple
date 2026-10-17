using System.Diagnostics;
using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// The "remote machine" of the tests: a directory on this machine. Scripts
/// run in a local pwsh, exactly as SSH would start them there; copies are file
/// copies. Everything above ssh and scp runs for real.
/// </summary>
public sealed class DirectoryMachine(string root) : IRemoteMachine
{
    private readonly List<string> sent = [];

    public string Name => "test-remote";

    public string Root { get; } = root.Replace('\\', '/');

    public int Slots { get; init; } = 2;

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

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

        return LineProcess.RunAsync(start, onOutput, onError, cancellationToken);
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
}
