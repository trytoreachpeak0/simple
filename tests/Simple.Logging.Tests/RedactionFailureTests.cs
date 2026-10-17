using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Simple.Logging.Tests;

/// <summary>
/// When redaction itself fails, nothing unredacted is written and no line is
/// lost. The failures are made with <see cref="Redaction.TestHook"/>, which is
/// process-wide, so these tests run alone.
/// </summary>
[Collection(nameof(RedactionTimeTests))]
public sealed partial class RedactionFailureTests : IDisposable
{
    [Fact]
    public void ATextWhoseRedactionTimesOutIsReplacedWhole()
    {
        Redaction.TestHook = () => throw new RegexMatchTimeoutException("Server=db01;Password=Slow-secret", "slow", TimeSpan.FromSeconds(1));

        Assert.Equal(Redaction.TimedOut, Redaction.Redact("Server=db01;Password=Slow-secret"));
    }

    [Fact]
    public void ANameWhoseCheckTimesOutCountsAsACredential()
    {
        Redaction.TestHook = () => throw new RegexMatchTimeoutException("User", "slow", TimeSpan.FromSeconds(1));

        Assert.True(Redaction.IsSecretName("User"));
    }

    [Fact]
    public void ALineThatCannotBeRedactedIsWrittenWithoutItsBodyAndTheNextLineIsWhole()
    {
        using var log = new TestLog();
        Redaction.TestHook = () => throw new InvalidOperationException("broken");
        Said(log.Logger, "Server=db01;Password=Broken-secret");
        Redaction.TestHook = null;
        Said(log.Logger, "after");

        var lines = log.Text().Split(Environment.NewLine);
        Assert.Contains(lines, line => line.EndsWith("[INF] Simple.Logging.Tests: " + RedactingFormatter.NotRedacted, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith("[INF] Simple.Logging.Tests: after", StringComparison.Ordinal) && line.IndexOf("[INF]", StringComparison.Ordinal) == line.LastIndexOf("[INF]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Broken-secret", StringComparison.Ordinal));
    }

    public void Dispose() => Redaction.TestHook = null;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Text}")]
    private static partial void Said(ILogger logger, string text);
}
