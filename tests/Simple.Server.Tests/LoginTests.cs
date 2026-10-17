using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>Logging in and out through the API, and what the login cookie looks like (ADR 0045).</summary>
public sealed class LoginTests : IAsyncLifetime
{
    private readonly ServerInstance server = new();
    private HttpClient admin = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        admin = server.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Theory]
    [InlineData("/api/v1/status")]
    [InlineData("/api/v1/events")]
    [InlineData("/api/v1/session")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/health/ready")]
    [InlineData("/no/such/path")]
    public async Task WithoutLoginOnlyTheExceptionsAnswer(string path)
    {
        using var anonymous = server.CreateClient();

        var response = await anonymous.GetAsync(new Uri(path, UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task TheProcessAliveHealthCheckNeedsNoLogin()
    {
        using var anonymous = server.CreateClient();

        var response = await anonymous.GetAsync(new Uri("/health/live", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheLoggedInAdministratorReadsStatusAndSeesWhoItIs()
    {
        var status = await admin.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        var session = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/session", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("admin", session.GetProperty("name").GetString());
        Assert.Equal("admin", session.GetProperty("role").GetString());
        Assert.Equal("console", session.GetProperty("entry").GetString());
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownAccountGetTheSameAnswerAndNoCookie()
    {
        var wrong = await server.LogInAsync("admin", "not-the-password");
        var unknown = await server.LogInAsync("nobody", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Response.StatusCode);
        var wrongBody = await wrong.Response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        var unknownBody = await unknown.Response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Equal(wrongBody.GetProperty("title").GetString(), unknownBody.GetProperty("title").GetString());
        Assert.Equal(
            wrongBody.EnumerateObject().Select(p => p.Name).Order(),
            unknownBody.EnumerateObject().Select(p => p.Name).Order());
        Assert.Null(TestLogin.SetCookieOf(wrong.Response));
    }

    [Fact]
    public async Task TheLoginCookieIsSecureHttpOnlyStrictHostOnlyAndLastsSevenDays()
    {
        var login = await server.LogInAsync("admin", ServerInstance.AdminPassword);

        var setCookie = TestLogin.SetCookieOf(login.Response);
        Assert.NotNull(setCookie);
        var attributes = setCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList();
        Assert.StartsWith("__Host-", LoginSetup.CookieName, StringComparison.Ordinal);
        Assert.Contains("secure", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("httponly", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("path=/", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.OrdinalIgnoreCase));
        var expires = DateTimeOffset.Parse(
            attributes.Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7).AddMinutes(5));
    }

    [Fact]
    public async Task AChangingRequestWithoutTheRequestHeaderIsRefused()
    {
        using var anonymous = server.CreateClient();

        var login = await anonymous.PostAsJsonAsync(
            new Uri("/api/v1/login", UriKind.Relative), new { name = "admin", password = ServerInstance.AdminPassword, entry = "console" }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.Contains(RequestHeader.Name, await login.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Null(TestLogin.SetCookieOf(login));
    }

    [Fact]
    public async Task LoggingOutEndsTheLoginAlsoForACopyOfItsCookie()
    {
        var login = await server.LogInAsync("admin", ServerInstance.AdminPassword);
        using var copy = server.WithCookie(login.Cookie!);

        var logout = await login.Client!.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), null, Cancel);
        var afterwards = await copy.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
        // The other login of the same account is untouched.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownEntryIsRefused()
    {
        var login = await server.LogInAsync("admin", ServerInstance.AdminPassword, entry: "browser");

        Assert.Equal(HttpStatusCode.BadRequest, login.Response.StatusCode);
    }

    [Fact]
    public async Task TheServerAllowsNoCrossOriginCallsAndSendsNoHsts()
    {
        using var anonymous = server.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/login");
        preflight.Headers.Add("Origin", "https://elsewhere.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", RequestHeader.Name);

        var response = await anonymous.SendAsync(preflight, Cancel);
        var status = await admin.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(status.Headers.Contains("Strict-Transport-Security"));
    }
}
