using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Simple.Logging;
using Simple.Server.Accounts;
using Simple.Server.Events;

namespace Simple.Server.Tests;

/// <summary>Managing accounts and passwords (ADR 0016, 0045): what changes, what ends, what is recorded.</summary>
public sealed class AccountTests : IAsyncLifetime
{
    private const string Password = "password-1234";
    private const string Workstation = "10.20.30.40";

    private readonly ServerInstance server = new();
    private HttpClient admin = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        server.Start();
        admin = (await server.LogInAsync("admin", ServerInstance.AdminPassword, remoteAddress: Workstation)).Client!;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task ANewPasswordEndsEveryLoginOfThatAccountAndNoOther()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");
        await CreateAsync("p1-team-leader", "team-leader", "P1");
        var first = (await server.LogInAsync("p1-engineer", Password, remoteAddress: "10.0.0.1")).Client!;
        var second = (await server.LogInAsync("p1-engineer", Password, remoteAddress: "10.0.0.2")).Client!;
        var other = (await server.LogInAsync("p1-team-leader", Password)).Client!;

        var set = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusOf(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusOf(second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatusOf(other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LogInAsync("p1-engineer", Password)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("p1-engineer", "new-password-1")).Response.StatusCode);
    }

    [Fact]
    public async Task DeletingAnAccountEndsItsLogins()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");
        var engineer = (await server.LogInAsync("p1-engineer", Password)).Client!;

        var deleted = await admin.DeleteAsync(new Uri("/api/v1/accounts/p1-engineer", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusOf(engineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LogInAsync("p1-engineer", Password)).Response.StatusCode);
    }

    [Fact]
    public async Task AnAccountChangesItsOwnPasswordWithTheOldOneAndEveryLoginOfItEnds()
    {
        await CreateAsync("p1-team-leader", "team-leader", "P1");
        var here = (await server.LogInAsync("p1-team-leader", Password)).Client!;
        var elsewhere = (await server.LogInAsync("p1-team-leader", Password)).Client!;

        var wrong = await here.PutAsJsonAsync(
            new Uri("/api/v1/session/password", UriKind.Relative), new { oldPassword = "not-the-old-one", newPassword = "new-password-1" }, Cancel);
        var stillIn = await StatusOf(elsewhere);
        var changed = await here.PutAsJsonAsync(
            new Uri("/api/v1/session/password", UriKind.Relative), new { oldPassword = Password, newPassword = "new-password-1" }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusOf(here)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusOf(elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("p1-team-leader", "new-password-1")).Response.StatusCode);
    }

