using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>The outcome of a login through the API: the response, and on success the cookie and a client that sends it.</summary>
public sealed record TestLogin(HttpResponseMessage Response, string? Cookie, HttpClient? Client)
{
    /// <summary>The login cookie's value from a response's <c>Set-Cookie</c>, or null.</summary>
    public static string? CookieOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return SetCookieOf(response)?.Split(';')[0][(LoginSetup.CookieName.Length + 1)..];
    }

    /// <summary>The whole <c>Set-Cookie</c> line of the login cookie, attributes included, or null.</summary>
    public static string? SetCookieOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(LoginSetup.CookieName + "=", StringComparison.Ordinal))
            : null;
    }
}

/// <summary>
/// Lets an in-memory test server see a request as coming from another
/// computer: the address in <see cref="Header"/> becomes the connection's
/// remote address. Test code only; the product never reads such a header.
/// </summary>
public sealed class TestRemoteAddress : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((HttpContext context, RequestDelegate following) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var address))
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address.ToString());
            }

            return following(context);
        });
        next(app);
    };
}
