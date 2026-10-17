using System.Text.RegularExpressions;

namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// Lets through to a real RIoT only the reads the contracts use, named one by
/// one: not "every GET", since RIoT has GETs that act (execActionCommand,
/// clearVehicleAndCancelOrderTask, clearVehicle, clearAllVehicle,
/// clearOrderSequence in riot-sdk's specs/task.json). Anything not on the
/// lists (creating an order, an order command, an emergency stop, taking a
/// vehicle off line, an acting GET, an endpoint added to RIoT later) is
/// refused before it is sent, so a contract that forgets
/// <see cref="RiotTarget.RequireWrites"/> fails instead of moving a vehicle.
/// </summary>
public sealed partial class ReadOnlyGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>The guard around the handler a real RIoT is reached through: no automatic redirects, like riot-sdk's own (ADR 0028).</summary>
    public static ReadOnlyGuard ForRealRiot() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public static bool Allows(HttpMethod method, string path) =>
        (method == HttpMethod.Get && ReadingGet().IsMatch(path)) || (method == HttpMethod.Post && ReadingPost().IsMatch(path));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The synchronous send too: DelegatingHandler would hand it to the inner handler unchecked.</summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.Send(request, cancellationToken);
    }

    private static void Check(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (!Allows(request.Method, path))
        {
            throw new RealRiotWriteRefusedException(
                $"契约测试只向真实 RIoT 发白名单里的读请求：{request.Method} {path} 不在名单里，在发出前被拦下。写操作的契约漏了 Target.RequireWrites()，或者要读的接口还没加进 ReadOnlyGuard。");
        }
    }

    /// <summary>Maps, stations, edges, removed edges and stations, edge groups, vehicles, order details and the order list, dynamic route costs.</summary>
    [GeneratedRegex(
        @"^/api/(" +
        @"imap/v1/mapInfo/getALLMapInfoExcludeMapJson" +
        @"|imap/v1/mapInfo/(stations|edges)/[0-9]+" +
        @"|imap/v1/mapResource/(removedEdge|removedStation)/[0-9]+" +
        @"|imap/v1/mapEdgeGroup/all" +
        @"|task/vehicles/(getAllVehicleSimpleInfo|getVehicleInfoByDeviceKey)" +
        @"|task/v1/task/getVehicleInfo/[^/]+" +
        @"|task/v1/route/" +
        @"|order/v1/orderRecord" +
        @"|order/v1/orderRecord/(detailByUpperId|detailByOrderId)/[^/]+" +
        @")$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReadingGet();

    /// <summary>The admin login and the three route queries: POSTs that change nothing.</summary>
    [GeneratedRegex(
        @"^/api/(auth/v1/admin/login|task/v1/route/(getRouteCostsBy|queryNearEnd|queryNearestStart))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReadingPost();
}

public sealed class RealRiotWriteRefusedException(string message) : Exception(message);
