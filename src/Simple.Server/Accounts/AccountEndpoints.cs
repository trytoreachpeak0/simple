using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>
/// Managing accounts (ADR 0016): the administrator, through the console only
/// (<see cref="Access.ManageAccounts"/>). The API only; the console's
/// account page comes with the console.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/accounts", ListAsync)
            .RequireAccess(Access.ManageAccounts)
            .WithName("ListAccounts")
            .WithSummary("Every account: role, project, when it was last seen, how many logins it has now; the watch account's on-duty pause.");
        app.MapPost("/api/v1/accounts", CreateAsync)
            .RequireAccess(Access.ManageAccounts)
            .WithName("CreateAccount")
            .WithSummary("Creates a team-leader, engineer or watch account. The administrator's account is made by the server command only.");
        app.MapDelete("/api/v1/accounts/{name}", DeleteAsync)
            .RequireAccess(Access.ManageAccounts)
            .WithName("DeleteAccount")
            .WithSummary("Deletes an account; its every login ends.");
        app.MapPut("/api/v1/accounts/{name}/password", SetPasswordAsync)
            .RequireAccess(Access.ManageAccounts)
            .WithName("SetAccountPassword")
            .WithSummary("Sets another account's password; its every login ends. Not for the administrator's own.");
        app.MapPut("/api/v1/accounts/{name}/paused", SetPausedAsync)
            .RequireAccess(Access.ManageAccounts)
            .WithName("SetOnDutyPause")
            .WithSummary("Switches the watch account's on-duty pause (ADR 0033): while on, its every request answers paused.");
    }

    private static async Task<Ok<List<AccountView>>> ListAsync(SimpleDbContext db, LoginRegistry registry, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(accounts.Select(a => new AccountView(
                a.Name,
                a.Role,
                a.Project,
                registry.Account(a.Id)?.LastSeenAt ?? a.LastSeenAt,
                registry.CountLogins(a.Id),
                a.Role == AccountRole.Watch ? a.Paused : null))
            .ToList());
    }

    private static async Task<Results<Created<AccountView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateAccountRequest request, HttpContext http, AccountChanges changes, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name;
        if (string.IsNullOrEmpty(name) || name.Length > Account.NameMaxLength || name.Any(char.IsControl) || name.Trim() != name)
        {
            errors["name"] = [$"name has 1 to {Account.NameMaxLength} characters, without control characters or surrounding spaces."];
        }

        switch (request.Role)
        {
            case null:
                errors["role"] = ["role is team-leader, engineer or watch."];
                break;
            case AccountRole.Admin:
                errors["role"] = ["There is one administrator, made by the server command set-admin-password."];
                break;
            case AccountRole.TeamLeader or AccountRole.Engineer
                when string.IsNullOrWhiteSpace(request.Project) || request.Project.Length > Account.NameMaxLength:
                errors["project"] = ["A team-leader or engineer account belongs to one project (ADR 0016)."];
                break;
            case AccountRole.Watch when request.Project is not null:
                errors["project"] = ["The watch account sees every project and belongs to none (ADR 0033)."];
                break;
        }

        if (Passwords.Check(request.Password) is { } password)
        {
            errors["password"] = [password];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var change = await changes.CreateAsync(name!, request.Role!.Value, request.Project, request.Password!, Actors.Of(http), cancellationToken);
        if (change.Account is not { } account)
        {
            return Problem(change);
        }

        return TypedResults.Created(
            $"/api/v1/accounts/{Uri.EscapeDataString(account.Name)}",
            new AccountView(account.Name, account.Role, account.Project, null, 0, account.Role == AccountRole.Watch ? false : null));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string name, HttpContext http, AccountChanges changes, CancellationToken cancellationToken)
    {
        var change = await changes.DeleteAsync(name, Actors.Of(http), cancellationToken);
        return change.Account is null ? Problem(change) : TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetPasswordAsync(
        string name, SetPasswordRequest request, HttpContext http, AccountChanges changes, CancellationToken cancellationToken)
    {
        if (Passwords.Check(request.Password) is { } problem)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [problem] });
        }

        var change = await changes.SetPasswordAsync(name, request.Password!, Actors.Of(http), cancellationToken);
        return change.Account is null ? Problem(change) : TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetPausedAsync(
        string name, SetPausedRequest request, HttpContext http, AccountChanges changes, CancellationToken cancellationToken)
    {
        var change = await changes.SetPausedAsync(name, request.Paused, Actors.Of(http), cancellationToken);
        return change.Account is null ? Problem(change) : TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(AccountChange change) =>
        TypedResults.Problem(statusCode: change.Status, title: change.Problem);
}

/// <param name="Project">Required for team-leader and engineer accounts, absent for the watch account.</param>
public sealed record CreateAccountRequest(string? Name, AccountRole? Role, string? Project, string? Password);

public sealed record SetPasswordRequest(string? Password);

public sealed record SetPausedRequest(bool Paused);

/// <param name="LastSeenAt">When a login of the account was last checked (ADR 0045); the watch account's is when the on-duty AI last looked (ADR 0033).</param>
/// <param name="Logins">How many computers and programs it is logged in on now.</param>
/// <param name="Paused">The watch account's on-duty pause; null for every other account.</param>
public sealed record AccountView(string Name, AccountRole Role, string? Project, DateTimeOffset? LastSeenAt, int Logins, bool? Paused);
