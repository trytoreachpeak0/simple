using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Simple.MesGateway.Rounds;

public static class SnapshotEndpoints
{
    public static void MapSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/snapshot", (SnapshotStore snapshots) => TypedResults.Ok(snapshots.Latest))
            .WithName("GetSnapshot")
            .WithSummary("The latest round whole: its number, when it was read, whether it succeeded and every row.");
    }
}

/// <summary>Ready once the first round is in, good or not: from then on the snapshot says what the MES gave.</summary>
public sealed class FirstRoundHealthCheck(SnapshotStore snapshots) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(snapshots.Latest.Round > 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("No round has been read yet."));
}
