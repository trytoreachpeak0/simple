using Microsoft.AspNetCore.Http.HttpResults;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Fake;

/// <summary>
/// The fake source's control API, mapped only when the gateway reads the fake
/// source. Like every gateway endpoint it is served on loopback only. Each
/// change takes effect at the next round.
/// </summary>
public static class FakeMesEndpoints
{
    public const string Prefix = "/fake-mes";

    public static void MapFakeMesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).WithTags("Fake MES");

        group.MapGet("/", (FakeMesSource fake) => TypedResults.Ok(fake.State()))
            .WithName("GetFakeMes")
            .WithSummary("The fake MES's rows and the failed and malformed reads still pending.");

        group.MapPost("/rows", (FakeMesSource fake, MesRow row) =>
            {
                fake.Add(row);
                return TypedResults.Ok(fake.State());
            })
            .WithName("AddFakeMesRow")
            .WithSummary("Adds a row as given; missing columns stay empty.");

        group.MapDelete("/rows", Results<Ok<FakeMesState>, NotFound<string>> (FakeMesSource fake, string? taskType, string? sublot) =>
                fake.Remove(taskType, sublot) > 0
                    ? TypedResults.Ok(fake.State())
                    : TypedResults.NotFound($"No row has TASK_TYPE '{taskType}' and SUBLOT '{sublot}'."))
            .WithName("RemoveFakeMesRows")
            .WithSummary("Removes every row with this TASK_TYPE and SUBLOT (exact, case and spaces count).");

        group.MapPost("/failures", Results<Ok<FakeMesState>, ValidationProblem> (FakeMesSource fake, FailRequest request) =>
            {
                if (request.Count < 0)
                {
                    return CountProblem();
                }

                fake.FailNext(request.Count, request.Message);
                return TypedResults.Ok(fake.State());
            })
            .WithName("FailFakeMesReads")
            .WithSummary("The next count reads fail, with the given message; 0 cancels pending failures.");

        group.MapPost("/malformed", Results<Ok<FakeMesState>, ValidationProblem> (FakeMesSource fake, MalformedRequest request) =>
            {
                if (request.Count < 0)
                {
                    return CountProblem();
                }

                fake.MalformedNext(request.Count);
                return TypedResults.Ok(fake.State());
            })
            .WithName("MalformFakeMesReads")
            .WithSummary("The next count reads also return a row missing its PACKAGE value; 0 cancels.");

        group.MapPost("/reset", (FakeMesSource fake) =>
            {
                fake.Reset();
                return TypedResults.Ok(fake.State());
            })
            .WithName("ResetFakeMes")
            .WithSummary("Back to the seed rows, with nothing pending.");
    }

    private static ValidationProblem CountProblem() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["count"] = ["count must be 0 or more."] });
}

public sealed record FailRequest(int Count, string? Message);

public sealed record MalformedRequest(int Count);
