using Microsoft.Extensions.Logging;

namespace Simple.Logging.Tests;

/// <summary>
/// Credentials never reach a program log (ADR 0034): whatever a program logs,
/// the file holds the rest of the line but not the secret.
/// </summary>
public sealed partial class RedactionTests
{
    [Fact]
    public void APasswordInAConnectionStringInTheMessageIsNotWritten()
    {
        using var log = new TestLog();

        Connecting(log.Logger, "Server=db01;User ID=simple;Password=Hunter2-secret;Encrypt=true");

        var text = log.Text();
        Assert.DoesNotContain("Hunter2-secret", text, StringComparison.Ordinal);
        Assert.Contains("Server=db01;User ID=simple;Password=", text, StringComparison.Ordinal);
        Assert.Contains(";Encrypt=true", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PairingCode")]
    [InlineData("Password")]
    [InlineData("ApiKey")]
    [InlineData("AccessToken")]
    [InlineData("ClientSecret")]
    public void AValueLoggedUnderASecretNameIsNotWritten(string name)
    {
        using var log = new TestLog();

        var shows = LoggerMessage.Define<string>(LogLevel.Information, default, $"Terminal vehicle-2 shows {{{name}}}");
        shows(log.Logger, "482913", null);

        var text = log.Text();
        Assert.DoesNotContain("482913", text, StringComparison.Ordinal);
        Assert.Contains($"Terminal vehicle-2 shows {Redaction.Mask}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialInAnExceptionAndItsInnerExceptionIsNotWritten()
    {
        using var log = new TestLog();
        var inner = new InvalidOperationException("Login failed for Server=db01;User ID=simple;Pwd=Inner-secret");
        var outer = new HttpRequestException("GET https://simple:Outer-secret@mes.example/api failed", inner);

        Failed(log.Logger, outer);

        var text = log.Text();
        Assert.DoesNotContain("Inner-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Outer-secret", text, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException: GET https://simple:***@mes.example/api failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: Login failed for Server=db01;User ID=simple;Pwd=***", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--Simple:Mes:Password=Cmd-secret --urls=http://127.0.0.1:5000", "Cmd-secret", "--Simple:Mes:Password=***")]
    [InlineData("GET /api?api_key=Query-secret", "Query-secret", "/api?api_key=***")]
    [InlineData("""settings {"user":"simple","password":"Json-secret"}""", "Json-secret", """{"user":"simple","password":"***"}""")]
    [InlineData("""{"pairingCode": 482913}""", "482913", """{"pairingCode": ***}""")]
    [InlineData("Authorization: Bearer abc.Header-secret.def", "Header-secret", "Authorization: Bearer ***")]
    [InlineData("X-Api-Key: Header-key-secret", "Header-key-secret", "X-Api-Key: ***")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzaW1wbGUifQ.Jwt-secret_sig", "Jwt-secret", "token ***")]
    [InlineData("key -----BEGIN RSA PRIVATE KEY-----\nMIIBPem-secret\n-----END RSA PRIVATE KEY----- loaded", "Pem-secret", "key *** loaded")]
    [InlineData("pushed with ghp_0123456789abcdefghijABCDEFGHIJ0123456789", "ghp_0123456789", "pushed with ***")]
    [InlineData("vehicle-2 shows pairing code 482913", "482913", "vehicle-2 shows pairing code ***")]
    [InlineData("vehicle-2 配对码：482913", "482913", "vehicle-2 配对码：***")]
    [InlineData("Authorization: Token Token-secret", "Token-secret", "Authorization: Token ***")]
    [InlineData("Authorization: ApiKey ApiKey-secret", "ApiKey-secret", "Authorization: ApiKey ***")]
    [InlineData("Cookie: .AspNetCore.Session=Cookie-secret; theme=dark", "Cookie-secret", "Cookie: ***")]
    [InlineData("Set-Cookie: session=SetCookie-secret; Path=/", "SetCookie-secret", "Set-Cookie: ***")]
    [InlineData("--Simple:Riot:Key=RiotKey-secret --urls=http://127.0.0.1:5000", "RiotKey-secret", "--Simple:Riot:Key=*** --urls=http://127.0.0.1:5000")]
    [InlineData("SigningKey=Signing-secret", "Signing-secret", "SigningKey=***")]
    [InlineData("--Simple:Mes:Password Spaced-secret --urls=http://127.0.0.1:5000", "Spaced-secret", "--Simple:Mes:Password *** --urls=http://127.0.0.1:5000")]
    [InlineData("Server=db01;Password=\"ab\"\"Quoted-secret\";Encrypt=true", "Quoted-secret", "Server=db01;Password=***;Encrypt=true")]
    [InlineData("Driver={ODBC};PWD={ab;Braced-secret};UID=simple", "Braced-secret", "Driver={ODBC};PWD=***;UID=simple")]
    [InlineData("Server=db01;Password=with space-secret", "space-secret", "Server=db01;Password=***")]
    [InlineData("GET https://simple:p@ss-At-secret@mes.example/api", "At-secret", "GET https://simple:***@mes.example/api")]
    [InlineData("key -----BEGIN PRIVATE KEY-----\nMIIBCut-secret", "Cut-secret", "key ***")]
    public void ACredentialInFreeTextIsNotWritten(string message, string secret, string kept)
    {
        using var log = new TestLog();

        Said(log.Logger, message);

        var text = log.Text();
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains(kept, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Connected to https://127.0.0.1:5181 after 12 ms")]
    [InlineData("--Simple:OnboardTerminals:ServerKeyName=Simple-Server-29-simple-cli")]
    [InlineData("serverCertificatePin 3F2A9C0B7D1E4F5A6B8C9D0E1F2A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true")]
    [InlineData("Round 42: read 17 rows; lot LOT123 at EQP-01")]
    // Business values are often digits, a 6-digit one included: only a pairing
    // code named as such is taken out, never a number for looking like one.
    [InlineData("Sublot 482913.01 for EQP 100234 at station 300512, task 654321, vehicle 2")]
    [InlineData("SAMPLE_TO_OVEN SMP0001.01 S1-1 SMP-DA-01 SMP-CURE 2026-01-05T08:00:00 SMP-PKG-A")]
    [InlineData("""{"taskType":"SAMPLE_TO_GATE","sublot":"482913.01","eqp":"100234","area":"S2-1"}""")]
    [InlineData("Scan 482913 received from vehicle-2; code 123456 is not a known lot")]
    // Words that only look like credential markers stay.
    [InlineData("Basic check passed for vehicle-2")]
    [InlineData("Bearer of LOT 482913 is vehicle-2")]
    [InlineData("Simple.Server.TokenExpiredException: The terminal must pair again")]
    public void AnOrdinaryLineIsWrittenAsItIs(string message)
    {
        using var log = new TestLog();

        Said(log.Logger, message);

        Assert.Contains(message, log.Text(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET /login?password=x&user=y took 12 ms", "GET /login?password=***&user=y took 12 ms")]
    [InlineData("{ Password = x, User = simple }", "{ Password = ***, User = simple }")]
    [InlineData("Server=db01;User ID=simple;Password=a b;Encrypt=true", "Server=db01;User ID=simple;Password=***;Encrypt=true")]
    public void OnlyTheValueGoes(string text, string expected) => Assert.Equal(expected, Redaction.Redact(text));

    /// <summary>
    /// Redaction runs on the thread that logs (a UI thread included), so a
    /// long value must not make it slow: no pattern may try every start
    /// position against the rest of the line.
    /// </summary>
    [Theory]
    [InlineData("hex")]
    [InlineData("bytes")]
    [InlineData("dotted")]
    [InlineData("colons")]
    [InlineData("base64")]
    [InlineData("quotes")]
    [InlineData("words")]
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
            _ => string.Concat(Enumerable.Repeat("pass word token key ", 52_000)),
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{text.Length} characters took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(text, redacted);
    }

    [Fact]
    public void ACategoryNamedLikeACredentialDoesNotHideTheMessage()
    {
        using var log = new TestLog();

        var issuer = log.CreateLogger("Simple.Server.AccessTokenIssuer");
        Said(issuer, "Issued for vehicle-2");

        Assert.Contains("Simple.Server.AccessTokenIssuer: Issued for vehicle-2", log.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialInsideALoggedObjectIsNotWritten()
    {
        using var log = new TestLog();

        var login = new GatewayLogin("simple", "Object-secret");
        Settings(log.Logger, login);

        var text = log.Text();
        Assert.DoesNotContain("Object-secret", text, StringComparison.Ordinal);
        Assert.Contains("simple", text, StringComparison.Ordinal);
    }

    private sealed record GatewayLogin(string User, string Password);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings {@Login}")]
    private static partial void Settings(ILogger logger, GatewayLogin login);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Text}")]
    private static partial void Said(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Error, Message = "The round failed")]
    private static partial void Failed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting with {Target}")]
    private static partial void Connecting(ILogger logger, string target);
}
