using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>
/// Login for the server API (ADR 0045), from ASP.NET Core's own pieces:
/// cookie authentication with a server-side store, data protection with its
/// keys in the database, the Identity password hasher, authorization
/// policies and the rate limiter.
/// </summary>
public static class LoginSetup
{
    /// <summary>
    /// The login cookie. <c>__Host-</c>: the browser keeps it only when it is
    /// <c>Secure</c>, has <c>Path=/</c> and no <c>Domain</c> (RFC 10017 §6.1.3.2).
    /// </summary>
    public const string CookieName = "__Host-simple";

    /// <summary>The rate limit on password checks: logging in and changing one's own password.</summary>
    public const string RateLimitPolicy = "simple:password-attempts";

    /// <summary>OWASP's current recommendation for PBKDF2-HMAC-SHA512 (ADR 0045); the framework's default is 100,000.</summary>
    public const int PasswordIterations = 220_000;

    public static IServiceCollection AddLogin(this IServiceCollection services)
    {
        services.AddOptions<LoginSettings>()
            .BindConfiguration(LoginSettings.Section)
            .Validate(s => s.AttemptsPerWindow > 0 && s.AttemptWindow > TimeSpan.Zero,
                $"{LoginSettings.Section}:AttemptsPerWindow and {LoginSettings.Section}:AttemptWindow must be positive.")
            .ValidateOnStart();

        // The keys that encrypt login cookies are kept in the database, so a
        // restart logs nobody out (ADR 0045), each encrypted with the machine's
        // DPAPI key. The application name is fixed: by default it is the
        // install path, and simple up runs the server from a new folder.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Simple.Server")
            .PersistKeysToDbContext<SimpleDbContext>();
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
        else
        {
            throw new PlatformNotSupportedException("The server keeps its keys in Windows key stores (DPAPI, CNG); it runs on Windows.");
        }

        services.Configure<PasswordHasherOptions>(options => options.IterationCount = PasswordIterations);
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();

        services.AddSingleton<LoginRegistry>();
        services.AddSingleton<LoginTicketStore>();
        services.AddHostedService<LoginHousekeeping>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<LoginTicketStore, TimeProvider>((options, store, time) =>
            {
                options.SessionStore = store;
                options.TimeProvider = time;
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = LoginRegistry.Lifetime;
                // The framework renews once half the lifetime has passed; ADR 0045
                // wants the first use of each day to renew (OnValidatePrincipal).
                options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = context =>
                {
                    if (context.Properties.IssuedUtc is { } issued && time.GetUtcNow() - issued >= LoginRegistry.RenewAfter)
                    {
                        context.ShouldRenew = true;
                    }

                    return Task.CompletedTask;
                };
                // An API, not pages: no redirect to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddSingleton<IAuthorizationHandler, AccessHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AccessResultHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var access in Enum.GetValues<Access>().Where(a => a is not (Access.Anonymous or Access.OnboardTerminal)))
            {
                options.AddPolicy(AccessExtensions.PolicyName(access), policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new AccessRequirement(access)));
            }

            // Closed by default: an endpoint that names no access is for nobody.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new AccessRequirement(null))
                .Build();
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<LoginSettings>>().Value;
                // A fixed window can say when the next attempt is allowed (Retry-After).
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.AttemptsPerWindow,
                        Window = settings.AttemptWindow,
                        QueueLimit = 0,
                    });
            });
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                LoginLog.TooManyAttempts(
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoginLog.Category),
                    context.HttpContext.Connection.RemoteIpAddress?.ToString());
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }

    /// <summary>After routing: the request header, the rate limit, then who is logged in and what it may do.</summary>
    public static void UseLogin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(RequestHeader.Require);
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
    }
}

/// <summary>Limits on password attempts (ADR 0045): per computer, never a lockout.</summary>
public sealed class LoginSettings
{
    public const string Section = "Simple:Login";

    /// <summary>
    /// Password attempts one computer may make per <see cref="AttemptWindow"/>;
    /// successful ones count too, the rate limiter counting requests.
    /// </summary>
    public int AttemptsPerWindow { get; set; } = 5;

    public TimeSpan AttemptWindow { get; set; } = TimeSpan.FromMinutes(1);
}
