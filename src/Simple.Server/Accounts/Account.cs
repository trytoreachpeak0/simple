using System.Text.Json.Serialization;

namespace Simple.Server.Accounts;

/// <summary>
/// A console account (ADR 0016): shared per project and role, one
/// administrator for the whole of SIMPLE, and one on-duty (watch) account
/// that only views (ADR 0033).
/// </summary>
public sealed class Account
{
    public const int NameMaxLength = 100;

    /// <summary>Whether a name can be an account's: 1 to 100 characters, no control characters, no surrounding spaces.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= NameMaxLength && !name.Any(char.IsControl) && name.Trim() == name;

    public int Id { get; private set; }

    public required string Name { get; init; }

    public AccountRole Role { get; init; }

    /// <summary>
    /// The project a team-leader or engineer account belongs to; null for the
    /// administrator and the watch account. A plain name for now: the server
    /// has no projects yet, so it is not checked against anything. It becomes
    /// a reference once projects live in the server (ADR 0015, 0017).
    /// </summary>
    public string? Project { get; init; }

    /// <summary>The password's hash (<see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>); never the password.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>When a login of this account was last checked (ADR 0045); kept when its logins end.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The on-duty pause (ADR 0033): only the watch account has it; while on, its every request answers "paused".</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Raised with every new password. A login carries the version its
    /// password was checked against, and one older than the account's is
    /// refused: also a login whose password was checked just before the
    /// password changed and that was written just after.
    /// </summary>
    public int CredentialVersion { get; set; }
}

/// <summary>The three console roles of ADR 0014, and the watch account of ADR 0033, which is no role.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountRole>))]
public enum AccountRole
{
    [JsonStringEnumMemberName("team-leader")]
    TeamLeader = 1,

    [JsonStringEnumMemberName("engineer")]
    Engineer = 2,

    [JsonStringEnumMemberName("admin")]
    Admin = 3,

    [JsonStringEnumMemberName("watch")]
    Watch = 4,
}

/// <summary>
/// One login (ADR 0045): a row per browser or program that logged in. The
/// cookie carries only the login's random key, encrypted; logging out, a new
/// password or a deleted account removes the row, and with it the login.
/// </summary>
public sealed class LoginRecord
{
    /// <summary>
    /// The SHA-256 of the login's key, in lowercase hex. The key itself is
    /// only in the cookie: a copy of the database cannot be turned into a login.
    /// </summary>
    public required string KeyHash { get; init; }

    /// <summary>The account's <see cref="Account.CredentialVersion"/> when its password was checked for this login.</summary>
    public int CredentialVersion { get; init; }

    public int AccountId { get; init; }

    /// <summary>Through which entry it logged in: <see cref="LoginEntries.Console"/> or <see cref="LoginEntries.Mcp"/>.</summary>
    public required string Entry { get; init; }

    /// <summary>The computer it logged in from, as the server saw it.</summary>
    public string? RemoteAddress { get; init; }

    public DateTimeOffset LoggedInAt { get; init; }

    /// <summary>When its cookie was last issued: at login, then renewed once a day (ADR 0045).</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>When it was last checked; a login not seen for seven days is removed.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>The entries a login names (ADR 0045). The command line logs in through the console entry.</summary>
public static class LoginEntries
{
    public const string Console = "console";

    /// <summary>Limited by the server to viewing and parameter configuration (ADR 0016, 0045).</summary>
    public const string Mcp = "mcp";

    public static bool IsKnown(string? entry) => entry is Console or Mcp;
}
