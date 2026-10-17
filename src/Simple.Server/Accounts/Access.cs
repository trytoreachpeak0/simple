using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Simple.Server.Accounts;

/// <summary>
/// Who may use an endpoint: ADR 0014's action table, written in one place.
/// Every endpoint names one with <see cref="AccessExtensions.RequireAccess{TBuilder}"/>;
/// an endpoint that names none is closed to everyone (ADR 0045), and a test
/// fails on it. What a level admits is <see cref="AccessHandler.Admits"/>.
/// </summary>
public enum Access
{
    /// <summary>
    /// No login (ADR 0045): the login endpoint, the vehicle overview and its
    /// data, the console's static files and the process-alive health check.
    /// </summary>
    Anonymous,

    /// <summary>
    /// The onboard terminal protocol: no login, because it is not the
    /// console's; it is served only on its own listener, over TLS (ADR 0025).
    /// </summary>
    OnboardTerminal,

    /// <summary>Any login, including MCP and the watch account: logging out, who am I.</summary>
    AnyLogin,

    /// <summary>
    /// Viewing (ADR 0014's first row): every account, through any entry. A
    /// team-leader or engineer account sees its own project only, which the
    /// endpoint checks (ADR 0015).
    /// </summary>
    View,

    /// <summary>Viewing every project: the administrator and the watch account (ADR 0033).</summary>
    ViewAll,

    /// <summary>A team leader's actions and up, through the console only.</summary>
    TeamLeader,

    /// <summary>An engineer's actions ("declare repaired, put back into use") and up, through the console only.</summary>
    Engineer,

    /// <summary>
    /// Changing the parameter configuration: the administrator, through the
    /// console or MCP (ADR 0016; the event record's retention is the one item
    /// MCP may not change, which that endpoint checks).
    /// </summary>
    Configure,

    /// <summary>Managing accounts and the on-duty pause: the administrator, through the console only.</summary>
    ManageAccounts,

    /// <summary>Changing one's own password with the old one: any role account, through the console only.</summary>
    ChangeOwnPassword,
}

/// <summary>The access an endpoint names; what the endpoint test looks for.</summary>
public sealed record AccessMetadata(Access Access);

public static class AccessExtensions
{
    public static TBuilder RequireAccess<TBuilder>(this TBuilder builder, Access access)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new AccessMetadata(access));
        if (access == Access.OnboardTerminal)
        {
            // It opens the endpoint without login: only the terminal protocol,
            // which its own listener and TLS protect, may have it.
            builder.Add(endpoint =>
            {
                if (!endpoint.Metadata.Any(m => m is Grpc.AspNetCore.Server.GrpcMethodMetadata))
                {
                    throw new InvalidOperationException(
                        $"{endpoint.DisplayName} is not the onboard terminal protocol and cannot have {nameof(Access)}.{nameof(Access.OnboardTerminal)}.");
                }
            });
        }

        return access is Access.Anonymous or Access.OnboardTerminal
            ? builder.AllowAnonymous()
            : builder.RequireAuthorization(PolicyName(access));
    }

    public static string PolicyName(Access access) => $"simple:{access}";
}

/// <summary>An access level to check; null for an endpoint that names none, which nobody may use.</summary>
public sealed record AccessRequirement(Access? Access) : IAuthorizationRequirement;

/// <summary>
/// Checks a login against an access level: its account's role as it is now,
/// the entry it logged in through, and the on-duty pause.
/// </summary>
public sealed class AccessHandler(LoginRegistry registry) : AuthorizationHandler<AccessRequirement>
{
    /// <summary>The failure reason for the watch account while the on-duty pause is on.</summary>
    public const string WatchPaused = "watch-paused";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        if (context.User.FindFirstValue(LoginTicketStore.AccountIdClaim) is not { } id
            || registry.Account(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)) is not { } account)
        {
            return Task.CompletedTask;
        }

        if (account.Role == AccountRole.Watch && account.Paused)
        {
            context.Fail(new AuthorizationFailureReason(this, WatchPaused));
        }
        else if (requirement.Access is { } access && Admits(access, account.Role, context.User.FindFirstValue(LoginTicketStore.EntryClaim)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>ADR 0014's ranks, ADR 0016's MCP boundary and ADR 0033's view-only watch account.</summary>
    public static bool Admits(Access access, AccountRole role, string? entry)
    {
        var console = entry == LoginEntries.Console;
        var rank = role switch
        {
            AccountRole.TeamLeader => 1,
            AccountRole.Engineer => 2,
            AccountRole.Admin => 3,
            _ => 0,
        };
        return access switch
        {
            Access.AnyLogin or Access.View => true,
            Access.ViewAll => role is AccountRole.Admin or AccountRole.Watch,
            Access.TeamLeader => console && rank >= 1,
            Access.Engineer => console && rank >= 2,
            Access.Configure => role == AccountRole.Admin && entry is LoginEntries.Console or LoginEntries.Mcp,
            Access.ManageAccounts => console && role == AccountRole.Admin,
            Access.ChangeOwnPassword => console && rank >= 1,
            _ => false,
        };
    }
}

/// <summary>
/// Answers a refused request: 401 without a login, 403 with one, and for the
/// paused watch account a 403 that says so (ADR 0033), so its starting script
/// and the AI can tell "paused" from "not allowed".
/// </summary>
public sealed class AccessResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Message == AccessHandler.WatchPaused) == true)
        {
            return Paused().ExecuteAsync(context);
        }

        return fallback.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>What the watch account gets while the on-duty pause is on, its login included.</summary>
    public static IResult Paused() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: AccessHandler.WatchPaused,
            title: "On-duty watching is paused.",
            detail: "The administrator has paused on-duty watching; the watch account gets no data until it is resumed.");
}
