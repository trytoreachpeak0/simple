using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;

namespace Simple.FakeRiot;

/// <summary>
/// Makes load visible: counts every call to the RIoT API by endpoint and
/// holds every read for the read delay. Real RIoT is shared by about 25
/// vehicles; the previous line read it about ten times a second and its reads
/// took about 168 ms at p95, so a caller that polls too often slowed the
/// whole fleet (research/riot-review.md R39, D16).
/// </summary>
public sealed class FakeRiotLoad(IOptions<FakeRiotSettings> settings)
{
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
}

public static class FakeRiotLoadMiddleware
{
    /// <summary>Counts each /api call, the unauthorised ones too, and delays each /api GET.</summary>
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
                if (HttpMethods.IsGet(request.Method) && load.ReadDelay > TimeSpan.Zero)
                {
                    await Task.Delay(load.ReadDelay, context.RequestAborted);
                }
            }

            await next(context);
        });
}
