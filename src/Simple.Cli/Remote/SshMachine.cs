using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Simple.Cli.Remote;

/// <summary>
/// A machine reached with the system's own ssh and scp under an alias from
/// the user's SSH configuration (<c>~/.ssh/config</c>), which holds the
/// address, the account and the key. The repository names no machine: the
/// alias comes from <c>SIMPLE_REMOTE</c>.
/// </summary>
public sealed partial class SshMachine : IRemoteMachine
{
    public const string Variable = "SIMPLE_REMOTE";

    /// <summary>
    /// The SQL Server instance kept on that machine for simple remote alone
    /// (research/new-machine-evaluation.md 3.4): a named instance, so the
    /// command line's own rules accept it there as a local one.
    /// </summary>
    public const string SqlServer = @"Server=.\SIMPLE_DEV;Integrated Security=true;TrustServerCertificate=true";

    private static readonly string[] Options = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15"];

    public SshMachine(string alias)
    {
        if (Refusal(alias) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(alias));
        }

        Name = alias;
    }

    public string Name { get; }

    public string Root => "C:/simple-remote";

    public int Slots => 4;

    public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        [SqlServerTarget.Variable] = SqlServer,
        ["SIMPLE_TEST_SQLSERVER"] = SqlServer,
    };

    /// <summary>Why <paramref name="alias"/> cannot be used, or null. An alias starting with '-' would be read as an option.</summary>
    public static string? Refusal(string? alias) =>
        alias is not null && Alias().IsMatch(alias)
            ? null
            : $"{Variable} must be the alias of a host in your SSH configuration (letters, digits, '.', '_', '-'), not '{alias}'.";

    public async Task<int> RunAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        var start = new ProcessStartInfo("ssh");
        foreach (var argument in (string[])["-T", .. Options, Name, Command(script)])
        {
            start.ArgumentList.Add(argument);
        }

        var exitCode = await LineProcess.RunAsync(start, onOutput, onError, cancellationToken);
        // 255 is ssh's own: it could not connect, or the connection was lost.
        return exitCode == 255
            ? throw new RemoteException(ExitCodes.RemoteFailed, $"ssh {Name} failed (exit code 255): the machine could not be reached or the connection was lost.")
            : exitCode;
    }

    public Task SendAsync(string file, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        // Relative local paths: scp would read "F:/x" as a file on host "F".
        return ScpAsync(Path.GetDirectoryName(Path.GetFullPath(file))!, [Path.GetFileName(file), $"{Name}:{Root}/{directory}/"], cancellationToken);
    }

    public Task ReceiveAsync(string directory, string localParent, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localParent);
        return ScpAsync(localParent, ["-r", $"{Name}:{Root}/{directory}", "."], cancellationToken);
    }

    /// <summary>A script for <c>-EncodedCommand</c>: no quoting survives ssh, the remote shell and pwsh in between otherwise.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// The command line sshd hands to the remote machine's default shell.
    /// That shell does not pass the script's exit code on: pwsh, run with
    /// -c, ends with 1 for any non-zero code (seen on simple01, 2026-10-17).
    /// What the script has to say goes into its output instead.
    /// </summary>
    public static string Command(string script) => "pwsh -NoProfile -NonInteractive -EncodedCommand " + Encode(script);

    private static async Task ScpAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("scp") { WorkingDirectory = workingDirectory };
        foreach (var argument in (string[])["-q", "-B", .. Options, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        var errors = new List<string>();
        var exitCode = await LineProcess.RunAsync(start, _ => { }, line => { lock (errors) { errors.Add(line); } }, cancellationToken);
        if (exitCode != 0)
        {
            throw new RemoteException(ExitCodes.RemoteFailed, $"scp {string.Join(' ', arguments)} failed (exit code {exitCode}): {string.Join(' ', errors)}");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex Alias();
}
