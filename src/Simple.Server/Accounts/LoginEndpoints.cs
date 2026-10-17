using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>Logging in and out, who am I, and changing one's own password (ADR 0045).</summary>
public static class LoginEndpoints
{
    // Checked when the account name is unknown, so that an unknown name takes
    // as long to refuse as a wrong password and does not give itself away.
    private static readonly string UnknownAccountHash =
        new PasswordHasher<Account>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = LoginSetup.PasswordIterations }))
            .HashPassword(null!, "no account has this password");

    public static void MapLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/login", LogInAsync)
            .RequireAccess(Access.Anonymous)
            .RequireRateLimiting(LoginSetup.RateLimitPolicy)
            .ReplacesLogin()
            // Anyone may send it: a name and a password need no more.
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LoginSetup.PasswordRequestSizeLimit))
            .WithName("LogIn")
            .WithSummary("Logs in with an account name and password through the console or MCP entry; sets the login cookie.");
        app.MapPost("/api/v1/logout", LogOutAsync)
            .RequireAccess(Access.AnyLogin)
            .WithName("LogOut")
            .WithSummary("Ends this login: its cookie, and any copy of it, no longer works.");
        app.MapGet("/api/v1/session", (ClaimsPrincipal user) => TypedResults.Ok(SessionView.Of(user)))
            .RequireAccess(Access.AnyLogin)
            .WithName("GetSession")
            .WithSummary("The account, role, project and entry of this login.");
        app.MapPut("/api/v1/session/password", ChangeOwnPasswordAsync)
            .RequireAccess(Access.ChangeOwnPassword)
            .RequireRateLimiting(LoginSetup.RateLimitPolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LoginSetup.PasswordRequestSizeLimit))
            .WithName("ChangeOwnPassword")
            .WithSummary("Changes this account's password, given the old one. Every login of the account ends, this one too.");
    }

    private static async Task<Results<Ok<SessionView>, ProblemHttpResult, ValidationProblem>> LogInAsync(
        LoginRequest request,
        HttpContext http,
        SimpleDbContext db,
        IPasswordHasher<Account> hasher,
        LoginRegistry registry,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(LoginLog.Category);
        var remote = http.Connection.RemoteIpAddress?.ToString();
        if (!LoginEntries.IsKnown(request.Entry))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["entry"] = [$"entry must be \"{LoginEntries.Console}\" or \"{LoginEntries.Mcp}\"."],
            });
        }

        // A name no account can have is not looked up; it is answered like any unknown one.
        var account = Account.IsValidName(request.Name)
            ? await db.Accounts.SingleOrDefaultAsync(a => a.Name == request.Name, cancellationToken)
            : null;
        var verdict = hasher.VerifyHashedPassword(account!, account?.PasswordHash ?? UnknownAccountHash, request.Password ?? string.Empty);
        if (account is null || verdict == PasswordVerificationResult.Failed)
        {
            if (account is null)
            {
                LoginLog.FailedUnknown(logger, request.Entry!, remote);
            }
            else
            {
                LoginLog.Failed(logger, account.Name, request.Entry!, remote);
            }

            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The account name or the password is wrong.");
        }

        if (account.Role == AccountRole.Watch && account.Paused)
        {
            LoginLog.RefusedWhilePaused(logger, account.Name, request.Entry!, remote);
            return (ProblemHttpResult)AccessResultHandler.Paused();
        }

        // An account the server command created or gave a new password while
        // the server runs reaches the registry with the next flush; the login
        // need not wait for it.
        if (registry.Account(account.Id) is not { } known || known.CredentialVersion < account.CredentialVersion)
        {
            registry.Put(account, endLogins: false);
        }

        // A stronger hash of the same password, written only while the password
        // is still the one checked: a new password set meanwhile wins, and this
        // login fails like any login with an old password.
        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var stronger = hasher.HashPassword(account, request.Password!);
            var written = await db.Accounts
                .Where(a => a.Id == account.Id && a.CredentialVersion == account.CredentialVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.PasswordHash, stronger), cancellationToken);
            if (written == 0)
            {
                LoginLog.Failed(logger, account.Name, request.Entry!, remote);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "The account name or the password is wrong.");
            }
        }

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            LoginTicketStore.PrincipalForLogin(account.Id, request.Entry!, account.CredentialVersion),
            new AuthenticationProperties { IsPersistent = true });
        LoginLog.LoggedIn(logger, account.Name, request.Entry!, remote);
        // The computer's previous login, if it was still logged in, is replaced.
        foreach (var previous in LoginReplacement.ReplacedLoginKeys(http))
        {
            await registry.EndAsync(previous, cancellationToken);
        }
        return TypedResults.Ok(new SessionView(account.Name, account.Role, account.Project, request.Entry!));
    }

    private static async Task<NoContent> LogOutAsync(HttpContext http, ILoggerFactory loggers)
    {
        var actor = Actors.Of(http);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var logger = loggers.CreateLogger(LoginLog.Category);
        LoginLog.LoggedOut(logger, actor.Account, actor.Entry, actor.RemoteAddress);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem>> ChangeOwnPasswordAsync(
        ChangeOwnPasswordRequest request,
        HttpContext http,
        AccountChanges changes,
        CancellationToken cancellationToken)
    {
        var id = int.Parse(http.User.FindFirstValue(LoginTicketStore.AccountIdClaim)!, System.Globalization.CultureInfo.InvariantCulture);
        if (Passwords.Check(request.NewPassword) is { } problem)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
        }

        if (!await changes.ChangeOwnPasswordAsync(id, request.OldPassword ?? string.Empty, request.NewPassword!, Actors.Of(http), cancellationToken))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["oldPassword"] = ["The old password is wrong."] });
        }

        // Its every login ended, this one too: the browser drops the cookie.
        http.Response.Cookies.Delete(LoginSetup.CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        return TypedResults.NoContent();
    }
}

