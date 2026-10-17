using Microsoft.AspNetCore.Http.HttpResults;

namespace Simple.Server.Events;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/events", GetEvents)
            .WithName("GetEvents")
            .WithSummary("Reads the event record in insertion order. Follow nextCursor with 'after' to page or to poll.");
    }

    private static async Task<Results<Ok<EventPage>, ValidationProblem>> GetEvents(
        EventStore store,
        long? after,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit ?? EventStore.DefaultLimit;
        if (take is < 1 or > EventStore.MaxLimit)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"limit must be between 1 and {EventStore.MaxLimit}."],
            });
        }

        return TypedResults.Ok(await store.ReadAsync(after, since, until, take, cancellationToken));
    }
}
