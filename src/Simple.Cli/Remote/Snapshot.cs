using System.Globalization;

namespace Simple.Cli.Remote;

/// <summary>
/// The working tree as one commit: what simple remote sends and runs.
/// </summary>
/// <param name="Commit">The snapshot commit; <see cref="Head"/> itself when nothing is uncommitted.</param>
/// <param name="Head">The commit the snapshot is based on.</param>
/// <param name="Uncommitted">How many files differ from <see cref="Head"/>: changed, added or deleted.</param>
public sealed record Snapshot(string Commit, string Head, int Uncommitted)
{
    /// <summary>The ref that holds an instance's snapshot, here and in the remote cache, so it is not collected.</summary>
    public static string Ref(string instance) => $"refs/simple-remote/{instance}";

    /// <summary>
    /// The ref the remote cache fetches the origin's branch into for an
    /// instance. One per instance, not the shared refs/remotes/origin/main:
    /// a fetch ended by a cut leaves the ref's lock, which only that
    /// instance's next run, under the instance's lock, can safely remove.
    /// </summary>
    public static string OriginRef(string instance) => $"refs/simple-remote-origin/{instance}";

    /// <summary>
    /// Takes the snapshot through a temporary index: it starts from HEAD and
    /// takes in every change and every new file that .gitignore does not
    /// exclude (<c>git add -A</c>), so a test file nobody has added yet is in
    /// it. The real index and the working tree are not touched. Author, committer
    /// and time are fixed (the time is HEAD's), so the same tree always gives
    /// the same commit.
    /// </summary>
    public static async Task<Snapshot> TakeAsync(string repository, CancellationToken cancellationToken)
    {
        var head = (await Git.RunAsync(repository, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken)).Trim();
        var index = Path.Combine(Path.GetTempPath(), $"simple-remote-{Guid.NewGuid():N}.index");
        try
        {
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            await Git.RunAsync(repository, ["read-tree", head], cancellationToken, environment);
            await Git.RunAsync(repository, ["add", "-A", "--", ":/"], cancellationToken, environment);
            var tree = (await Git.RunAsync(repository, ["write-tree"], cancellationToken, environment)).Trim();
            var changed = Git.Lines(await Git.RunAsync(repository, ["diff-tree", "-r", "--name-only", "--no-renames", head, tree], cancellationToken)).Count();
            if (changed == 0)
            {
                return new Snapshot(head, head, 0);
            }

            var date = (await Git.RunAsync(repository, ["show", "-s", "--format=%ct %cz", head], cancellationToken)).Trim();
            var identity = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "simple remote",
                ["GIT_AUTHOR_EMAIL"] = "simple-remote@invalid",
                ["GIT_AUTHOR_DATE"] = date,
                ["GIT_COMMITTER_NAME"] = "simple remote",
                ["GIT_COMMITTER_EMAIL"] = "simple-remote@invalid",
                ["GIT_COMMITTER_DATE"] = date,
            };
            var commit = (await Git.RunAsync(
                repository,
                ["commit-tree", tree, "-p", head, "-m", $"simple remote snapshot of {head}"],
                cancellationToken,
                identity)).Trim();
            return new Snapshot(commit, head, changed);
        }
        finally
        {
            Scratch.Delete(index);
            Scratch.Delete(index + ".lock");
        }
    }

    /// <summary>
    /// The files the snapshot adds over <paramref name="baseRef"/> (all of its
    /// history when null) that are larger than <paramref name="limit"/> bytes.
    /// They would go to GitHub with the next push, too.
    /// </summary>
    public static async Task<IReadOnlyList<(string Path, long Size)>> LargeFilesAsync(
        string repository, string commit, string? baseRef, long limit, CancellationToken cancellationToken)
    {
        var objects = await Git.RunAsync(
            repository,
            baseRef is null ? ["rev-list", "--objects", commit] : ["rev-list", "--objects", commit, "--not", baseRef],
            cancellationToken);
        var sizes = await Git.RunAsync(
            repository,
            ["cat-file", "--batch-check=%(objecttype) %(objectsize) %(rest)"],
            cancellationToken,
            input: objects);
        return Git.Lines(sizes)
            .Select(line => line.Split(' ', 3))
            .Where(parts => parts.Length == 3 && parts[0] == "blob" && long.Parse(parts[1], CultureInfo.InvariantCulture) > limit)
            .Select(parts => (parts[2], long.Parse(parts[1], CultureInfo.InvariantCulture)))
            .DistinctBy(file => file.Item1)
            .ToList();
    }
}
