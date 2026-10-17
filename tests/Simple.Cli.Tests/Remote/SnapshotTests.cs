using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

public sealed class SnapshotTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TakesInChangesAndNewFilesButNotIgnoredOnes()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        sandbox.Write("README.txt", "changed\n");
        sandbox.Write("tests/NewTests.cs", "new, never added\n");
        sandbox.Write("bin/output.dll", "ignored\n");
        File.Delete(Path.Combine(sandbox.Clone, "src/kept.txt"));
        var head = (await sandbox.GitAsync(Cancel, "rev-parse", "HEAD")).Trim();

        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        Assert.Equal(head, snapshot.Head);
        Assert.NotEqual(head, snapshot.Commit);
        Assert.Equal(3, snapshot.Uncommitted);
        Assert.Equal("changed\n", await sandbox.GitAsync(Cancel, "show", $"{snapshot.Commit}:README.txt"));
        Assert.Equal("new, never added\n", await sandbox.GitAsync(Cancel, "show", $"{snapshot.Commit}:tests/NewTests.cs"));
        var files = Git.Lines(await sandbox.GitAsync(Cancel, "ls-tree", "-r", "--name-only", snapshot.Commit)).ToList();
        Assert.DoesNotContain("bin/output.dll", files);
        Assert.DoesNotContain("src/kept.txt", files);
        Assert.Equal(head, (await sandbox.GitAsync(Cancel, "rev-parse", $"{snapshot.Commit}^")).Trim());
    }

    [Fact]
    public async Task LeavesTheIndexAndTheWorkingTreeAlone()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        sandbox.Write("README.txt", "staged\n");
        await sandbox.GitAsync(Cancel, "add", "README.txt");
        sandbox.Write("README.txt", "staged, then changed again\n");
        sandbox.Write("new.txt", "untracked\n");
        var before = await sandbox.GitAsync(Cancel, "status", "--porcelain=v1");
        var staged = await sandbox.GitAsync(Cancel, "diff", "--cached");

        await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        Assert.Equal(before, await sandbox.GitAsync(Cancel, "status", "--porcelain=v1"));
        Assert.Equal(staged, await sandbox.GitAsync(Cancel, "diff", "--cached"));
        Assert.Equal("staged, then changed again\n", File.ReadAllText(Path.Combine(sandbox.Clone, "README.txt")));
    }

    [Fact]
    public async Task TheSameTreeGivesTheSameCommit()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        sandbox.Write("new.txt", "one\n");

        var first = await Snapshot.TakeAsync(sandbox.Clone, Cancel);
        await Task.Delay(1100, Cancel); // A commit time taken from the clock would differ now.
        var second = await Snapshot.TakeAsync(sandbox.Clone, Cancel);
        sandbox.Write("new.txt", "two\n");
        var third = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        Assert.Equal(first.Commit, second.Commit);
        Assert.NotEqual(first.Commit, third.Commit);
    }

    [Fact]
    public async Task ACleanTreeIsHeadItself()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        sandbox.Write("bin/ignored.dll", "ignored\n");
        var head = (await sandbox.GitAsync(Cancel, "rev-parse", "HEAD")).Trim();

        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);

        Assert.Equal(new Snapshot(head, head, 0), snapshot);
    }

    [Fact]
    public async Task LargeFilesAreThoseAddedSinceTheBase()
    {
        await using var sandbox = await GitSandbox.CreateAsync(Cancel);
        sandbox.Write("pushed-large.bin", new string('a', 2000));
        await sandbox.CommitAsync("large, pushed", Cancel);
        await sandbox.GitAsync(Cancel, "push", "--quiet", "origin", "main");
        await sandbox.GitAsync(Cancel, "fetch", "--quiet", "origin");
        sandbox.Write("evidence/new-large.bin", new string('b', 2000));
        sandbox.Write("small.txt", "small\n");

        var snapshot = await Snapshot.TakeAsync(sandbox.Clone, Cancel);
        var large = await Snapshot.LargeFilesAsync(sandbox.Clone, snapshot.Commit, "refs/remotes/origin/main", 1000, Cancel);

        Assert.Equal([("evidence/new-large.bin", 2000L)], large);
    }
}
