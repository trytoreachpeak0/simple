using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>Who may use which endpoint (ADR 0014, 0016, 0033, 0045).</summary>
public sealed class AccessTests : IAsyncLifetime
{
    private const string Password = "password-1234";

    private readonly ServerInstance server = new();
    private HttpClient admin = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        admin = server.Start();
        await CreateAsync("p1-team-leader", "team-leader", "P1");
        await CreateAsync("p1-engineer", "engineer", "P1");
        await CreateAsync("watch", "watch", null);
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public void EveryEndpointNamesWhoMayUseIt()
    {
        var endpoints = server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.True(
            e.Metadata.GetMetadata<AccessMetadata>() is not null,
            $"{e.DisplayName} ({e.RoutePattern.RawText}) names no Access: add .RequireAccess(...)"));
    }

    [Fact]
    public void OnlyTheAdrsExceptionsNeedNoLogin()
    {
        var anonymous = server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<AccessMetadata>()?.Access == Access.Anonymous)
            .Select(e => e.RoutePattern.RawText)
            .Order();

        Assert.Equal(["/api/v1/login", "/health/live"], anonymous);
    }

    // ADR 0014's ranks, ADR 0016's MCP boundary and ADR 0033's view-only watch account.
    [Theory]
    [InlineData(Access.View, "team-leader engineer admin watch | team-leader engineer admin watch")]
    [InlineData(Access.AnyLogin, "team-leader engineer admin watch | team-leader engineer admin watch")]
    [InlineData(Access.ViewAll, "admin watch | admin watch")]
    [InlineData(Access.TeamLeader, "team-leader engineer admin | ")]
    [InlineData(Access.Engineer, "engineer admin | ")]
    [InlineData(Access.Configure, "admin | admin")]
    [InlineData(Access.ManageAccounts, "admin | ")]
    [InlineData(Access.ChangeOwnPassword, "team-leader engineer admin | ")]
    public void EachAccessAdmitsTheRolesAndEntriesOfTheAdrs(Access access, string admitted)
    {
        var parts = admitted.Split('|', StringSplitOptions.TrimEntries);
        var roles = new[] { AccountRole.TeamLeader, AccountRole.Engineer, AccountRole.Admin, AccountRole.Watch };
        string Name(AccountRole role) => role switch
        {
            AccountRole.TeamLeader => "team-leader",
            AccountRole.Engineer => "engineer",
            AccountRole.Admin => "admin",
            _ => "watch",
        };

        var console = roles.Where(r => AccessHandler.Admits(access, r, LoginEntries.Console)).Select(Name);
        var mcp = roles.Where(r => AccessHandler.Admits(access, r, LoginEntries.Mcp)).Select(Name);

        Assert.Equal(parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries), console);
        Assert.Equal(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), mcp);
    }

    [Theory]
    [InlineData("p1-team-leader", "console", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest)]
    [InlineData("p1-engineer", "console", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest)]
    [InlineData("admin", "console", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadRequest)]
    [InlineData("admin", "mcp", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("watch", "console", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("watch", "mcp", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task RealRequestsAreAdmittedOrRefusedByAccess(
        string account, string entry, HttpStatusCode view, HttpStatusCode manageAccounts, HttpStatusCode changeOwnPassword)
    {
        var login = await server.LogInAsync(account, account == "admin" ? ServerInstance.AdminPassword : Password, entry);
        var client = login.Client!;

        var status = await client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        var accounts = await client.GetAsync(new Uri("/api/v1/accounts", UriKind.Relative), Cancel);
        // A wrong old password: 400 where the endpoint is open to the login, 403 where it is not.
        var password = await client.PutAsJsonAsync(
            new Uri("/api/v1/session/password", UriKind.Relative), new { oldPassword = "not-the-old-one", newPassword = "new-password-1" }, Cancel);

        Assert.Equal(view, status.StatusCode);
        Assert.Equal(manageAccounts, accounts.StatusCode);
        Assert.Equal(changeOwnPassword, password.StatusCode);
    }

    [Fact]
    public async Task WhileOnDutyWatchingIsPausedTheWatchAccountGetsPausedForEverythingItsLoginIncluded()
    {
        var watch = (await server.LogInAsync("watch", Password, "mcp")).Client!;

        await SetPausedAsync(true);
        var whilePaused = await watch.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        var loginWhilePaused = await server.LogInAsync("watch", Password, "mcp");
        await SetPausedAsync(false);
        var resumed = await watch.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, whilePaused.StatusCode);
        Assert.Equal(AccessHandler.WatchPaused, (await whilePaused.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, loginWhilePaused.Response.StatusCode);
        Assert.Equal(AccessHandler.WatchPaused, (await loginWhilePaused.Response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("type").GetString());
        Assert.Null(TestLogin.SetCookieOf(loginWhilePaused.Response));
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
    }

    [Fact]
    public async Task APausedWatchAccountWithAWrongPasswordIsToldOnlyThatTheLoginFailed()
    {
        await SetPausedAsync(true);

        var login = await server.LogInAsync("watch", "not-the-password", "mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, login.Response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheWatchAccountHasTheOnDutyPause()
    {
        var response = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/paused", UriKind.Relative), new { paused = true }, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task SetPausedAsync(bool paused)
    {
        var response = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/watch/paused", UriKind.Relative), new { paused }, Cancel);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task CreateAsync(string name, string role, string? project)
    {
        var response = await admin.PostAsJsonAsync(new Uri("/api/v1/accounts", UriKind.Relative), new { name, role, project, password = Password }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
