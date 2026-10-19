using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Simple.Cli;

/// <summary>
/// The local system's server API as the command line uses it: logged in as
/// the instance's administrator through the console entry (ADR 0045), with
/// the password <c>up</c> keeps outside the clone. The login cookie is
/// <c>Secure</c> and the API is plain HTTP on loopback, so the cookie is sent
/// by hand, not through a cookie container that would hold it back.
/// Disposing logs out.
/// </summary>
public sealed class ServerApi : IAsyncDisposable
{
    /// <summary>The server's login cookie (Simple.Server.Accounts.LoginSetup.CookieName).</summary>
    public const string CookieName = "__Host-simple";

    /// <summary>The header every changing request carries (Simple.Server.Accounts.RequestHeader.Name).</summary>
    public const string RequestHeader = "X-Simple-Request";

    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly string cookie;

    private ServerApi(HttpClient http, Uri baseUri, string cookie)
    {
        this.http = http;
        this.baseUri = baseUri;
        this.cookie = cookie;
    }

    /// <summary>Logs in; null when the server answers but refuses the password.</summary>
    /// <exception cref="HttpRequestException">The server does not answer, or answers with an error.</exception>
    public static async Task<ServerApi?> LogInAsync(HttpClient http, Uri baseUri, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/v1/login"))
        {
            Content = JsonContent.Create(new { name = "admin", password, entry = "console" }),
        };
        request.Headers.Add(RequestHeader, "1");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var value = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal))?.Split(';')[0][(CookieName.Length + 1)..]
            : null;
        return value is null
            ? throw new HttpRequestException("The server accepted the login but set no login cookie.")
            : new ServerApi(http, baseUri, value);
    }

    /// <summary>Whether the server answers, the instance's administrator can log in and the server can reach its database.</summary>
    public static async Task<bool> ReadyAsync(HttpClient http, Uri baseUri, string? password, CancellationToken cancellationToken)
    {
        if (password is null)
        {
            return false;
        }

        try
        {
            await using var api = await LogInAsync(http, baseUri, password, cancellationToken);
            if (api is null)
            {
                return false;
            }

            using var ready = await api.GetAsync("/health/ready", cancellationToken);
            return ready.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // the request timed out
        }
    }

    public async Task<HttpResponseMessage> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, pathAndQuery));
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>A changing request: the login cookie and the request header every changing request carries.</summary>
    public async Task<HttpResponseMessage> PostJsonAsync(string pathAndQuery, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, pathAndQuery)) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        request.Headers.Add(RequestHeader, "1");
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task<JsonElement> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(pathAndQuery, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>Logs out, so the command line leaves no login behind; a server that is gone is not an error.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/v1/logout"));
            request.Headers.Add("Cookie", $"{CookieName}={cookie}");
            request.Headers.Add(RequestHeader, "1");
            using var response = await http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // An unused login ends by itself after seven days (ADR 0045).
        }
    }
}
