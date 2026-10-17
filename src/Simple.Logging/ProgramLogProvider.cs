using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Simple.Logging;

/// <summary>
/// A program's log provider (<see cref="ProgramLog.CreateProvider"/>). It
/// begins the log with a line saying the program started, so every run shows
/// in the log even when the program says nothing else, and while it lives it
/// records unhandled exceptions (<see cref="Unhandled"/>).
/// </summary>
public sealed partial class ProgramLogProvider : ILoggerProvider
{
    private readonly ILoggerProvider inner;

    internal ProgramLogProvider(string program, ILoggerProvider inner)
    {
        this.inner = inner;
        var entry = Assembly.GetEntryAssembly();
        var version = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? entry?.GetName().Version?.ToString()
            ?? "unknown";
        var logger = inner.CreateLogger(typeof(ProgramLog).FullName!);
        Started(logger, program, version, Environment.ProcessId);
        Unhandled = new UnhandledExceptionLog(inner.CreateLogger(UnhandledExceptionLog.Category));
    }

    public UnhandledExceptionLog Unhandled { get; }

    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

    public void Dispose()
    {
        Unhandled.Dispose();
        inner.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Program} started (version {Version}, process {ProcessId})")]
    private static partial void Started(ILogger logger, string program, string version, int processId);
}
