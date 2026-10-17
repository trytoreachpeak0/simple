using Microsoft.Extensions.Logging;

namespace Simple.Logging.Tests;

/// <summary>
/// A program log in a directory of its own under the temp folder, written the
/// way every program writes it (<see cref="ProgramLog.CreateProvider"/>).
/// <see cref="Text"/> disposes the logger first, so everything is on disk.
/// </summary>
internal sealed class TestLog : IDisposable
{
    private readonly ProgramLogProvider provider;
    private readonly LoggerFactory factory;
    private bool closed;

    public TestLog(long fileSizeLimitBytes = 10 * 1024 * 1024, int retainedFileCount = 10)
    {
        Directory = Path.Combine(Path.GetTempPath(), "simple-logging-tests", Guid.NewGuid().ToString("N"));
        Settings = new ProgramLogSettings(Directory, fileSizeLimitBytes, retainedFileCount);
        provider = ProgramLog.CreateProvider("test", Settings, console: false);
        factory = new LoggerFactory([provider]);
        Logger = factory.CreateLogger("Simple.Logging.Tests");
    }

    public string Directory { get; }

    public ProgramLogSettings Settings { get; }

    public ILogger Logger { get; }

    public ProgramLogProvider Provider => provider;

    public ILogger CreateLogger(string category) => factory.CreateLogger(category);

    /// <summary>Closes the log and returns everything in its files, oldest first.</summary>
    public string Text()
    {
        Close();
        return ProgramLog.ReadText(Directory);
    }

    public void Close()
    {
        if (!closed)
        {
            closed = true;
            factory.Dispose();
            provider.Dispose();
        }
    }

    public void Dispose()
    {
        Close();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Best effort: a file just closed can still be held for a moment
            // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
        }
    }
}
