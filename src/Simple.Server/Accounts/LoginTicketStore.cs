using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Simple.Server.Accounts;

/// <summary>
/// The server-side store behind the login cookie (ADR 0045): the cookie
/// carries only a login's key, encrypted by ASP.NET Core's cookie
/// authentication; who is logged in, through which entry and with which
/// role is read from <see cref="LoginRegistry"/> on every request, so a role,
/// a pause or an ended login takes effect on the next request.
/// </summary>
public sealed class LoginTicketStore(LoginRegistry registry) : ITicketStore
{
    public const string AccountIdClaim = "simple:account-id";
    public const string EntryClaim = "simple:entry";
    public const string ProjectClaim = "simple:project";

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(httpContext);
        var principal = ticket.Principal;
        var accountId = int.Parse(principal.FindFirstValue(AccountIdClaim)!, System.Globalization.CultureInfo.InvariantCulture);
        var entry = principal.FindFirstValue(EntryClaim)!;
        return await registry.AddAsync(
            accountId,
            entry,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            ticket.Properties.IssuedUtc ?? throw new InvalidOperationException("A login ticket has no issue time."),
            cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        Task.FromResult(registry.Check(key) is { } found ? Ticket(found.Login, found.Account) : null);

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.Properties.IssuedUtc is { } issued)
        {
            registry.Renewed(key, issued);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        registry.EndAsync(key, cancellationToken);

    // The cookie handler calls the overloads with the HttpContext; these exist for the interface only.
    public Task<string> StoreAsync(AuthenticationTicket ticket) => throw new NotSupportedException();

    public Task RenewAsync(string key, AuthenticationTicket ticket) => throw new NotSupportedException();

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => throw new NotSupportedException();

    public Task RemoveAsync(string key) => throw new NotSupportedException();

    /// <summary>The principal the login endpoint signs in: which account, through which entry. The store adds the rest.</summary>
    public static ClaimsPrincipal PrincipalForLogin(int accountId, string entry) =>
        new(new ClaimsIdentity(
            [
                new Claim(AccountIdClaim, accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(EntryClaim, entry),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    private static AuthenticationTicket Ticket(LoginState login, AccountState account)
    {
        var claims = new List<Claim>
        {
            new(AccountIdClaim, account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, account.Name),
            new(ClaimTypes.Role, account.Role.ToString()),
            new(EntryClaim, login.Entry),
        };
        if (account.Project is { } project)
        {
            claims.Add(new Claim(ProjectClaim, project));
        }

        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = login.IssuedAt,
            ExpiresUtc = login.IssuedAt + LoginRegistry.Lifetime,
        };
        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
