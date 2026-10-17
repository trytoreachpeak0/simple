using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>
/// A login whose password is being checked while the password changes
/// (review of #77): the check is held in the <see cref="GatedPasswordHasher"/>,
/// the password changes, then the check goes on. The login must not outlive
/// the new password, and must not bring the old one back.
/// </summary>
public sealed class LoginRaceTests : IAsyncLifetime
{
    private const string Password = "password-1234";

    private readonly GatedPasswordHasher hasher = new();
    private readonly ServerInstance server;
    private HttpClient admin = null!;

    public LoginRaceTests() =>
        server = new ServerInstance(services: s => s.Replace(ServiceDescriptor.Singleton<IPasswordHasher<Account>>(hasher)));

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
    public async Task ALoginCheckedJustBeforeTheAdministratorSetsANewPasswordDoesNotLastAndChangesNothing()
    {
        var held = hasher.HoldNext(Password);
        var login = server.LogInAsync("p1-engineer", Password);
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(30), Cancel);

        var set = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);
        held.Release();
        var raced = await login;

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        // The login itself went through: its password was right when checked.
        Assert.Equal(HttpStatusCode.OK, raced.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raced.Client!.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
        // The server still knows the new password's version: a login with it lasts.
        var fresh = await server.LogInAsync("p1-engineer", "new-password-1");
        Assert.Equal(HttpStatusCode.OK, (await fresh.Client!.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LogInAsync("p1-engineer", Password)).Response.StatusCode);
    }

    [Fact]
    public async Task AStrongerHashOfTheOldPasswordDoesNotOverwriteANewPasswordSetMeanwhile()
    {
        var held = hasher.HoldNext(Password, rehash: true);
        var login = server.LogInAsync("p1-engineer", Password);
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(30), Cancel);

        var set = await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);
        held.Release();
        var raced = await login;

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, raced.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LogInAsync("p1-engineer", Password)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("p1-engineer", "new-password-1")).Response.StatusCode);
    }

    [Fact]
    public async Task TwoNewPasswordsSetAtOnceEachEndTheLoginsBeforeThem()
    {
        // The administrator's new password is being hashed (the account already read)...
        var adminHash = hasher.HoldNextHash("admin-set-password");
        var adminSet = admin.PutAsJsonAsync(
            new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "admin-set-password" }, Cancel);
        await adminHash.Entered.WaitAsync(TimeSpan.FromSeconds(30), Cancel);

        // ...while the engineer changes its own, and logs in with that one, the
        // login checked but not yet written when the administrator's lands.
        var engineer = (await server.LogInAsync("p1-engineer", Password)).Client!;
        var own = await engineer.PutAsJsonAsync(
            new Uri("/api/v1/session/password", UriKind.Relative), new { oldPassword = Password, newPassword = "self-set-password" }, Cancel);
        var check = hasher.HoldNext("self-set-password");
        var login = server.LogInAsync("p1-engineer", "self-set-password");
        await check.Entered.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        adminHash.Release();
        var set = await adminSet;
        check.Release();
        var raced = await login;

        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, raced.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raced.Client!.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("p1-engineer", "admin-set-password")).Response.StatusCode);
    }

    [Fact]
    public async Task AnAccountStateReadBeforeANewPasswordDoesNotTakeTheRegistryBack()
    {
        var registry = server.Services.GetRequiredService<LoginRegistry>();
        Account stale;
        await using (var db = server.CreateDbContext())
        {
            stale = db.Accounts.Single(a => a.Name == "p1-engineer");
        }

        await admin.PutAsJsonAsync(new Uri("/api/v1/accounts/p1-engineer/password", UriKind.Relative), new { password = "new-password-1" }, Cancel);
        var current = registry.Account(stale.Id)!.CredentialVersion;
        registry.Put(stale, endLogins: false);

        Assert.Equal(stale.CredentialVersion + 1, current);
        Assert.Equal(current, registry.Account(stale.Id)!.CredentialVersion);
    }
}

/// <summary>
/// The framework's password hasher, except that a check of one chosen
/// password can be held until the test releases it, and can then answer
/// "correct, but rehash".
/// </summary>
public sealed class GatedPasswordHasher : IPasswordHasher<Account>
{
    private readonly PasswordHasher<Account> inner = new(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = LoginSetup.PasswordIterations }));

    private Gate? next;
    private Gate? nextHash;

    /// <summary>Holds the next check of this password.</summary>
    public Gate HoldNext(string password, bool rehash = false) => next = new Gate(password, rehash);

    /// <summary>Holds the next hashing of this password: a new password being set.</summary>
    public Gate HoldNextHash(string password) => nextHash = new Gate(password, rehash: false);

    public string HashPassword(Account user, string password)
    {
        if (nextHash is { } gate && gate.Password == password && Interlocked.CompareExchange(ref nextHash, null, gate) == gate)
        {
            gate.Hold();
        }

        return inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(Account user, string hashedPassword, string providedPassword)
    {
        var verdict = inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        if (next is { } gate && gate.Password == providedPassword && Interlocked.CompareExchange(ref next, null, gate) == gate)
        {
            gate.Hold();
            if (gate.Rehash && verdict != PasswordVerificationResult.Failed)
            {
                return PasswordVerificationResult.SuccessRehashNeeded;
            }
        }

        return verdict;
    }

    public sealed class Gate(string password, bool rehash)
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Password { get; } = password;

        public bool Rehash { get; } = rehash;

        public Task Entered => entered.Task;

        public void Release() => released.TrySetResult();

        internal void Hold()
        {
            entered.SetResult();
            released.Task.Wait(TimeSpan.FromSeconds(60));
        }
    }
}
