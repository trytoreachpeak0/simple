using System.Diagnostics;
using System.Text.Json;

namespace Simple.Cli.Tests;

/// <summary>
/// scripts/ci/Assert-SdkVersion.ps1, the first CI step: run as CI runs it,
/// with pwsh -File, against a global.json of its own that pins 10.0.113, so
/// the cases hold whatever the repository pins. Judged by exit code and by
/// the ::error:: line, never by an exception's text.
/// </summary>
public sealed class AssertSdkVersionTests
{
    private const string Pinned = "10.0.113";

    [Theory]
    [InlineData("10.0.113")]
    [InlineData("10.0.150")]
    public async Task AcceptsThePinnedPatchOrANewerOneOfTheSameBand(string resolved)
    {
        var (exitCode, output) = await RunAsync(Pinned, resolved);

        Assert.True(exitCode == 0, output);
        Assert.DoesNotContain("::error::", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.112")]
    [InlineData("10.0.200")]
    [InlineData("11.0.113")]
    [InlineData("10.0")]
    [InlineData("10.0.113.0")]
    [InlineData("10.0.113-preview")]
    // $ also matches before a final newline; \z does not.
    [InlineData("10.0.113\n")]
    // \d also matches digits of other scripts: Arabic-Indic, fullwidth.
    [InlineData("10.0.11٣")]
    [InlineData("١٠.0.113")]
    [InlineData("10.0.11３")]
    public async Task RefusesAnythingElseWithAnErrorLine(string resolved)
    {
        var (exitCode, output) = await RunAsync(Pinned, resolved);

        Assert.True(exitCode == 1, $"exit code {exitCode}: {output}");
        Assert.Contains($"::error::global.json pins SDK {Pinned} ", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.113\n")]
    [InlineData("10.0.11٣")]
    [InlineData("10.0")]
    public async Task RefusesAPinThatIsNotMajorMinorPatch(string pinned)
    {
        var (exitCode, output) = await RunAsync(pinned, Pinned);

        Assert.True(exitCode == 1, $"exit code {exitCode}: {output}");
        Assert.Contains("which is not a major.minor.patch version.", output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string pinned, string resolved)
    {
        var cancel = TestContext.Current.CancellationToken;
        var repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);
        var directory = Directory.CreateTempSubdirectory("simple-sdk-version-test-").FullName;
        try
        {
            var globalJson = Path.Combine(directory, "global.json");
            await File.WriteAllTextAsync(globalJson, JsonSerializer.Serialize(new { sdk = new { version = pinned, rollForward = "latestPatch" } }), cancel);
            var start = new ProcessStartInfo("pwsh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repository, "scripts", "ci", "Assert-SdkVersion.ps1"), "-GlobalJson", globalJson, "-Resolved", resolved })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var error = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
