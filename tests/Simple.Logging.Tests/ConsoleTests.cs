using Microsoft.Extensions.Logging;

namespace Simple.Logging.Tests;

/// <summary>
/// The console output goes through the same redaction as the file. It
/// replaces the process's console for a moment, so it runs alone.
/// </summary>
[Collection(nameof(RedactionTimeTests))]
public sealed partial class ConsoleTests
{
    [Fact]
    public void TheConsoleShowsTheLineWithoutTheCredential()
    {
        var directory = Path.Combine(Path.GetTempPath(), "simple-logging-tests", Guid.NewGuid().ToString("N"));
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            using (var provider = ProgramLog.CreateProvider("test", new ProgramLogSettings(directory, 1024 * 1024, 2), console: true))
            {
                var logger = provider.CreateLogger("Simple.Logging.Tests");
                Connecting(logger, "Server=db01;Password=Console-secret");
            }
        }
        finally
        {
            Console.SetOut(original);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file just closed can still be held for a moment
                // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
            }
        }

        var text = console.ToString();
        Assert.Contains("[INF] Simple.Logging.Tests: Connecting with Server=db01;Password=***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Console-secret", text, StringComparison.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting with {Target}")]
    private static partial void Connecting(ILogger logger, string target);
}
