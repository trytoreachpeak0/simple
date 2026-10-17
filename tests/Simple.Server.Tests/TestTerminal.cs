using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Simple.Protocol;
using Simple.Protocol.V1;
using Simple.Server.Events;
using Simple.Server.Status;

namespace Simple.Server.Tests;

/// <summary>
/// A terminal driven by hand, message by message, over a real TLS stream to
/// the terminal endpoint of a server started with Kestrel. It trusts the
/// server's certificate by the pin the server's status publishes, as a real
/// terminal does.
/// </summary>
public sealed class TestTerminal : IAsyncDisposable
{
    private readonly GrpcChannel channel;
    private readonly AsyncDuplexStreamingCall<OnboardReport, DesiredState> call;
    private ulong lastSentSeq;

    private TestTerminal(GrpcChannel channel, AsyncDuplexStreamingCall<OnboardReport, DesiredState> call)
    {
        this.channel = channel;
        this.call = call;
    }

    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ulong LastReceivedSeq { get; private set; }

    /// <summary>The terminal endpoint and its pin, from the server's status.</summary>
    public static async Task<OnboardTerminalEndpointStatus> EndpointAsync(HttpClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var status = await api.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        return status!.OnboardTerminalEndpoint ?? throw new InvalidOperationException("The server has no terminal endpoint.");
    }

    /// <summary>A channel to the terminal endpoint that trusts exactly the published pin.</summary>
    public static GrpcChannel Channel(OnboardTerminalEndpointStatus endpoint) =>
        GrpcChannel.ForAddress(endpoint.Url, new GrpcChannelOptions { HttpHandler = Handler(endpoint), DisposeHttpClient = true });

    /// <summary>HTTP over TLS to the terminal endpoint, trusting exactly the published pin.</summary>
    public static SocketsHttpHandler Handler(OnboardTerminalEndpointStatus endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    using var presented = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
                    return CertificatePin.Matches(endpoint.CertificatePin, CertificatePin.Of(presented));
                },
            },
        };
    }

    public static async Task<TestTerminal> ConnectAsync(HttpClient api, string? major = "1")
    {
        var channel = Channel(await EndpointAsync(api));
        var headers = new Metadata();
        if (major is not null)
        {
            headers.Add(ProtocolVersion.MajorHeader, major);
        }

        var call = new OnboardLink.OnboardLinkClient(channel).Connect(headers, cancellationToken: Cancel);
        return new TestTerminal(channel, call);
    }

    /// <summary>The next desired state the server sends.</summary>
    public async Task<DesiredState> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (!await call.ResponseStream.MoveNext(timeout.Token))
        {
            throw new InvalidOperationException("The server ended the stream.");
        }

        LastReceivedSeq = call.ResponseStream.Current.Seq;
        return call.ResponseStream.Current;
    }

    /// <summary>Sends a report naming the latest state received (or <paramref name="acknowledging"/>).</summary>
    public async Task ReportAsync(string vehicle = "vehicle-1", ulong? acknowledging = null, params OnboardInput[] inputs)
    {
        var report = new OnboardReport
        {
            Seq = ++lastSentSeq,
            LastReceivedSeq = acknowledging ?? LastReceivedSeq,
            TerminalVersion = "test-terminal-1",
            VehicleId = vehicle,
        };
        report.Inputs.Add(inputs);
        await call.RequestStream.WriteAsync(report, Cancel);
    }

    /// <summary>Ends the terminal's half of the stream, as a terminal that stops does, and waits for the server to end its half.</summary>
    public async Task CloseAsync()
    {
        await call.RequestStream.CompleteAsync();
        while (await call.ResponseStream.MoveNext(Cancel))
        {
            // Drain until the server ends the stream.
        }
    }

    public async Task<RpcException> FailureAsync()
    {
        return await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(Cancel))
            {
                // Read on until the server refuses.
            }
        });
    }

    public static OnboardInput Scan(ulong id, string text) => new() { Id = id, Scan = new Scan { Text = text } };

    public async ValueTask DisposeAsync()
    {
        call.Dispose();
        channel.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>Reads what a test is waiting for from the server's API, polling until it shows or the wait runs out.</summary>
public static class Api
{
    public static async Task<StatusResponse> StatusAsync(HttpClient api) =>
        (await api.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), TestTerminal.Cancel))!;

    public static async Task<IReadOnlyList<EventView>> EventsAsync(HttpClient api, string type)
    {
        var page = await api.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events?limit=1000", UriKind.Relative), TestTerminal.Cancel);
        return page!.Items.Where(e => e.Type == type).ToList();
    }

    /// <summary>Polls until <paramref name="found"/> holds; the last value otherwise, for the assertion to report.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> found, double seconds = 10)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(found);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await read();
            if (found(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(50, TestTerminal.Cancel);
        }
    }

    public static string Text(this EventView e, string property) =>
        e.Data!.Value.GetProperty(property).ToString() ?? string.Empty;
}
