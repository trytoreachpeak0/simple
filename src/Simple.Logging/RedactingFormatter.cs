using Serilog.Events;
using Serilog.Formatting;

namespace Simple.Logging;

/// <summary>
/// Writes what the inner formatter writes, with credentials taken out
/// (<see cref="Redaction"/>). Applied to the whole rendered event, so the
/// message, its values and the exception text are all covered.
/// </summary>
internal sealed class RedactingFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var rendered = new StringWriter(output.FormatProvider);
        inner.Format(logEvent, rendered);
        output.Write(Redaction.Redact(rendered.ToString()));
    }
}
