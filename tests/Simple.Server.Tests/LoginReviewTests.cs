using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Simple.Logging;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>
/// Logging in again on a computer that is still logged in, what an
/// anonymous login request can make the server write, and the edges of
/// who-may-use-what (review of #77).
/// </summary>
public sealed class LoginReviewTests : IAsyncLifetime
{
    private const string Password = "password-1234";

    private readonly ServerInstance server = new();
    private HttpClient admin = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        admin = server.Start();
        var created = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "p1-engineer", role = "engineer", project = "P1", password = Password }, Cancel);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task LoggingInOnAComputerThatIsStillLoggedInReplacesItsLogin()
    {
        var first = await server.LogInAsync("admin", ServerInstance.AdminPassword);
        using var stillLoggedIn = server.WithCookie(first.Cookie!);

        var second = await stillLoggedIn.PostAsJsonAsync(
            new Uri("/api/v1/login", UriKind.Relative), new { name = "p1-engineer", password = Password, entry = "console" }, Cancel);
        var cookie = TestLogin.CookieOf(second);
        using var replaced = server.WithCookie(cookie!);
        var session = await replaced.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/session", UriKind.Relative), Cancel);
        using var oldCopy = server.WithCookie(first.Cookie!);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(first.Cookie, cookie);
        Assert.Equal("p1-engineer", session.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldCopy.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task LoggingInThroughTheConsoleOnAComputerLoggedInThroughMcpTakesTheConsoleEntry()
    {
        var mcp = await server.LogInAsync("admin", ServerInstance.AdminPassword, "mcp");
        using var stillLoggedIn = server.WithCookie(mcp.Cookie!);

        var console = await stillLoggedIn.PostAsJsonAsync(
            new Uri("/api/v1/login", UriKind.Relative), new { name = "admin", password = ServerInstance.AdminPassword, entry = "console" }, Cancel);
        using var replaced = server.WithCookie(TestLogin.CookieOf(console)!);

        Assert.Equal(HttpStatusCode.OK, (await replaced.GetAsync(new Uri("/api/v1/accounts", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task AFailedLoginOnAComputerThatIsLoggedInLeavesItsLoginAlone()
    {
        var first = await server.LogInAsync("admin", ServerInstance.AdminPassword);
        using var stillLoggedIn = server.WithCookie(first.Cookie!);

        var wrong = await stillLoggedIn.PostAsJsonAsync(
            new Uri("/api/v1/login", UriKind.Relative), new { name = "admin", password = "not-the-password", entry = "console" }, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await stillLoggedIn.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/session/password")]
    [InlineData("DELETE", "/api/v1/accounts/p1-engineer")]
    [InlineData("PATCH", "/api/v1/accounts/p1-engineer")]
    [InlineData("POST", "/api/v1/logout")]
    public async Task EveryChangingMethodNeedsTheRequestHeader(string method, string path)
    {
        using var noHeader = server.CreateClient();
        noHeader.DefaultRequestHeaders.Add("Cookie", admin.DefaultRequestHeaders.GetValues("Cookie"));
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };

        var response = await noHeader.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(RequestHeader.Name, await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNameWithALineBreakOrTooLongIsRefusedAndAnUnknownNameIsNotLogged()
    {
        var forged = await server.LogInAsync("admin\r\n2026-10-18 08:00:00.000 +08:00 [INF] forged line", "whatever-1");
        var tooLong = await server.LogInAsync(new string('a', Account.NameMaxLength + 1), "whatever-1");
        var unknown = await server.LogInAsync("my-password-typed-in-the-name-box", "whatever-1");
        await server.StopAsync();
        var log = ProgramLog.ReadText(server.LogDirectory);

        Assert.Equal(HttpStatusCode.Unauthorized, forged.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tooLong.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Response.StatusCode);
        Assert.DoesNotContain("forged line", log, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', Account.NameMaxLength + 1), log, StringComparison.Ordinal);
        Assert.DoesNotContain("my-password-typed-in-the-name-box", log, StringComparison.Ordinal);
        Assert.Contains("Failed login as an unknown account through console", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoginRequestLargerThanANameAndAPasswordNeedIsRefused()
    {
        await using var real = new ServerInstance(kestrel: true);
        real.Start();

        var large = await real.LogInAsync("admin", new string('x', (int)LoginSetup.PasswordRequestSizeLimit));
        var normal = await real.LogInAsync("admin", ServerInstance.AdminPassword);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, normal.Response.StatusCode);
    }

    [Fact]
    public async Task ARoleThatIsNoneOfTheFourIsRefused()
    {
        var response = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "someone", role = 7, project = "P1", password = Password }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ALoginCheckedAgainstTheOldPasswordDoesNotOutliveANewOne()
    {
        // A login whose password was checked just before the password changed
        // and whose record was written just after.
        var registry = server.Services.GetRequiredService<LoginRegistry>();
        var engineer = registry.Accounts().Single(a => a.Name == "p1-engineer");
        var before = engineer.CredentialVersion;
        var set = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);

        var key = await registry.AddAsync(engineer.Id, LoginEntries.Console, null, DateTimeOffset.UtcNow, before, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Null(registry.Check(key));
    }

    [Fact]
    public void OnlyTheAdrsExceptionsAreOpenAndTheTerminalAccessOnlyOnTheTerminalProtocol()
    {
        var endpoints = server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.All(endpoints, e =>
        {
            var access = e.Metadata.GetMetadata<AccessMetadata>()!.Access;
            var open = e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            var terminal = e.Metadata.GetMetadata<Grpc.AspNetCore.Server.GrpcMethodMetadata>() is not null;
            Assert.True(open == access is Access.Anonymous or Access.OnboardTerminal, $"{e.DisplayName}: open {open}, access {access}");
            Assert.True(access != Access.OnboardTerminal || terminal, $"{e.DisplayName} is marked for terminals but is not the terminal protocol");
        });
    }

    [Fact]
    public void MarkingAnOrdinaryEndpointForTerminalsFailsWhenTheServerBuildsIt()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapGet("/not-the-terminal-protocol", () => "x").RequireAccess(Access.OnboardTerminal);

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
        });
    }
}
