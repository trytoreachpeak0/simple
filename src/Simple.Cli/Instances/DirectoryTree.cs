using System.Diagnostics;
using Simple.Cli.Processes;

namespace Simple.Cli.Instances;

/// <summary>
/// Deletes a directory tree that processes ran from. A file can stay held a
/// moment after the process that used it has stopped, for example by a virus
/// scanner: deleting a DLL that some process still has loaded fails with
/// access denied, one still open without delete sharing with a sharing
/// violation. On CI this failed simple down (#60).
/// </summary>
public static class DirectoryTree
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, trying again
    /// for up to <paramref name="wait"/> while files in it are held.
    /// </summary>
    /// <exception cref="FilesHeldException">Files were still held when the wait was over; they and their holders are named.</exception>
    public static async Task DeleteAsync(string path, TimeSpan wait, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var pause = FirstPause;
        while (true)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= wait)
                {
                    throw new FilesHeldException(path, wait, Left(path), e);
                }
            }

            await Task.Delay(pause, cancellationToken);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
        }
    }

    /// <summary>What a failed recursive delete left: the files it could not delete, or the directories when no file is left.</summary>
    private static List<HeldFile> Left(string path)
    {
        const int Named = 5;
        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(Named).ToList();
            return files.Count > 0
                ? [.. files.Select(file => new HeldFile(file, FileHolders.Of(file)))]
                : [.. Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Prepend(path).Take(Named).Select(directory => new HeldFile(directory, []))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [new HeldFile(path, [])];
        }
    }
}

/// <summary>A file or directory a delete left behind, with the processes that hold it as far as Windows names them.</summary>
public sealed record HeldFile(string Path, IReadOnlyList<string> HeldBy);

public sealed class FilesHeldException(string directory, TimeSpan wait, IReadOnlyList<HeldFile> files, Exception innerException)
    : IOException(Describe(directory, wait, files), innerException)
{
    public IReadOnlyList<HeldFile> Files { get; } = files;

    private static string Describe(string directory, TimeSpan wait, IReadOnlyList<HeldFile> files) =>
        $"{directory} could not be deleted within {wait.TotalSeconds:0.#} s. Still there: "
        + string.Join("; ", files.Select(f => f.HeldBy.Count > 0
            ? $"{f.Path}, held by {string.Join(", ", f.HeldBy)}"
            : $"{f.Path}, held by no process Windows names (a virus scanner, for example, is not named)"))
        + ".";
}
