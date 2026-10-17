namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// Lets through to a real RIoT only the requests that read: every GET, and
/// the four POSTs that change nothing (the admin login and the three route
/// queries). Anything else (creating an order, an order command, an
/// emergency stop, taking a vehicle off line) is refused before it is sent,
/// so a contract that forgets <see cref="RiotTarget.RequireWrites"/> fails
/// instead of moving a vehicle.
/// </summary>
public sealed class ReadOnlyGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly HashSet<string> ReadingPosts = new(StringComparer.Ordinal)
    {
        "/api/auth/v1/admin/login",
        "/api/task/v1/route/getRouteCostsBy",
        "/api/task/v1/route/queryNearEnd",
        "/api/task/v1/route/queryNearestStart",
    };

    /// <summary>The guard around the handler a real RIoT is reached through: no automatic redirects, like riot-sdk's own (ADR 0028).</summary>
    public static ReadOnlyGuard ForRealRiot() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public static bool Allows(HttpMethod method, string path) =>
        method == HttpMethod.Get || (method == HttpMethod.Post && ReadingPosts.Contains(path.TrimEnd('/')));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? "";
        return Allows(request.Method, path)
            ? base.SendAsync(request, cancellationToken)
            : throw new RealRiotWriteRefusedException(
                $"契约测试不向真实 RIoT 发写请求：{request.Method} {path} 在发出前被拦下。这一条契约漏了 Target.RequireWrites()。");
    }
}

public sealed class RealRiotWriteRefusedException(string message) : Exception(message);
