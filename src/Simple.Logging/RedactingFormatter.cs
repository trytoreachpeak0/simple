using Serilog.Events;
using Serilog.Formatting;

namespace Simple.Logging;

/// <summary>
/// Writes a log line as <paramref name="prefix"/> then <paramref name="body"/>,
/// with credentials taken out of the body (<see cref="Redaction"/>). The body
/// is the message with its values and the exception text; the prefix (time,
/// level, category) is left alone, so a category such as
/// <c>AccessTokenIssuer:</c> is not read as a setting whose value follows.
/// </summary>
internal sealed class RedactingFormatter(ITextFormatter prefix, ITextFormatter body) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        prefix.Format(logEvent, output);
        using var rendered = new StringWriter(output.FormatProvider);
        body.Format(logEvent, rendered);
        output.Write(Redaction.Redact(rendered.ToString()));
    }
}
