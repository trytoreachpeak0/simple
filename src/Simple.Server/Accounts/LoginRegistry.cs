using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>
/// Every account and every live login, held in memory (ADR 0045): each
/// request is checked against this copy, so a database that is briefly
/// unreachable logs nobody out. A login is written to the database before it
/// is added here and removed here first when it ends, so it takes effect at
/// once. What only changes with use (when a login or an account was last
/// seen, when a cookie was renewed) is written by <see cref="FlushAsync"/>,
/// which also drops logins the database no longer has (the administrator's
/// password set by the server command from another process) and logins not
/// seen for <see cref="Lifetime"/>. Logins are kept by the SHA-256 of their
/// key, here and in the database; only the cookie holds the key itself.
/// </summary>
/// <remarks>
/// Hand-written because ASP.NET Core has only the interface for a
/// server-side login store (<see cref="Microsoft.AspNetCore.Authentication.Cookies.ITicketStore"/>),
/// no implementation; Identity's own store is built for one account per
/// person and checks a new password only every 30 minutes (ADR 0045).
/// </remarks>
public sealed partial class LoginRegistry(IServiceScopeFactory scopes, TimeProvider time, ILogger<LoginRegistry> logger)
{
    /// <summary>A login not used for this long is gone (ADR 0045); its cookie lasts as long from its last renewal.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>A cookie this old is renewed for another <see cref="Lifetime"/> on its next use: once a day.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromDays(1);

    /// <summary>By key hash.</summary>
    private readonly ConcurrentDictionary<string, LoginState> logins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, AccountState> accounts = new();

    /// <summary>Key hashes of logins ended here whose row could not be deleted yet; <see cref="FlushAsync"/> tries again.</summary>
    private readonly ConcurrentDictionary<string, byte> pendingDeletes = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts additions here, so a flush drops only what was here before it
    /// read the database: a login or account added while it reads is not
    /// mistaken for one the database lost.
    /// </summary>
    private long version;

    /// <summary>Reads every account and login. Runs before the server takes requests.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        foreach (var account in await db.Accounts.AsNoTracking().ToListAsync(cancellationToken))
        {
            accounts[account.Id] = AccountState.From(account, 0);
        }

