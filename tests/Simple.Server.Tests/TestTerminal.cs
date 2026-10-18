using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
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
/// terminal does, and presents a certificate made from its own key
/// (<see cref="TestTerminalKey"/>).
/// </summary>
public sealed class TestTerminal : IAsyncDisposable
{
    private readonly GrpcChannel channel;
    private readonly AsyncDuplexStreamingCall<OnboardReport, DesiredState> call;
    private readonly TestTerminalKey? ownKey;
    private ulong lastSentSeq;

    private TestTerminal(GrpcChannel channel, AsyncDuplexStreamingCall<OnboardReport, DesiredState> call, TestTerminalKey key, bool ownsKey)
    {
        this.channel = channel;
        this.call = call;
        Key = key;
        ownKey = ownsKey ? key : null;
    }

    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ulong LastReceivedSeq { get; private set; }

    public TestTerminalKey Key { get; }

    /// <summary>The run id its reports carry: one run of a terminal program.</summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The terminal endpoint and its pin, from the server's status.</summary>
    public static async Task<OnboardTerminalEndpointStatus> EndpointAsync(HttpClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var status = await api.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);
        return status!.OnboardTerminalEndpoint ?? throw new InvalidOperationException("The server has no terminal endpoint.");
    }

    /// <summary>A channel to the terminal endpoint that trusts exactly the published pin and presents <paramref name="certificate"/>, if any.</summary>
    public static GrpcChannel Channel(OnboardTerminalEndpointStatus endpoint, X509Certificate2? certificate) =>
        GrpcChannel.ForAddress(endpoint.Url, new GrpcChannelOptions { HttpHandler = Handler(endpoint, certificate), DisposeHttpClient = true });

    /// <summary>HTTP over TLS to the terminal endpoint, trusting exactly the published pin and presenting <paramref name="certificate"/>, if any.</summary>
    public static SocketsHttpHandler Handler(OnboardTerminalEndpointStatus endpoint, X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var tls = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) =>
            {
                using var server = X509CertificateLoader.LoadCertificate(presented!.GetRawCertData());
                return CertificatePin.Matches(endpoint.CertificatePin, CertificatePin.Of(server));
            },
        };
        if (certificate is not null)
        {
            tls.ClientCertificates = [certificate];
            tls.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        return new SocketsHttpHandler { UseProxy = false, SslOptions = tls };
    }

    /// <summary>Opens a stream presenting <paramref name="key"/>, or a key of its own that it deletes when disposed.</summary>
    public static async Task<TestTerminal> ConnectAsync(HttpClient api, string? major = "1", TestTerminalKey? key = null)
    {
        var owns = key is null;
        key ??= new TestTerminalKey();
        var channel = Channel(await EndpointAsync(api), key.Certificate);
        var headers = new Metadata();
        if (major is not null)
        {
            headers.Add(ProtocolVersion.MajorHeader, major);
        }

        var call = new OnboardLink.OnboardLinkClient(channel).Connect(headers, cancellationToken: Cancel);
        return new TestTerminal(channel, call, key, owns);
    }

    /// <summary>
    /// Opens a stream as <see cref="ConnectAsync"/> does and sends a first
    /// report claiming <paramref name="vehicle"/>; the administrator then
    /// confirms its key with the pairing code the server shows, as an engineer
    /// at the vehicle would. Its next report is used.
    /// </summary>
    public static async Task<TestTerminal> ConnectConfirmedAsync(HttpClient api, string vehicle = "vehicle-1", TestTerminalKey? key = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        var terminal = await ConnectAsync(api, key: key);
        await terminal.ReceiveAsync();
        await terminal.ReportAsync(vehicle);
        var code = await Api.PairingCodeAsync(api, terminal.Key.Pin, vehicle);
        using var confirmed = await Api.ConfirmAsync(api, vehicle, code);
        confirmed.EnsureSuccessStatusCode();
        return terminal;
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
            RunId = RunId,
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
        ownKey?.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// A terminal's own key for a test: a named, persisted, non-exportable CNG
/// key (ECDSA P-256), as a real terminal's, and a certificate made from it;
/// SChannel takes no key that lives in memory only (ADR 0025). Named
/// Simple-Test- and 32 hex digits, which the remote machine's sweep knows,
/// in the key store the tests were told (<see cref="TestKeyStore"/>), and
/// deleted when disposed.
/// </summary>
public sealed class TestTerminalKey : IDisposable
{
    public TestTerminalKey()
    {
        Name = $"Simple-Test-{Guid.NewGuid():N}";
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, Name, new CngKeyCreationParameters
        {
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = TestKeyStore.IsMachine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
        });
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest("CN=simple-test-terminal", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Pin = CertificatePin.Of(Certificate);
    }

    public string Name { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>What the server knows the key by.</summary>
    public string Pin { get; }

    public void Dispose()
    {
        Certificate.Dispose();
        var options = TestKeyStore.IsMachine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (CngKey.Exists(Name, CngProvider.MicrosoftSoftwareKeyStorageProvider, options))
        {
            using var key = CngKey.Open(Name, CngProvider.MicrosoftSoftwareKeyStorageProvider, options);
            key.Delete();
        }
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

    /// <summary>The pairing code the pending list shows for <paramref name="keyPin"/> claiming <paramref name="vehicle"/>, once it shows.</summary>
    public static async Task<string> PairingCodeAsync(HttpClient api, string keyPin, string vehicle = "vehicle-1")
    {
        var status = await UntilAsync(() => StatusAsync(api), s => s.PendingOnboardTerminals.Any(p => p.KeyPin == keyPin && p.VehicleId == vehicle));
        return status.PendingOnboardTerminals.Single(p => p.KeyPin == keyPin && p.VehicleId == vehicle).PairingCode;
    }

    public static Task<HttpResponseMessage> ConfirmAsync(HttpClient api, string vehicle, string code) =>
        api.PostAsJsonAsync(new Uri($"/api/v1/onboard-terminals/{vehicle}/confirm", UriKind.Relative), new { PairingCode = code }, TestTerminal.Cancel);

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
