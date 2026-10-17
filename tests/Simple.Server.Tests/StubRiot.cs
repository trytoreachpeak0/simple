using System.Net;
using System.Text;

namespace Simple.Server.Tests;

/// <summary>
/// Stands in for RIoT inside the server's <c>riot</c> HTTP client: answers
/// the map list in RIoT's envelope for the right call key, or fails the way
/// a test asks.
/// </summary>
public sealed class StubRiot
{
    public const string CallApiKey = "stub-riot-call-api-key";

    private readonly Lock gate = new();
    private string? unreachable;
    private bool cutOff;
    private bool businessFailure;

    public int Calls { get; private set; }

    public string? LastAuthorization { get; private set; }

    /// <summary>RIoT answers again.</summary>
    public void Answer() => Set(() => (unreachable, cutOff, businessFailure) = (null, false, false));

    /// <summary>Every call fails like a refused connection.</summary>
    public void BeUnreachable(string error = "stub: RIoT refused the connection") => Set(() => unreachable = error);

    /// <summary>RIoT sends part of the body, then the connection ends (HttpIOException, simple#37).</summary>
    public void CutOffMidBody() => Set(() => cutOff = true);

    /// <summary>HTTP 200 with RIoT's general internal error.</summary>
    public void FailWithInternalError() => Set(() => businessFailure = true);

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private void Set(Action change)
    {
        lock (gate)
        {
            change();
        }
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        lock (gate)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (unreachable is not null)
            {
                throw new HttpRequestException(unreachable);
            }

            if (request.RequestUri?.AbsolutePath != "/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (LastAuthorization != $"Bearer {CallApiKey}")
            {
                return Json(HttpStatusCode.Unauthorized, """{"code":"401","message":"暂未登录或token已经过期","msgDetail":"","tid":""}""");
            }

            const string maps = """{"code":"0","message":"成功","msgDetail":"","result":[{"id":25,"name":"老厂前线new"},{"id":26,"name":"老厂前线new_wk"}],"tid":""}""";
            if (cutOff)
            {
                var bytes = Encoding.UTF8.GetBytes(maps);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndsPrematurely(bytes[..(bytes.Length / 2)])) };
            }

            return businessFailure
                ? Json(HttpStatusCode.OK, """{"code":"00002","message":"内部错误","msgDetail":"java.lang.NullPointerException","tid":""}""")
                : Json(HttpStatusCode.OK, maps);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Gives the bytes it has, then fails the way SocketsHttpHandler does when the connection ends early.</summary>
    private sealed class EndsPrematurely(byte[] head) : MemoryStream(head)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, count) is > 0 and var read ? read : throw Ended();

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer) is > 0 and var read ? read : throw Ended();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        private static HttpIOException Ended() =>
            new(HttpRequestError.ResponseEnded, "The response ended prematurely. (stub)");
    }

    private sealed class Handler(StubRiot stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(stub.Respond(request));
    }
}
