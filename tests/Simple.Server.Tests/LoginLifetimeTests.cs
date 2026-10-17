using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>
/// How long a login lasts (ADR 0045): a cookie of seven days, renewed by the
/// first use of each day; a login not used for seven days is gone; a restart
/// and a new process keep every login.
/// </summary>
public sealed class LoginLifetimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstUseOfADayRenewsTheCookieForSevenDaysAndLaterUsesThatDayDoNot()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(time: time);
        server.Start();
        var login = await server.LogInAsync("admin", ServerInstance.AdminPassword);

        time.Advance(TimeSpan.FromHours(23));
        var sameDay = await StatusAsync(login.Client!);
        time.Advance(TimeSpan.FromHours(2));
        var nextDay = await StatusAsync(login.Client!);
        time.Advance(TimeSpan.FromHours(1));
        var laterThatDay = await StatusAsync(login.Client!);

        Assert.Equal(HttpStatusCode.OK, sameDay.StatusCode);
        Assert.Null(TestLogin.SetCookieOf(sameDay));
        Assert.Equal(HttpStatusCode.OK, nextDay.StatusCode);
        Assert.Equal(Start.AddHours(25).AddDays(7), ExpiresOf(nextDay));
        Assert.Null(TestLogin.SetCookieOf(laterThatDay));
    }

    [Fact]
    public async Task AComputerUsedEveryFewDaysStaysLoggedInWithoutAnyLimit()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(time: time);
        server.Start();
        var cookie = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Cookie!;

        for (var week = 0; week < 8; week++)
        {
            time.Advance(TimeSpan.FromDays(6));
            using var client = server.WithCookie(cookie);
            var response = await StatusAsync(client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = TestLogin.CookieOf(response) ?? cookie;
        }
    }

    [Fact]
    public async Task ALoginNotUsedForSevenDaysIsGone()
    {
        var time = new FakeTimeProvider(Start);
        await using var server = new ServerInstance(time: time);
        var admin = server.Start();
        var idle = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Client!;
        var before = await LoginsOfAdminAsync(admin);

        time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var response = await StatusAsync(idle);
        var fresh = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Client!;
        var after = await LoginsOfAdminAsync(fresh);

        Assert.Equal(2, before);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Both earlier logins went unused for seven days; only the fresh one is left.
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task ARestartedServerKeepsEveryLogin()
    {
        await using var server = new ServerInstance();
        server.Start();
        var cookie = (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Cookie!;

        await server.StopAsync();
        server.Start();
        using var client = server.WithCookie(cookie);

        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(client)).StatusCode);
    }

    [Fact]
    public async Task ADatabaseBrieflyOutOfReachLogsNobodyOut()
    {
        await using var server = new ServerInstance();
        var admin = server.Start();
        var database = $"Simple_{server.Instance}";

        await SetOnlineAsync(database, online: false);
        HttpResponseMessage whileOffline;
        try
        {
            // The status needs no database; the login is checked against the server's memory.
            whileOffline = await StatusAsync(admin);
        }
        finally
        {
            await SetOnlineAsync(database, online: true);
        }

        Assert.Equal(HttpStatusCode.OK, whileOffline.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(admin)).StatusCode);
    }

    [Fact]
    public async Task TheKeysThatEncryptLoginCookiesAreInTheDatabaseEachEncryptedWithDpapi()
    {
        await using var server = new ServerInstance();
        server.Start();

        await using var db = server.CreateDbContext();
        var keys = await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync(Cancel);

        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            Assert.Contains("<encryptedSecret", xml, StringComparison.Ordinal);
            Assert.Contains("DpapiXmlDecryptor", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        });
    }

    private static async Task SetOnlineAsync(string database, bool online)
    {
        await using var connection = new SqlConnection(ServerInstance.SqlServer);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = online
            ? $"ALTER DATABASE [{database}] SET ONLINE"
            : $"ALTER DATABASE [{database}] SET OFFLINE WITH ROLLBACK IMMEDIATE";
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private static Task<HttpResponseMessage> StatusAsync(HttpClient client) =>
        client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel);

    private static async Task<int> LoginsOfAdminAsync(HttpClient admin)
    {
        var accounts = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/accounts", UriKind.Relative), Cancel);
        return accounts.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "admin").GetProperty("logins").GetInt32();
    }

    private static DateTimeOffset ExpiresOf(HttpResponseMessage response)
    {
        var setCookie = TestLogin.SetCookieOf(response) ?? throw new InvalidOperationException("No login cookie was set.");
        var expires = setCookie.Split(';', StringSplitOptions.TrimEntries).Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return DateTimeOffset.Parse(expires["expires=".Length..], CultureInfo.InvariantCulture);
    }
}
