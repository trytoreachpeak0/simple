using Microsoft.Extensions.Logging;

namespace Simple.Logging;

/// <summary>
/// A program's log provider (<see cref="ProgramLog.CreateProvider"/>). While
/// it lives it also records unhandled exceptions (<see cref="Unhandled"/>).
/// </summary>
public sealed class ProgramLogProvider : ILoggerProvider
{
    private readonly ILoggerProvider inner;

    internal ProgramLogProvider(ILoggerProvider inner)
    {
        this.inner = inner;
        Unhandled = new UnhandledExceptionLog(inner.CreateLogger(UnhandledExceptionLog.Category));
    }

    public UnhandledExceptionLog Unhandled { get; }

    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

    public void Dispose()
    {
        Unhandled.Dispose();
        inner.Dispose();
    }
}
