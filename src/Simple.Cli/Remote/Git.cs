using System.Diagnostics;
using System.Text;

namespace Simple.Cli.Remote;

/// <summary>Runs git in a clone and returns what it printed; a non-zero exit is a <see cref="RemoteException"/>.</summary>
public static class Git
{
    public static async Task<string> RunAsync(
        string repository,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? input = null)
    {
        var (exitCode, output, error) = await TryAsync(repository, arguments, cancellationToken, environment, input);
        return exitCode == 0
            ? output
            : throw new RemoteException(ExitCodes.RemoteFailed, $"git {string.Join(' ', arguments)} exited with {exitCode}: {error.Trim()}");
    }

    public static async Task<(int ExitCode, string Output, string Error)> TryAsync(
        string repository,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? input = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var git = Process.Start(start) ?? throw new RemoteException(ExitCodes.RemoteFailed, "Could not start git.");
        var output = git.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = git.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            await git.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            git.StandardInput.Close();
        }

        try
        {
            await git.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            git.Kill(entireProcessTree: true);
            throw;
        }

        return (git.ExitCode, await output, await error);
    }

    public static IEnumerable<string> Lines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
