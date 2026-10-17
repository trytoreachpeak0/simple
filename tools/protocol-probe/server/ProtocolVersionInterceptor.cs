using Grpc.Core;
using Grpc.Core.Interceptors;
using Serilog;

namespace Simple.ProtocolProbe.Server;

/// <summary>
/// The protocol major travels on the connection as a request header (ADR 0026).
/// The server serves majors 1 and 2; anything else, or a header that does not
/// match the service the client called, is refused with FAILED_PRECONDITION
/// and a trailer listing what the server supports.
/// </summary>
internal sealed class ProtocolVersionInterceptor : Interceptor
{
    public const string MajorHeader = "simple-protocol-major";
    public const string SupportedTrailer = "simple-supported-majors";
    private static readonly int[] Supported = [1, 2];

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var header = context.RequestHeaders.GetValue(MajorHeader);
        var serviceMajor = context.Method.StartsWith("/simple.probe.v2.", StringComparison.Ordinal) ? 2 : 1;
        string? problem = null;
        if (!int.TryParse(header, System.Globalization.CultureInfo.InvariantCulture, out var major))
        {
            problem = $"missing or invalid {MajorHeader} header";
        }
        else if (!Supported.Contains(major))
        {
            problem = $"protocol major {major} is not supported; this server supports {string.Join(',', Supported)}";
        }
        else if (major != serviceMajor)
        {
            problem = $"header says major {major} but the call is to {context.Method}";
        }

        if (problem is null)
        {
            return continuation(requestStream, responseStream, context);
        }

        var info = ConnectionInfo.From(context);
        Log.Warning("REFUSE-VERSION conn={Conn} key={Key} method={Method} header={Header} status=FailedPrecondition detail={Detail}",
            info.ConnectionId, info.Key is null ? "-" : TerminalHub.Short(info.Key), context.Method, header ?? "-", problem);
        throw new RpcException(
            new Status(StatusCode.FailedPrecondition, problem),
            new Metadata { { SupportedTrailer, string.Join(',', Supported) } });
    }
}
