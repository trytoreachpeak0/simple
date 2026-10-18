namespace Simple.Logging.Tests;

/// <summary>
/// Redaction runs on the thread that logs (a UI thread included), so a long
/// text must not make it slow. Timed alone, not next to other tests.
/// </summary>
[Collection(nameof(RedactionTimeTests))]
public sealed class RedactionTimeTests
{
    /// <summary>
    /// What these tests catch is a pattern that tries every start position
    /// against the rest of the line: quadratic in the length, minutes for a
    /// megabyte (#59: the first version did not finish this class in 5
    /// minutes; a 1 MB a:a:...:password took over 4). Done in linear time a
    /// megabyte takes well under a second (about 440 ms for the whole class,
    /// Debug, on a laptop), but 1.1 s on the CI machine while other jobs ran
    /// there (#73), so a one-second bound failed working code. Five seconds
    /// leaves room for a loaded machine and is still far below any quadratic
    /// version: the measured shapes need minutes at this length. Not a
    /// benchmark; the tests still run alone (the collection below).
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

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
    [InlineData("slash-at")]
    [InlineData("colon-slash-at")]
    public void AMegabyteLongValueIsRedactedInLinearTime(string shape)
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
            "slash-at" => string.Concat(Enumerable.Repeat("ab/c@ ", 175_000)),
            "colon-slash-at" => string.Concat(Enumerable.Repeat("a:b/", 262_144)) + "@ ",
            _ => string.Concat(Enumerable.Repeat("pass word token key ", 52_000)),
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < Bound, $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(text, redacted);
    }

    // A long word with colons whose last part names a credential: each part is
    // checked on its own, not every prefix of the word again.
    [Fact]
    public void AMegabyteLongColonWordEndingInACredentialIsRedactedInLinearTime()
    {
        var text = string.Concat(Enumerable.Repeat("a:", 512 * 1024)) + "password:Long-colon-secret";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < Bound, $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.EndsWith(":password:***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AMegabyteOfCredentialsOnOneLineIsRedactedInLinearTime()
    {
        var text = string.Concat(Enumerable.Repeat("Server=db01;Password=pw-secret;", 16_000))
            + string.Concat(Enumerable.Repeat("password=pw-secret x ", 12_000))
            + string.Concat(Enumerable.Repeat("{\"signingKey\": \"pw-secret\"} ", 7_500));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < Bound, $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.DoesNotContain("pw-secret", redacted, StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(RedactionTimeTests), DisableParallelization = true)]
public sealed class RedactionTimeTestsRunAlone;
