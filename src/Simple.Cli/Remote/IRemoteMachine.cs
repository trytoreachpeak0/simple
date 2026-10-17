namespace Simple.Cli.Remote;

/// <summary>
/// The machine simple remote runs on, reduced to what it needs from it: run
/// a PowerShell script there with its output coming back line by line, and
/// copy files there and back. <see cref="SshMachine"/> does this with ssh and
/// scp; the tests use a directory on this machine.
/// </summary>
/// <remarks>A failure to reach the machine or to copy is a <see cref="RemoteException"/>.</remarks>
public interface IRemoteMachine
{
    /// <summary>The name printed in every report (the SSH alias).</summary>
    string Name { get; }

    /// <summary>The directory everything lives in on that machine, with forward slashes.</summary>
    string Root { get; }

    /// <summary>How many heavy commands (build, test, exec, up) may run on the machine at once.</summary>
    int Slots { get; }

    /// <summary>Set for every command run there, on top of that machine's own environment.</summary>
    IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Runs <paramref name="script"/> in PowerShell 7 there and returns its exit code.</summary>
    Task<int> RunAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);

    /// <summary>Copies a local file into <paramref name="directory"/>, relative to <see cref="Root"/>.</summary>
    Task SendAsync(string file, string directory, CancellationToken cancellationToken);

    /// <summary>Copies <paramref name="directory"/> (relative to <see cref="Root"/>) with its contents into <paramref name="localParent"/>.</summary>
    Task ReceiveAsync(string directory, string localParent, CancellationToken cancellationToken);
}
