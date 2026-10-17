namespace Simple.Logging.Tests;

/// <summary>
/// Redaction runs on the thread that logs (a UI thread included), so a long
/// text must not make it slow. Timed alone, not next to other tests.
/// </summary>
[Collection(nameof(RedactionTimeTests))]
public sealed class RedactionTimeTests
{
    // No pattern may try every start position against the rest of the line.
    [Theory]
    [InlineData("hex")]
    [InlineData("bytes")]
    [InlineData("dotted")]
    [InlineData("colons")]
    [InlineData("base64")]
    [InlineData("quotes")]
    [InlineData("words")]
    [InlineData("pairing-spaces")]
    [InlineData("colons-then-password")]
    [InlineData("colons-then-key")]
    [InlineData("配对码-spaces")]
    public void AMegabyteLongValueIsRedactedWithinASecond(string shape)
    {
        var text = shape switch
        {
            "hex" => Convert.ToHexString(new byte[512 * 1024]),
            "bytes" => BitConverter.ToString(new byte[350 * 1024]),
            "dotted" => string.Concat(Enumerable.Repeat("simple.server.", 75_000)),
            "colons" => string.Concat(Enumerable.Repeat("Simple:Mes:", 95_000)),
            "base64" => Convert.ToBase64String(new byte[768 * 1024]),
            "quotes" => string.Concat(Enumerable.Repeat("\"a\":", 260_000)),
            "colons-then-password" => string.Concat(Enumerable.Repeat("a:", 512 * 1024)) + "password",
            "colons-then-key" => string.Concat(Enumerable.Repeat("a:", 512 * 1024)) + "keyx",
            "pairing-spaces" => "pairing code" + new string(' ', 1024 * 1024),
            "配对码-spaces" => "配对码" + new string(' ', 1024 * 1024),
            _ => string.Concat(Enumerable.Repeat("pass word token key ", 52_000)),
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(text, redacted);
    }

    // A long word with colons whose last part names a credential: each part is
    // checked on its own, not every prefix of the word again.
    [Fact]
    public void AMegabyteLongColonWordEndingInACredentialIsRedactedWithinASecond()
    {
        var text = string.Concat(Enumerable.Repeat("a:", 512 * 1024)) + "password:Long-colon-secret";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.EndsWith(":password:***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AMegabyteOfCredentialsOnOneLineIsRedactedWithinASecond()
    {
        var text = string.Concat(Enumerable.Repeat("Server=db01;Password=pw-secret;", 16_000))
            + string.Concat(Enumerable.Repeat("password=pw-secret x ", 12_000))
            + string.Concat(Enumerable.Repeat("{\"signingKey\": \"pw-secret\"} ", 7_500));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.DoesNotContain("pw-secret", redacted, StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(RedactionTimeTests), DisableParallelization = true)]
public sealed class RedactionTimeTestsRunAlone;
