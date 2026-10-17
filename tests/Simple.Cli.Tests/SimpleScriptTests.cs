using System.Diagnostics;

namespace Simple.Cli.Tests;

/// <summary>
/// simple.ps1 as a caller in PowerShell uses it: captured into a variable.
/// The script builds the command line (Debug) when it runs.
/// </summary>
public sealed class SimpleScriptTests
{
    // Runs in its own console (CreateNoWindow gives one, hidden), in the
    // machine's code page as a terminal is: 936 on a Chinese Windows. It
    // prints only ASCII, so its own output's encoding plays no part.
    private const string Caller =
        """
        #Requires -Version 7
        param($Script)
        $name = '已连接 服务端'
        $before = [Console]::OutputEncoding.CodePage
        $text = & $Script status --instance $name --json 2>$null
        "exit code: $LASTEXITCODE"
        "code page kept: $($before -eq [Console]::OutputEncoding.CodePage)"
        "name intact: $(($text | Out-String | ConvertFrom-Json).error.Contains("'$name'"))"
        """;

    [Fact]
    public async Task ACallerCapturingTheJsonGetsItsChineseIntact()
    {
        var repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);
        var caller = Path.Combine(Path.GetTempPath(), $"simple-script-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(caller, Caller);
        try
        {
            var start = new ProcessStartInfo("pwsh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" },
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", caller, Path.Combine(repository, "simple.ps1") })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var lines = (await output).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

            // 2: the name is not a valid instance name, and the error quotes it.
            Assert.True(lines.Contains("exit code: 2"), $"{await output}{await error}");
            Assert.Contains("name intact: True", lines);
            Assert.Contains("code page kept: True", lines);
        }
        finally
        {
            File.Delete(caller);
        }
    }
}