    [Fact]
    public async Task TheAdministratorsOwnAccountIsNeitherDeletedNorGivenAPasswordThroughTheApi()
    {
        var delete = await admin.DeleteAsync(new Uri("/api/v1/accounts/admin", UriKind.Relative), Cancel);
        var set = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/admin/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);
        var create = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "second-admin", role = "admin", password = Password }, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, set.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Response.StatusCode);
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("密码密码密码密码", true)]
    [InlineData("密码密码密码密", false)]
    public async Task APasswordNeedsEightCharactersAndNothingElse(string password, bool accepted)
    {
        var response = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "p1-engineer", role = "engineer", project = "P1", password }, Cancel);

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("team-leader", null)]
    [InlineData("engineer", "")]
    [InlineData("watch", "P1")]
    public async Task ARoleAccountBelongsToOneProjectAndTheWatchAccountToNone(string role, string? project)
    {
        var response = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "someone", role, project, password = Password }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ThereIsOneWatchAccountAndAccountNamesAreUnique()
    {
        await CreateAsync("watch", "watch", null);
        await CreateAsync("p1-engineer", "engineer", "P1");

        var secondWatch = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "watch-2", role = "watch", password = Password }, Cancel);
        var sameName = await admin.PostAsJsonAsync(
            new Uri("/api/v1/accounts", UriKind.Relative), new { name = "p1-engineer", role = "engineer", project = "P2", password = Password }, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, secondWatch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
    }

    [Fact]
    public async Task TheAccountListShowsWhenEachWasLastSeenAndHowManyLoginsItHasNow()
    {
        await CreateAsync("watch", "watch", null);
        var watch = (await server.LogInAsync("watch", Password, "mcp")).Client!;
        await StatusOf(watch);
        await watch.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), null, Cancel);
        await server.LogInAsync("admin", ServerInstance.AdminPassword);

        var accounts = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/accounts", UriKind.Relative), Cancel);

        var rows = accounts.EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        // The test client's login, AccountTests' own and the one just made.
        Assert.Equal(3, rows["admin"].GetProperty("logins").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows["admin"].GetProperty("paused").ValueKind);
        // Logged out: no login left, but when it last came is kept (ADR 0033, 0045).
        Assert.Equal(0, rows["watch"].GetProperty("logins").GetInt32());
        Assert.InRange(rows["watch"].GetProperty("lastSeenAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.False(rows["watch"].GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task AccountActionsAreRecordedWithAccountEntryAndComputerButNeverThePassword()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");
        await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "second-password" }, Cancel);
        await admin.DeleteAsync(new Uri("/api/v1/accounts/p1-engineer", UriKind.Relative), Cancel);

        var page = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        var raw = await admin.GetStringAsync(new Uri("/api/v1/events", UriKind.Relative), Cancel);

        var actions = page!.Items.Where(e => e.Account == "admin").ToList();
        Assert.Equal([EventTypes.AccountCreated, EventTypes.AccountPasswordSet, EventTypes.AccountDeleted], actions.Select(e => e.Type));
        Assert.All(actions, e =>
        {
            Assert.Equal(EventSources.Console, e.Source);
            Assert.Equal(Workstation, e.RemoteAddress);
            Assert.Equal("p1-engineer", e.Data!.Value.GetProperty("name").GetString());
        });
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("second-password", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordsAreStoredAsPbkdf2WithTwoHundredTwentyThousandIterations()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");

        await using var db = server.CreateDbContext();
        var hash = Convert.FromBase64String((await db.Accounts.SingleAsync(a => a.Name == "p1-engineer", Cancel)).PasswordHash);

        // Identity's V3 format: 0x01, the PRF (2 = HMAC-SHA512), the iteration count, big-endian.
        Assert.Equal(0x01, hash[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1)));
        Assert.Equal(220_000u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5)));
        Assert.DoesNotContain(Password, Convert.ToBase64String(hash), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderWeakerHashIsUpgradedAtTheNextLogin()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");
        await using (var db = server.CreateDbContext())
        {
            var account = await db.Accounts.SingleAsync(a => a.Name == "p1-engineer", Cancel);
            account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, Password);
            await db.SaveChangesAsync(Cancel);
        }

        var login = await server.LogInAsync("p1-engineer", Password);

        Assert.Equal(HttpStatusCode.OK, login.Response.StatusCode);
        await using var after = server.CreateDbContext();
        var hash = Convert.FromBase64String((await after.Accounts.SingleAsync(a => a.Name == "p1-engineer", Cancel)).PasswordHash);
        Assert.Equal(220_000u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5)));
    }

    [Fact]
    public async Task LoggingInAndOutAndFailingGoToTheProgramLogWithTheComputerAndNotToTheEventRecord()
    {
        await CreateAsync("p1-engineer", "engineer", "P1");
        var engineer = (await server.LogInAsync("p1-engineer", Password, remoteAddress: "10.0.0.9")).Client!;
        await engineer.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), null, Cancel);
        await server.LogInAsync("p1-engineer", "a-wrong-guess", remoteAddress: "10.0.0.66");

        var events = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        await server.StopAsync();
        var log = ProgramLog.ReadText(server.LogDirectory);

        Assert.Contains("p1-engineer logged in through console from 10.0.0.9", log, StringComparison.Ordinal);
        Assert.Contains("p1-engineer logged out (console) from 10.0.0.9", log, StringComparison.Ordinal);
        Assert.Contains("Failed login as p1-engineer through console from 10.0.0.66", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, log, StringComparison.Ordinal);
        Assert.DoesNotContain("a-wrong-guess", log, StringComparison.Ordinal);
        Assert.DoesNotContain(events!.Items, e => e.Type.Contains("login", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<HttpResponseMessage> StatusOf(HttpClient client) =>
        client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);

    private async Task CreateAsync(string name, string role, string? project)
    {
        var response = await admin.PostAsJsonAsync(new Uri("/api/v1/accounts", UriKind.Relative), new { name, role, project, password = Password }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
