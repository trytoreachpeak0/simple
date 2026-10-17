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

    public int Fetches { get; private set; }

    /// <summary>Good rounds with these rows.</summary>
    public void Answer(params MesRow[] answer) => Set(() => (rows, failure, unreachable, notReadYet, frozen) = (answer, null, null, false, false));

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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot, options: JsonSerializerOptions.Web) };
        }
    }

    private sealed class Handler(StubMesGateway stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(stub.Respond(request));
    }
}
