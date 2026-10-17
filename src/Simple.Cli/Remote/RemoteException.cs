namespace Simple.Cli.Remote;

/// <summary>simple remote could not do its part; <see cref="ExitCode"/> is what the command line ends with.</summary>
public sealed class RemoteException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
