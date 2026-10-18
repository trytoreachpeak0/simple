namespace Simple.Cli.Tests;

/// <summary>
/// Keeps a copy of what a failed test leaves in a temporary directory that
/// <see cref="CliRunner"/> deletes afterwards: on CI where the variable it is
/// told points (under the test results the run uploads), through simple
/// remote test in the run directory that comes back, otherwise under
/// artifacts/ in the clone.
/// </summary>
internal static class TestEvidence
{
    private static readonly string Repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);

    /// <summary>Copies <paramref name="directory"/> to <c>&lt;root&gt;/&lt;name&gt;</c>; returns where, or null when there was nothing to copy.</summary>
    /// <param name="variable">The environment variable CI sets to where it wants this kind of evidence.</param>
    /// <param name="folder">The folder for this kind of evidence in the run directory or under artifacts/.</param>
    public static string? Keep(string directory, string name, string variable, string folder)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var root = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured ? configured
            : Environment.GetEnvironmentVariable("SIMPLE_REMOTE_RUN") is { Length: > 0 } run ? Path.Combine(run, folder)
            : Path.Combine(Repository, "artifacts", folder);
        var kept = Path.Combine(root, name);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(kept, Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // A log may still be open for writing in a process that is ending.
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = File.Create(target);
            source.CopyTo(copy);
        }

        TestContext.Current.SendDiagnosticMessage($"The evidence of the failed run is kept in {kept}");
        return kept;
    }
}
