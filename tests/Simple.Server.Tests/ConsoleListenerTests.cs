using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Simple.Logging;
using Simple.Server.Accounts;
using Simple.Server.Listeners;

namespace Simple.Server.Tests;

/// <summary>
/// The console's HTTPS address with the certificate the server makes, and the
/// vehicle overview's plain address (ADR 0046), on real sockets. The console
/// listens on 127.0.0.3 and the overview on 127.0.0.2 here, so that each
/// address is told apart from the loopback API on 127.0.0.1.
/// </summary>
public sealed class ConsoleListenerTests
{
    private const string ConsoleHost = "127.0.0.3";
    private const string OverviewHost = "127.0.0.2";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Listening(string consoleHost = ConsoleHost) => new()
    {
        ["Simple:Console:Url"] = $"https://{consoleHost}:0",
        ["Simple:Overview:Url"] = $"http://{OverviewHost}:0",
    };

    [Fact]
    public async Task TheConsoleAddressServesLoginAndTheApiOverTls12WithTheServersOwnCertificate()
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        var admin = server.Start();
        var console = await ConsoleOfAsync(admin);
        X509Certificate2? presented = null;
        using var handler = new HttpClientHandler
        {
            // A machine-wide proxy (simple01 has one) must not carry loopback test traffic.
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                presented = new X509Certificate2(certificate!);
                return true;
            },
        };
        using var https = new HttpClient(handler) { BaseAddress = console.Url };

        // A browser keeps a Secure cookie from an https:// address and sends it back.
        var login = await https.SendAsync(LoginRequest(), Cancel);
        var cookie = TestLogin.CookieOf(login);
        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        status.Headers.Add("Cookie", $"{LoginSetup.CookieName}={cookie}");
        var statusResponse = await https.SendAsync(status, Cancel);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.False(statusResponse.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal(console.Sha256, Convert.ToHexStringLower(SHA256.HashData(presented!.RawData)));
        Assert.Equal(SslProtocols.Tls12, await NegotiatedProtocolAsync(console.Url));
    }

    [Fact]
    public async Task TheConsoleCertificateIsNoAuthorityOnlyProvesThisServerAndLastsTwentyYears()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        await using var server = new ServerInstance(kestrel: true, time: time, extraSettings: Listening());
        var admin = server.Start();
        var console = await ConsoleOfAsync(admin);

        using var certificate = await PresentedCertificateAsync(console.Url);

        var basic = Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(basic.Critical);
        Assert.False(basic.CertificateAuthority);
        var usage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);
        var extended = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], extended.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        var names = Assert.Single(certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>());
        Assert.Contains(IPAddress.Parse(ConsoleHost), names.EnumerateIPAddresses());
        Assert.Contains(Dns.GetHostName(), names.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(time.GetUtcNow().AddYears(20).Date, certificate.NotAfter.ToUniversalTime().Date);
        Assert.Equal([ConsoleHost, Dns.GetHostName()], console.Names, StringComparer.OrdinalIgnoreCase);
        Assert.True(console.Serving);
        Assert.Null(console.Problem);
    }

    [Fact]
    public async Task TheCertificateStaysTheSameAcrossRestartsAndIsExportedForInstalling()
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        var before = await ConsoleOfAsync(server.Start());
        await server.StopAsync();
        var after = await ConsoleOfAsync(server.Start());
        var file = Path.Combine(server.LogDirectory, "console.cer");
        var output = new StringWriter();

        var exit = await ConsoleCertificateCommands.ExportAsync(server.Services, file, output, Cancel);

        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(0, exit);
        Assert.Equal(after.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, Cancel))));
    }

    [Fact]
    public async Task ACertificateForAnotherAddressClosesOnlyTheConsoleUntilItIsDiscarded()
    {
        await using var first = new ServerInstance(kestrel: true, extraSettings: Listening());
        var original = await ConsoleOfAsync(first.Start());
        await first.StopAsync();
        await using var moved = new ServerInstance(instance: first.Instance, kestrel: true, extraSettings: Listening("127.0.0.4"));

        // The server runs on (the product owner, 2026-10-18): status, login, terminals.
        var admin = moved.Start();
        var closed = await ConsoleOfAsync(admin);
        var handshake = await Record.ExceptionAsync(() => NegotiatedProtocolAsync(closed.Url));
        await ConsoleCertificateCommands.DiscardAsync(moved.Services, new StringWriter(), Cancel);
        await moved.StopAsync();
        var renewed = await ConsoleOfAsync(moved.Start());

        Assert.False(closed.Serving);
        Assert.Contains($"The console certificate is for {ConsoleHost}, ", closed.Problem, StringComparison.Ordinal);
        Assert.Contains(ConsoleCertificateCommands.DiscardName, closed.Problem, StringComparison.Ordinal);
        Assert.NotNull(handshake);
        Assert.Contains("The console is closed; the server runs on.", ProgramLog.ReadText(moved.LogDirectory), StringComparison.Ordinal);
        Assert.True(renewed.Serving);
        Assert.NotEqual(original.Sha256, renewed.Sha256);
        Assert.Contains("127.0.0.4", renewed.Names);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("another key")]
    public async Task AConsoleKeyThatIsGoneOrNotTheCertificatesClosesOnlyTheConsole(string what)
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        await ConsoleOfAsync(server.Start());
        await server.StopAsync();
        if (OperatingSystem.IsWindows())
        {
            Simple.Server.OnboardTerminals.ServerCertificate.Delete(server.Database.ConsoleKeyName, TestKeyStore.IsMachine);
            if (what == "another key")
            {
                Simple.Server.OnboardTerminals.ServerCertificate.OpenOrCreateKey(server.Database.ConsoleKeyName, TestKeyStore.IsMachine).Dispose();
            }
        }

        var admin = server.Start();
        var closed = await ConsoleOfAsync(admin);

        Assert.False(closed.Serving);
        Assert.Contains($"The console certificate's key {server.Database.ConsoleKeyName} is not in the", closed.Problem, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task ACertificateForAnotherMachineNameClosesOnlyTheConsole()
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        await ConsoleOfAsync(server.Start());
        await server.StopAsync();
        if (OperatingSystem.IsWindows())
        {
            using var key = Simple.Server.OnboardTerminals.ServerCertificate.OpenOrCreateKey(server.Database.ConsoleKeyName, TestKeyStore.IsMachine);
            using var other = ConsoleCertificate.Create(key, IPAddress.Parse(ConsoleHost), "another-pc", DateTimeOffset.UtcNow);
            await using var db = server.CreateDbContext();
            var stored = await db.ConsoleCertificates.SingleAsync(Cancel);
            db.ConsoleCertificates.Remove(stored);
            await db.SaveChangesAsync(Cancel);
            db.ConsoleCertificates.Add(new ConsoleCertificateRecord { Certificate = other.RawData, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Cancel);
        }

        var closed = await ConsoleOfAsync(server.Start());

        Assert.False(closed.Serving);
        Assert.Contains($"The console certificate is for {ConsoleHost}, another-pc, but the server is {Dns.GetHostName()}", closed.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusWarnsNinetyDaysBeforeTheCertificateExpires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        await using var server = new ServerInstance(kestrel: true, time: time, extraSettings: Listening());
        var admin = server.Start();
        var fresh = await ConsoleOfAsync(admin);

        time.SetUtcNow(fresh.NotAfter.AddDays(-91));
        admin = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Client!;
        var early = await ConsoleOfAsync(admin);
        time.SetUtcNow(fresh.NotAfter.AddDays(-90));
        admin = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Client!;
        var ninety = await ConsoleOfAsync(admin);
        time.SetUtcNow(fresh.NotAfter.AddDays(-89));
        admin = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Client!;
        var soon = await ConsoleOfAsync(admin);

        Assert.False(fresh.ExpiresSoon);
        Assert.False(early.ExpiresSoon);
        Assert.Equal(91, early.DaysLeft);
        Assert.True(ninety.ExpiresSoon);
        Assert.Equal(90, ninety.DaysLeft);
        Assert.True(soon.ExpiresSoon);
        Assert.Equal(89, soon.DaysLeft);
    }

    [Theory]
    [InlineData("/api/v1/status")]
    [InlineData("/api/v1/session")]
    [InlineData("/health/live")]
    [InlineData("/openapi/v1.json")]
    public async Task ThePlainOverviewAddressAnswersNothingButTheOverview(string path)
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        var admin = server.Start();
        var overview = (await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/status", UriKind.Relative), Cancel))
            .GetProperty("overviewEndpoint").GetProperty("url").GetString()!;
        using var plain = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(overview) };

        var get = await plain.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        using var loginRequest = LoginRequest();
        var login = await plain.SendAsync(loginRequest, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Null(TestLogin.SetCookieOf(login));
    }

    [Fact]
    public async Task TheConsoleAddressDoesNotServeTheTerminalProtocol()
    {
        await using var server = new ServerInstance(kestrel: true, extraSettings: Listening());
        var console = await ConsoleOfAsync(server.Start());
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null };
        using var https = new HttpClient(handler) { BaseAddress = console.Url };
        using var grpc = new HttpRequestMessage(HttpMethod.Post, "/simple.protocol.v1.OnboardLink/Connect")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        grpc.Headers.Add(RequestHeader.Name, "1");
        grpc.Content = new ByteArrayContent([]);
        grpc.Content.Headers.ContentType = new("application/grpc");

        var response = await https.SendAsync(grpc, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage LoginRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login")
        {
            Content = JsonContent.Create(new { name = "admin", password = ServerInstance.AdminPassword, entry = "console" }),
        };
        request.Headers.Add(RequestHeader.Name, "1");
        return request;
    }

    private static async Task<ConsoleView> ConsoleOfAsync(HttpClient admin)
    {
        var status = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        var console = status.GetProperty("consoleEndpoint");
        var url = new Uri(console.GetProperty("url").GetString()!);
        var serving = console.GetProperty("serving").GetBoolean();
        var problem = console.GetProperty("problem").GetString();
        if (console.GetProperty("certificate") is not { ValueKind: JsonValueKind.Object } certificate)
        {
            return new ConsoleView(url, string.Empty, [], default, 0, false, serving, problem);
        }

        return new ConsoleView(
            url,
            certificate.GetProperty("sha256").GetString()!,
            certificate.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList(),
            certificate.GetProperty("notAfter").GetDateTimeOffset(),
            certificate.GetProperty("daysLeft").GetInt32(),
            certificate.GetProperty("expiresSoon").GetBoolean(),
            serving,
            problem);
    }

    private static async Task<X509Certificate2> PresentedCertificateAsync(Uri url)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(url.Host, url.Port, Cancel);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, Cancel);
        return new X509Certificate2(tls.RemoteCertificate!);
    }

    private static async Task<SslProtocols> NegotiatedProtocolAsync(Uri url)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(url.Host, url.Port, Cancel);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, Cancel);
        return tls.SslProtocol;
    }

    private sealed record ConsoleView(
        Uri Url, string Sha256, IReadOnlyList<string> Names, DateTimeOffset NotAfter, int DaysLeft, bool ExpiresSoon, bool Serving, string? Problem);
}
