using Serilog.Core;
using Serilog.Events;

namespace Simple.Logging;

/// <summary>
/// Replaces the value of every property whose name says it is a credential,
/// for example <c>{PairingCode}</c> or <c>{Password}</c>: such a value shows
/// in the message on its own, with nothing around it that
/// <see cref="Redaction.Redact"/> could recognise.
/// </summary>
internal sealed class RedactingEnricher : ILogEventEnricher
{
    private static readonly LogEventPropertyValue Masked = new ScalarValue(Redaction.Mask);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var name in logEvent.Properties.Keys.Where(Redaction.IsSecretName).ToList())
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, Masked));
        }
    }
}
