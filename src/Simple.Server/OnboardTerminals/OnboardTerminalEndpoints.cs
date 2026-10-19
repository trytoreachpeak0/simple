using Microsoft.AspNetCore.Http.HttpResults;
using Simple.Server.Accounts;
using Simple.Server.Status;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// Confirming which vehicle a terminal is, and revoking its key (ADR 0007,
/// 0025, 0029): an engineer and up, through the console. The API only; the
/// console's screen for it comes with the console.
/// </summary>
public static class OnboardTerminalEndpoints
{
    public static void MapOnboardTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/onboard-terminals", async (HttpContext http, OnboardTerminalRegistry terminals) =>
                TypedResults.Ok(new OnboardTerminalKeysResponse(
                    terminals.Registrations(), await terminals.PendingAsync(withCodes: await http.MayAsync(Access.Engineer), http.RequestAborted))))
            .RequireAccess(Access.View)
            .WithName("ListOnboardTerminalKeys")
            .WithSummary("Every vehicle's registered terminal key, and every terminal key waiting for confirmation; its pairing code only for an engineer and up, through the console.");
        app.MapPost("/api/v1/onboard-terminals/{vehicleId}/confirm", ConfirmAsync)
            .RequireAccess(Access.Engineer)
            .WithName("ConfirmOnboardTerminal")
            .WithSummary("Confirms that the terminal waiting with this pairing code is the vehicle it claims: its key becomes the vehicle's, and the vehicle's earlier key is released.");
        app.MapPost("/api/v1/onboard-terminals/{vehicleId}/revoke", RevokeAsync)
            .RequireAccess(Access.Engineer)
            .WithName("RevokeOnboardTerminal")
            .WithSummary("Revokes the vehicle's registered terminal key: from now on that terminal waits for confirmation.");
    }

    private static async Task<Results<Ok<OnboardTerminalKeyStatus>, ValidationProblem, ProblemHttpResult>> ConfirmAsync(
        string vehicleId, ConfirmOnboardTerminalRequest request, HttpContext http, OnboardTerminalRegistry terminals, CancellationToken cancellationToken)
    {
        if (request.PairingCode is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["pairingCode"] = ["pairingCode is the six digits the terminal's screen shows."] });
        }

        var outcome = await terminals.ConfirmAsync(vehicleId, code, Actors.Of(http), cancellationToken);
        return outcome.Key is { } key
            ? TypedResults.Ok(new OnboardTerminalKeyStatus(key.VehicleId, key.KeyPin, key.ConfirmedAt, key.ConfirmedBy))
            : TypedResults.Problem(statusCode: outcome.Status, title: outcome.Problem);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(
        string vehicleId, HttpContext http, OnboardTerminalRegistry terminals, CancellationToken cancellationToken)
    {
        var outcome = await terminals.RevokeAsync(vehicleId, Actors.Of(http), cancellationToken);
        return outcome.Key is null ? TypedResults.Problem(statusCode: outcome.Status, title: outcome.Problem) : TypedResults.NoContent();
    }
}

/// <param name="PairingCode">The six digits the terminal's screen and the pending list show.</param>
public sealed record ConfirmOnboardTerminalRequest(string? PairingCode);

public sealed record OnboardTerminalKeysResponse(IReadOnlyList<OnboardTerminalKeyStatus> Registered, IReadOnlyList<PendingOnboardTerminalStatus> Pending);