        foreach (var login in await db.Logins.AsNoTracking().ToListAsync(cancellationToken))
        {
            logins[login.KeyHash] = new LoginState(login, 0);
        }
    }

    /// <summary>The account as it is now, or null when it no longer exists.</summary>
    public AccountState? Account(int id) => accounts.GetValueOrDefault(id);

    /// <summary>Every account as it is now.</summary>
    public IReadOnlyList<AccountState> Accounts() => [.. accounts.Values];

    /// <summary>The SHA-256 of a login key, in lowercase hex: how the login is kept.</summary>
    public static string HashOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// The login behind a cookie's key, now marked as seen together with its
    /// account; null when it has ended, was not seen for <see cref="Lifetime"/>,
    /// or its password was checked before the account's latest new password.
    /// </summary>
    public (LoginState Login, AccountState Account)? Check(string key)
    {
        var hash = HashOf(key);
        if (!logins.TryGetValue(hash, out var login) || !accounts.TryGetValue(login.AccountId, out var account))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (now - login.LastSeenAt > Lifetime || login.CredentialVersion != account.CredentialVersion)
        {
            End(hash);
            return null;
        }

        login.Seen(now);
        account.Seen(now);
        return (login, account);
    }

    /// <summary>Records a new login in the database, then here; returns its key, for the cookie only.</summary>
    /// <param name="credentialVersion">The account's credential version the password was checked against.</param>
    public async Task<string> AddAsync(
        int accountId, string entry, string? remoteAddress, DateTimeOffset issuedAt, int credentialVersion, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new LoginRecord
        {
            KeyHash = HashOf(key),
            CredentialVersion = credentialVersion,
            AccountId = accountId,
            Entry = entry,
            RemoteAddress = remoteAddress,
            LoggedInAt = now,
            IssuedAt = issuedAt,
            LastSeenAt = now,
        };
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
            db.Logins.Add(record);
            await db.SaveChangesAsync(cancellationToken);
        }

        logins[record.KeyHash] = new LoginState(record, Interlocked.Increment(ref version));
        if (accounts.TryGetValue(accountId, out var account))
        {
            account.Seen(now);
        }

        return key;
    }

    /// <summary>The cookie of a login was renewed (once a day); written with the next flush.</summary>
    public void Renewed(string key, DateTimeOffset issuedAt)
    {
        if (logins.TryGetValue(HashOf(key), out var login))
        {
            login.Renewed(issuedAt);
        }
    }

    /// <summary>
    /// Ends one login (logging out, or logging in again on the same computer):
    /// at once here, then in the database. A database out of reach does not
    /// fail it: the next flush deletes the row. Until then, a restart of the
    /// server would read the row back and the login would live again.
    /// </summary>
    public async Task EndAsync(string key, CancellationToken cancellationToken)
    {
        End(HashOf(key));
        try
        {
            await DeletePendingAsync(cancellationToken);
        }
        catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            LogFlushFailed(logger, e);
        }
    }

    /// <summary>
    /// An account was created, or changed in the database: takes its new
    /// state. Its logins end when <paramref name="endLogins"/> (a new
    /// password, ADR 0045); the caller has deleted their rows already. A state
    /// read before the account's latest new password (a lower credential
    /// version) is ignored: a login that read the account just before the
    /// password changed must not bring the old version back.
    /// </summary>
    public void Put(Account account, bool endLogins)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (puts)
        {
            PutLocked(account, endLogins);
        }
    }

    private readonly Lock puts = new();

    private void PutLocked(Account account, bool endLogins)
    {
        var previous = accounts.GetValueOrDefault(account.Id);
        if (previous is not null && account.CredentialVersion < previous.CredentialVersion)
        {
            return;
        }

        var state = AccountState.From(account, Interlocked.Increment(ref version));
        if (previous?.LastSeenAt is { } seen && (state.LastSeenAt is null || seen > state.LastSeenAt))
        {
            state.Seen(seen);
        }

        accounts[account.Id] = state;
        if (endLogins)
        {
            DropLoginsOf(account.Id);
        }
    }

    /// <summary>An account was deleted from the database, its logins with it (cascade).</summary>
    public void Remove(int accountId)
    {
        accounts.TryRemove(accountId, out _);
        DropLoginsOf(accountId);
    }

    /// <summary>How many logins an account has now: the computers and programs it is logged in on.</summary>
    public int CountLogins(int accountId) => logins.Values.Count(l => l.AccountId == accountId);

    /// <summary>
    /// Writes when logins and accounts were last seen and cookies renewed,
    /// deletes logins not seen for <see cref="Lifetime"/>, and drops logins
    /// and accounts the database no longer has. A failure is logged and
    /// changes nothing here: the next flush tries again.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            foreach (var expired in logins.Where(l => now - l.Value.LastSeenAt > Lifetime).Select(l => l.Key).ToList())
            {
                End(expired);
            }

            await DeletePendingAsync(cancellationToken);

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
            foreach (var login in logins.Values.Where(l => l.Dirty).ToList())
            {
                var (seen, issued) = login.TakeChanges();
                await db.Logins.Where(l => l.KeyHash == login.KeyHash)
                    .ExecuteUpdateAsync(set => set.SetProperty(l => l.LastSeenAt, seen).SetProperty(l => l.IssuedAt, issued), cancellationToken);
            }

            foreach (var account in accounts.Values.Where(a => a.Dirty).ToList())
            {
                var seen = account.TakeChanges();
                await db.Accounts.Where(a => a.Id == account.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.LastSeenAt, seen), cancellationToken);
            }

            // Changes made by another process: the server command.
            var before = Interlocked.Read(ref version);
            var keys = (await db.Logins.Select(l => l.KeyHash).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            var storedAccounts = await db.Accounts.AsNoTracking().ToListAsync(cancellationToken);
            foreach (var key in logins.Where(l => l.Value.Version <= before && !keys.Contains(l.Key)).Select(l => l.Key).ToList())
            {
                logins.TryRemove(key, out _);
            }

            var ids = storedAccounts.Select(a => a.Id).ToHashSet();
            foreach (var id in accounts.Where(a => a.Value.Version <= before && !ids.Contains(a.Key)).Select(a => a.Key).ToList())
            {
                accounts.TryRemove(id, out _);
            }

            foreach (var stored in storedAccounts)
            {
                if (!accounts.TryGetValue(stored.Id, out var known)
                    || (known.Version <= before && (known.Paused != stored.Paused || known.CredentialVersion != stored.CredentialVersion)))
                {
                    Put(stored, endLogins: false);
                }
            }
        }
        catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            LogFlushFailed(logger, e);
        }
    }

    private void End(string hash)
    {
        if (logins.TryRemove(hash, out _))
        {
            pendingDeletes[hash] = 0;
        }
    }

    private void DropLoginsOf(int accountId)
    {
        foreach (var key in logins.Where(l => l.Value.AccountId == accountId).Select(l => l.Key).ToList())
        {
            logins.TryRemove(key, out _);
        }
    }

    private async Task DeletePendingAsync(CancellationToken cancellationToken)
    {
        if (pendingDeletes.IsEmpty)
        {
            return;
        }

        var keys = pendingDeletes.Keys.ToList();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        await db.Logins.Where(l => keys.Contains(l.KeyHash)).ExecuteDeleteAsync(cancellationToken);
        foreach (var key in keys)
        {
            pendingDeletes.TryRemove(key, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write login state to the database; logins stay as they are in memory and the next flush tries again")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);
}

/// <summary>One live login, as checked on every request.</summary>
public sealed class LoginState(LoginRecord record, long version)
{
    private readonly Lock gate = new();
    private DateTimeOffset lastSeenAt = record.LastSeenAt;
    private DateTimeOffset issuedAt = record.IssuedAt;
    private bool dirty;

    public string KeyHash { get; } = record.KeyHash;

    public int CredentialVersion { get; } = record.CredentialVersion;

    internal long Version { get; } = version;

    public int AccountId { get; } = record.AccountId;

    public string Entry { get; } = record.Entry;

    public string? RemoteAddress { get; } = record.RemoteAddress;

    public DateTimeOffset LastSeenAt
    {
        get
        {
            lock (gate)
            {
                return lastSeenAt;
            }
        }
    }

    public DateTimeOffset IssuedAt
    {
        get
        {
            lock (gate)
            {
                return issuedAt;
            }
        }
    }

    internal bool Dirty
    {
        get
        {
            lock (gate)
            {
                return dirty;
            }
        }
    }

    internal void Seen(DateTimeOffset now)
    {
        lock (gate)
        {
            if (now > lastSeenAt)
            {
                lastSeenAt = now;
                dirty = true;
            }
        }
    }

    internal void Renewed(DateTimeOffset issued)
    {
        lock (gate)
        {
            issuedAt = issued;
            dirty = true;
        }
    }

    internal (DateTimeOffset LastSeenAt, DateTimeOffset IssuedAt) TakeChanges()
    {
        lock (gate)
        {
            dirty = false;
            return (lastSeenAt, issuedAt);
        }
    }
}

/// <summary>An account as the checks on every request see it; no password hash.</summary>
public sealed class AccountState
{
    private readonly Lock gate = new();
    private DateTimeOffset? lastSeenAt;
    private bool dirty;

    private AccountState(Account account, long version)
    {
        Version = version;
        Id = account.Id;
        Name = account.Name;
        Role = account.Role;
        Project = account.Project;
        Paused = account.Paused;
        CredentialVersion = account.CredentialVersion;
        lastSeenAt = account.LastSeenAt;
    }

    public int Id { get; }

    internal long Version { get; }

    public string Name { get; }

    public AccountRole Role { get; }

    public string? Project { get; }

    public bool Paused { get; }

    /// <summary>Raised with every new password (<see cref="Account.CredentialVersion"/>).</summary>
    public int CredentialVersion { get; }

    public DateTimeOffset? LastSeenAt
    {
        get
        {
            lock (gate)
            {
                return lastSeenAt;
            }
        }
    }

    internal bool Dirty
    {
        get
        {
            lock (gate)
            {
                return dirty;
            }
        }
    }

    internal static AccountState From(Account account, long version)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new AccountState(account, version);
    }

    internal void Seen(DateTimeOffset now)
    {
        lock (gate)
        {
            if (lastSeenAt is null || now > lastSeenAt)
            {
                lastSeenAt = now;
                dirty = true;
            }
        }
    }

    internal DateTimeOffset? TakeChanges()
    {
        lock (gate)
        {
            dirty = false;
            return lastSeenAt;
        }
    }
}
