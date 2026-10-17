using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Server.Mes;

namespace Simple.Server.Tests;

/// <summary>
/// Stands in for the MES gateway's snapshot API inside the server's HTTP
/// client. A test sets what the next fetches see: a round, or no answer.
/// Each answered fetch numbers the round anew, as the real gateway's rounds
/// go on.
/// </summary>
public sealed class StubMesGateway
{
    private readonly Lock gate = new();
    private long round;
    private IReadOnlyList<MesRow>? rows = [];
    private MesReadFailure? failure;
    private string? unreachable;
    private bool notReadYet;
    private bool frozen;
    private bool cutOff;

    public int Fetches { get; private set; }

    /// <summary>Good rounds with these rows.</summary>
    public void Answer(params MesRow[] answer) =>
        Set(() => (rows, failure, unreachable, notReadYet, frozen, cutOff) = (answer, null, null, false, false, false));

    /// <summary>The gateway restarted: its rounds start over from 1.</summary>
    public void RestartCounter() => Set(() => round = 0);

    /// <summary>The next round is <paramref name="next"/>, as for a gateway that has been up a while.</summary>
    public void ContinueFrom(long next) => Set(() => round = next - 1);

    /// <summary>The gateway sends the headers and part of the body, then the connection ends, as when it is killed mid-answer.</summary>
    public void CutOffMidBody() => Set(() => cutOff = true);

    /// <summary>The gateway answers, but its round stays where it is, as when a read hangs.</summary>
    public void Freeze() => Set(() => frozen = true);

    /// <summary>Failed rounds: the gateway answers, the MES did not.</summary>
    public void FailRounds(string kind, string message) =>
        Set(() => (rows, failure, unreachable, notReadYet) = (null, new MesReadFailure(kind, message), null, false));

    /// <summary>The gateway does not answer: every fetch fails like a refused connection.</summary>
    public void BeUnreachable(string error = "stub: the gateway refused the connection") => Set(() => unreachable = error);

    /// <summary>The gateway answers but has not read yet (round 0), as right after it starts.</summary>
    public void AnswerNotReadYet() => Set(() => (unreachable, notReadYet) = (null, true));

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
            Fetches++;
            if (unreachable is not null)
            {
                throw new HttpRequestException(unreachable);
            }

            if (request.RequestUri?.AbsolutePath != "/api/v1/snapshot")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var snapshot = notReadYet
                ? new MesSnapshot(0, null, false, null, null)
                : new MesSnapshot(frozen ? round : ++round, DateTimeOffset.UtcNow, failure is null, failure, rows);
            if (cutOff)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonSerializerOptions.Web);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EndsPrematurely(json[..(json.Length / 2)])),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot, options: JsonSerializerOptions.Web) };
        }
    }

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

    private sealed class Handler(StubMesGateway stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(stub.Respond(request));
    }
}
