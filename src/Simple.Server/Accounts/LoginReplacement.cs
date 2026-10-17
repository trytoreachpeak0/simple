using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Simple.Server.Accounts;

/// <summary>
/// Logging in on a computer that is still logged in replaces its login with
/// a new one (OWASP: a new session identifier at every login). ASP.NET Core's
/// cookie authentication does not: when the request already carries a login
/// cookie, signing in renews that login's ticket under its old key
/// (<c>SessionStore.RenewAsync</c>, "multiple requests", dotnet/aspnetcore#22135),
/// so the old account would stay logged in while the API said the new one
/// was. So, before authentication, the login endpoint's request loses its
/// login cookie, and the key it carried is kept for the endpoint, which ends
/// that login once the new one has been made.
/// </summary>
public static class LoginReplacement
{
    private const string ReplacedKeys = "Simple.ReplacedLoginKeys";

    /// <summary>Marks the endpoint whose requests lose their login cookie before authentication: the login endpoint.</summary>
    public static TBuilder ReplacesLogin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(Marker.Instance);
    }

    /// <summary>Middleware, after routing and before authentication.</summary>
    public static Task Strip(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.GetEndpoint()?.Metadata.GetMetadata<Marker>() is null)
        {
            return next(context);
        }

        // ASP.NET Core reads cookie names without regard to case, so every
        // spelling of the login cookie goes, and each login it carried ends.
        var kept = new List<string>();
        var replaced = new List<string>();
        foreach (var header in context.Request.Headers.Cookie)
        {
            foreach (var pair in (header ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var name = separator < 0 ? pair : pair[..separator];
                if (!name.StartsWith(LoginSetup.CookieName, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(pair);
                }
                else if (separator > 0 && KeyOf(context, pair[(separator + 1)..]) is { } key)
                {
                    replaced.Add(key);
                }
            }
        }

        context.Items[ReplacedKeys] = replaced;

        context.Request.Headers.Cookie = kept.Count == 0 ? default : string.Join("; ", kept);
        return next(context);
    }

    /// <summary>The keys of the logins the request carried; for the login endpoint to end.</summary>
    public static IReadOnlyList<string> ReplacedLoginKeys(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ReplacedKeys, out var keys) && keys is List<string> list ? list : [];
    }

    private static string? KeyOf(HttpContext context, string cookie)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        // The cookie handler's own claim for the session key (CookieAuthenticationHandler.SessionIdClaim).
        return options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie))?.Principal
            .FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId");
    }

    private sealed class Marker
    {
        public static readonly Marker Instance = new();
    }
}
