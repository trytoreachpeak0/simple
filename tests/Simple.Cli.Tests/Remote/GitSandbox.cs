using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// A throwaway origin (a bare repository), a clone of it with one pushed
/// commit and a .gitignore, and a directory for the remote machine, all under
/// one temporary directory that disposing deletes.
/// </summary>
public sealed class GitSandbox : IAsyncDisposable
{
    private static readonly Dictionary<string, string> Identity = new()
    {
        ["GIT_AUTHOR_NAME"] = "test",
        ["GIT_AUTHOR_EMAIL"] = "test@invalid",
        ["GIT_COMMITTER_NAME"] = "test",
        ["GIT_COMMITTER_EMAIL"] = "test@invalid",
    };

    private GitSandbox(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public string Origin => Path.Combine(Directory, "origin.git");

    /// <summary>The clone; its folder name is the instance, as in a real worktree.</summary>
    public string Clone => Path.Combine(Directory, Instance);

    public string Instance { get; } = $"test_{Guid.NewGuid():N}"[..17];

    public string RemoteRoot => Path.Combine(Directory, "remote");

    public static async Task<GitSandbox> CreateAsync(CancellationToken cancellationToken)
    {
        var sandbox = new GitSandbox(System.IO.Directory.CreateTempSubdirectory("simple-remote-test-").FullName);
        System.IO.Directory.CreateDirectory(sandbox.Clone);
        System.IO.Directory.CreateDirectory(sandbox.RemoteRoot);
        await Git.RunAsync(sandbox.Directory, ["init", "--quiet", "--bare", "--initial-branch=main", sandbox.Origin], cancellationToken);
        await sandbox.GitAsync(cancellationToken, "init", "--quiet", "--initial-branch=main");
        await sandbox.GitAsync(cancellationToken, "config", "core.autocrlf", "false");
        sandbox.Write(".gitignore", "bin/\nartifacts/\n*.log\n");
        sandbox.Write("README.txt", "first\n");
        sandbox.Write("src/kept.txt", "kept\n");
        // Stands in for the clone's simple.ps1: says what it was asked, as JSON when asked to.
        sandbox.Write(
            "simple.ps1",
            """
            #Requires -Version 7
            if ($args -contains '--json') { @{ exitCode = 0; error = $null; result = @{ args = $args } } | ConvertTo-Json -Compress }
            else { "fake simple $args" }
            exit 0
            """);
        await sandbox.CommitAsync("first", cancellationToken);
        await sandbox.GitAsync(cancellationToken, "remote", "add", "origin", sandbox.Origin);
        await sandbox.GitAsync(cancellationToken, "push", "--quiet", "origin", "main");
        await sandbox.GitAsync(cancellationToken, "fetch", "--quiet", "origin");
        return sandbox;
    }

    public void Write(string path, string text)
    {
        var full = Path.Combine(Clone, path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public string RemoteWorktree(string? instance = null) => Path.Combine(RemoteRoot, "wt", instance ?? Instance);

    public Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments) =>
        Git.RunAsync(Clone, arguments, cancellationToken, Identity);

    public async Task<string> CommitAsync(string message, CancellationToken cancellationToken)
    {
        await GitAsync(cancellationToken, "add", "-A");
        await GitAsync(cancellationToken, "commit", "--quiet", "-m", message);
        return (await GitAsync(cancellationToken, "rev-parse", "HEAD")).Trim();
    }

    public async ValueTask DisposeAsync()
    {
        // git marks pack files read-only.
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                // A process the test started may still be letting go of a file.
                await Task.Delay(200);
            }
        }
    }
}
