using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Simple.Logging.Tests;

public sealed partial class ProgramLogTests
{
    [Fact]
    public void TheLogKeepsAtMostItsFileCountAndDropsTheOldestLines()
    {
        using var log = new TestLog(fileSizeLimitBytes: 2048, retainedFileCount: 3);

        for (var line = 1; line <= 200; line++)
        {
            Line(log.Logger, line);
        }

        var text = log.Text();
        var files = ProgramLog.Files(log.Directory);
        Assert.Equal(3, files.Count);
        // A file is closed once it has reached the limit, so it may pass it by one entry (here about 70 bytes).
        Assert.All(files.SkipLast(1), file => Assert.InRange(new FileInfo(file).Length, 2048, 2048 + 100));
        Assert.InRange(new FileInfo(files[^1]).Length, 1, 2048 + 100);
        Assert.DoesNotContain("line 0001", text, StringComparison.Ordinal);
        Assert.EndsWith("line 0200" + Environment.NewLine, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EachRunOfAProgramBeginsItsLogWithALineSayingItStarted()
    {
        using var log = new TestLog();

        var text = log.Text();

        Assert.Matches(
            $@"^\d{{4}}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{{3}} [+-]\d\d:\d\d \[INF\] Simple\.Logging\.ProgramLog: test started \(version .+, process {Environment.ProcessId}\)\r?\n$",
            text);
    }

    /// <summary>
    /// Another process can hold a log file a moment after it was written (on
    /// the CI machine, after the program had exited, #68): the log is read the
    /// way simple logs reads it, not refused.
    /// </summary>
    [Fact]
    public void ALogFileAnotherProcessStillHoldsCanBeRead()
    {
        using var log = new TestLog();
        Line(log.Logger, 1);
        log.Close();
        var file = Assert.Single(ProgramLog.Files(log.Directory));

        // As a virus scan or an indexer holds it: open, for writing too, sharing reads and writes.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => File.ReadAllText(file));
            Assert.Contains("line 0001", ProgramLog.ReadText(log.Directory), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutSettingsAProgramLogsUnderProgramDataInFilesOf10MegabytesKeepingTen()
    {
        var settings = ProgramLog.ReadSettings(new ConfigurationBuilder().Build(), "Server");

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Simple", "Server", "logs"),
            settings.Directory);
        Assert.Equal(10 * 1024 * 1024, settings.FileSizeLimitBytes);
        Assert.Equal(10, settings.RetainedFileCount);
    }

    [Fact]
    public void AProgramWritesWhereItsSimpleLogsSettingsSay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "simple-logging-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddCommandLine(
        [
            $"--Simple:Logs:Directory={directory}",
            "--Simple:Logs:FileSizeLimitBytes=4096",
            "--Simple:Logs:RetainedFileCount=2",
        ]).Build();
        try
        {
            using (var services = new ServiceCollection().AddLogging(logging => logging.AddProgramLog("Server", configuration, console: false)).BuildServiceProvider())
            {
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Simple.Server");
                Line(logger, 7);
            }

            Assert.Equal(new ProgramLogSettings(directory, 4096, 2), ProgramLog.ReadSettings(configuration, "Server"));
            var file = Assert.Single(ProgramLog.Files(directory));
            Assert.Equal("Server.log", Path.GetFileName(file));
            Assert.Contains("[INF] Simple.Server: line 0007", ProgramLog.ReadText(directory), StringComparison.Ordinal);
        }
        finally
        {
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
    }

    [Theory]
    [InlineData("FileSizeLimitBytes", "0")]
    [InlineData("FileSizeLimitBytes", "10MB")]
    [InlineData("RetainedFileCount", "-1")]
    public void ALimitThatIsNotAPositiveWholeNumberIsRefused(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddCommandLine([$"--Simple:Logs:{key}={value}"]).Build();

        var refused = Assert.Throws<InvalidDataException>(() => ProgramLog.ReadSettings(configuration, "Server"));

        Assert.Contains($"Simple:Logs:{key} '{value}'", refused.Message, StringComparison.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "line {Number:D4}")]
    private static partial void Line(ILogger logger, int number);
}
