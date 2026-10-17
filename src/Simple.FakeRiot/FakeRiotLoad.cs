using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;

namespace Simple.FakeRiot;

/// <summary>
/// Makes load visible: counts every call to the RIoT API by endpoint and
/// holds every read (a GET, or one of the three route queries) for the read
/// delay, on the fake's clock. Real RIoT is shared by about 25
/// vehicles; the previous line read it about ten times a second and its reads
/// took about 168 ms at p95, so a caller that polls too often slowed the
/// whole fleet (research/riot-review.md R39, D16).
/// </summary>
public sealed class FakeRiotLoad(IOptions<FakeRiotSettings> settings, TimeProvider time)
{
    /// <summary>The POSTs that only read: the three route queries.</summary>
    private static readonly HashSet<string> ReadingPosts = new(StringComparer.Ordinal)
    {
        "/api/task/v1/route/getRouteCostsBy",
        "/api/task/v1/route/queryNearEnd",
        "/api/task/v1/route/queryNearestStart",
    };

    private readonly ConcurrentDictionary<string, long> calls = new(StringComparer.Ordinal);
    private long readDelayTicks = settings.Value.ReadDelay.Ticks;

    public TimeSpan ReadDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref readDelayTicks));
        set => Interlocked.Exchange(ref readDelayTicks, value.Ticks);
    }

    /// <summary>Calls so far, keyed by method and route pattern (<c>GET /api/order/v1/orderRecord/detailByUpperId/{upperId}</c>).</summary>
    public IReadOnlyDictionary<string, long> Calls => new SortedDictionary<string, long>(calls, StringComparer.Ordinal);

    public void Reset() => calls.Clear();

    public void Count(string key) => calls.AddOrUpdate(key, 1, (_, n) => n + 1);

    /// <summary>Holds a read (a GET, or one of the route queries) for the read delay, on the fake's clock.</summary>
    public Task DelayIfReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var delay = ReadDelay;
        var read = HttpMethods.IsGet(request.Method) || (HttpMethods.IsPost(request.Method) && ReadingPosts.Contains(request.Path.Value ?? ""));
        return read && delay > TimeSpan.Zero ? Task.Delay(delay, time, cancellationToken) : Task.CompletedTask;
    }
}

public static class FakeRiotLoadMiddleware
{
    /// <summary>Counts each /api call, the unauthorised ones too, and delays each read.</summary>
    public static IApplicationBuilder UseFakeRiotLoad(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                var load = context.RequestServices.GetRequiredService<FakeRiotLoad>();
                var route = context.GetEndpoint() is RouteEndpoint { RoutePattern: RoutePattern pattern }
                    ? "/" + pattern.RawText?.TrimStart('/')
                    : request.Path.Value;
                load.Count($"{request.Method} {route}");
                await load.DelayIfReadAsync(request, context.RequestAborted);
            }

            await next(context);
        });
}
