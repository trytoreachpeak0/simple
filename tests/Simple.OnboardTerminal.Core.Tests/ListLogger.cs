using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>Keeps every log line, formatted, for a test to look at.</summary>
internal sealed class ListLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Enqueue(formatter(state, exception));

    public void Dispose()
    {
    }
}
