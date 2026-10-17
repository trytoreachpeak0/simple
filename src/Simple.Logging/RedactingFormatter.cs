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
/// <remarks>
/// The body is redacted before anything is written, and a body that cannot
/// be redacted is replaced by <see cref="NotRedacted"/>: a line is never lost,
/// never run into the next one, and never written unredacted.
/// </remarks>
internal sealed class RedactingFormatter(ITextFormatter prefix, ITextFormatter body) : ITextFormatter
{
    public const string NotRedacted = "*** (not shown: the log line could not be redacted)";

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string redacted;
        try
        {
            using var rendered = new StringWriter(output.FormatProvider);
            body.Format(logEvent, rendered);
            redacted = Redaction.Redact(rendered.ToString());
        }
#pragma warning disable CA1031 // Whatever went wrong, the line is written, without what could not be redacted.
        catch (Exception)
#pragma warning restore CA1031
        {
            redacted = NotRedacted + output.NewLine;
        }

        prefix.Format(logEvent, output);
        output.Write(redacted);
    }
}