/// <param name="Entry"><c>console</c> or <c>mcp</c> (ADR 0045); the command line uses <c>console</c>.</param>
public sealed record LoginRequest(string? Name, string? Password, string? Entry);

public sealed record ChangeOwnPasswordRequest(string? OldPassword, string? NewPassword);

/// <param name="Project">The project of a team-leader or engineer account; null otherwise.</param>
/// <param name="Entry">Through which entry this login came: <c>console</c> or <c>mcp</c>.</param>
public sealed record SessionView(string Name, AccountRole Role, string? Project, string Entry)
{
    public static SessionView Of(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new SessionView(
            user.FindFirstValue(ClaimTypes.Name)!,
            Enum.Parse<AccountRole>(user.FindFirstValue(ClaimTypes.Role)!),
            user.FindFirstValue(LoginTicketStore.ProjectClaim),
            user.FindFirstValue(LoginTicketStore.EntryClaim)!);
    }
}

/// <summary>The password rule (ADR 0045): at least eight characters, nothing else.</summary>
public static class Passwords
{
    public const int MinimumLength = 8;

    /// <summary>
    /// Not a rule of ADR 0045 but a bound of the login endpoint: its body is
    /// at most 4 KB (LoginSetup.PasswordRequestSizeLimit), and a password set
    /// longer than fits could never log in. 128 characters fit even when every
    /// one is sent escaped as a surrogate pair.
    /// </summary>
    public const int MaximumLength = 128;

    /// <summary>Why a password is refused, or null when it is accepted.</summary>
    public static string? Check(string? password)
    {
        var length = password?.EnumerateRunes().Count() ?? 0;
        return length < MinimumLength || length > MaximumLength
            ? $"A password has at least {MinimumLength} and at most {MaximumLength} characters."
            : null;
    }
}

public static class Actors
{
    /// <summary>The logged-in account behind a request, its entry and its computer.</summary>
    public static Actor Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new Actor(
            http.User.FindFirstValue(ClaimTypes.Name),
            http.User.FindFirstValue(LoginTicketStore.EntryClaim) ?? EventSources.Console,
            http.Connection.RemoteIpAddress?.ToString());
    }
}
