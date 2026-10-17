using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>
/// Every change to an account, each in one transaction with its entry in the
/// event record (ADR 0045: account actions are recorded, passwords never),
/// then applied to <see cref="LoginRegistry"/>. A new password or a deleted
/// account ends the account's every login at once.
/// </summary>
public sealed class AccountChanges(SimpleDbContext db, EventStore events, IPasswordHasher<Account> hasher, LoginRegistry registry)
{
    /// <summary>The administrator's account name; there is one, made by the server command.</summary>
    public const string AdminName = "admin";

    public async Task<AccountChange> CreateAsync(string name, AccountRole role, string? project, string password, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Accounts.AnyAsync(a => a.Name == name, cancellationToken))
        {
            return AccountChange.Conflict($"An account named {name} exists already.");
        }

        if (role == AccountRole.Watch && await db.Accounts.AnyAsync(a => a.Role == AccountRole.Watch, cancellationToken))
        {
            return AccountChange.Conflict("There is a watch account already; SIMPLE has one (ADR 0033).");
        }

        var account = new Account { Name = name, Role = role, Project = project, PasswordHash = string.Empty };
        account.PasswordHash = hasher.HashPassword(account, password);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        await events.AppendAsync(EventTypes.AccountCreated, actor, new { name, role, project }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        registry.Put(account, endLogins: false);
        return AccountChange.Done(account);
    }

    public async Task<AccountChange> DeleteAsync(string name, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Accounts.SingleOrDefaultAsync(a => a.Name == name, cancellationToken) is not { } account)
        {
            return AccountChange.NotFound(name);
        }

        if (account.Role == AccountRole.Admin)
        {
            return AccountChange.Conflict("The administrator's account cannot be deleted.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Its logins go with it (cascade).
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        await events.AppendAsync(EventTypes.AccountDeleted, actor, new { name = account.Name }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        registry.Remove(account.Id);
        return AccountChange.Done(account);
    }

    /// <summary>The administrator sets another account's password; the administrator's own is not set this way.</summary>
    public async Task<AccountChange> SetPasswordAsync(string name, string password, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Accounts.SingleOrDefaultAsync(a => a.Name == name, cancellationToken) is not { } account)
        {
            return AccountChange.NotFound(name);
        }

        if (account.Role == AccountRole.Admin)
        {
            return AccountChange.Conflict(
                "The administrator's password is changed by the administrator with the old one (PUT /api/v1/session/password), "
                + "or set on the server machine with the server command set-admin-password (ADR 0045).");
        }

        await SetPasswordAsync(account, password, actor, "administrator", cancellationToken);
        return AccountChange.Done(account);
    }

    /// <summary>An account changes its own password; false when the old one is wrong.</summary>
    public async Task<bool> ChangeOwnPasswordAsync(int accountId, string oldPassword, string newPassword, Actor actor, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, cancellationToken);
        if (hasher.VerifyHashedPassword(account, account.PasswordHash, oldPassword) == PasswordVerificationResult.Failed)
        {
            return false;
        }

        await SetPasswordAsync(account, newPassword, actor, "itself", cancellationToken);
        return true;
    }

    /// <summary>The administrator switches the on-duty pause of the watch account (ADR 0033).</summary>
    public async Task<AccountChange> SetPausedAsync(string name, bool paused, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Accounts.SingleOrDefaultAsync(a => a.Name == name, cancellationToken) is not { } account)
        {
            return AccountChange.NotFound(name);
        }

        if (account.Role != AccountRole.Watch)
        {
            return AccountChange.Conflict("Only the watch account has the on-duty pause.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        account.Paused = paused;
        await db.SaveChangesAsync(cancellationToken);
        await events.AppendAsync(EventTypes.AccountPauseSet, actor, new { name = account.Name, paused }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        registry.Put(account, endLogins: false);
        return AccountChange.Done(account);
    }

    /// <summary>
    /// Sets or creates the administrator's password: the server command only
    /// (ADR 0045). Its logins end in the database; a running server drops them
    /// with its next flush (<see cref="LoginHousekeeping.Period"/>).
    /// </summary>
    public async Task SetAdminPasswordAsync(string password, CancellationToken cancellationToken)
    {
        var actor = new Actor(null, EventSources.ServerCommand, null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var admin = await db.Accounts.SingleOrDefaultAsync(a => a.Role == AccountRole.Admin, cancellationToken);
        if (admin is null)
        {
            admin = new Account { Name = AdminName, Role = AccountRole.Admin, PasswordHash = string.Empty };
            admin.PasswordHash = hasher.HashPassword(admin, password);
            db.Accounts.Add(admin);
            await db.SaveChangesAsync(cancellationToken);
            await events.AppendAsync(EventTypes.AccountCreated, actor, new { name = admin.Name, role = admin.Role }, cancellationToken);
        }
        else
        {
            await WritePasswordAsync(admin.Id, hasher.HashPassword(admin, password), cancellationToken);
            await events.AppendAsync(EventTypes.AccountPasswordSet, actor, new { name = admin.Name, by = "server command" }, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetPasswordAsync(Account account, string password, Actor actor, string by, CancellationToken cancellationToken)
    {
        var hash = hasher.HashPassword(account, password);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await WritePasswordAsync(account.Id, hash, cancellationToken);
        await events.AppendAsync(EventTypes.AccountPasswordSet, actor, new { name = account.Name, by }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        registry.Put(changed, endLogins: true);
    }

    /// <summary>
    /// Writes a new password's hash and raises the credential version in one
    /// statement, from what the database holds, not from what was read before:
    /// two new passwords set at once each raise it, so a login checked against
    /// either of them does not outlive the other. Ends the account's logins
    /// in the database; returns the account as written.
    /// </summary>
    private async Task<Account> WritePasswordAsync(int accountId, string hash, CancellationToken cancellationToken)
    {
        await db.Accounts.Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.CredentialVersion, a => a.CredentialVersion + 1),
                cancellationToken);
        await db.Logins.Where(l => l.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId, cancellationToken);
    }
}

/// <summary>What became of an account change: done, or why not.</summary>
public sealed record AccountChange(Account? Account, int Status, string? Problem)
{
    public static AccountChange Done(Account account) => new(account, StatusCodes.Status200OK, null);

    public static AccountChange NotFound(string name) => new(null, StatusCodes.Status404NotFound, $"There is no account named {name}.");

    public static AccountChange Conflict(string problem) => new(null, StatusCodes.Status409Conflict, problem);
}
