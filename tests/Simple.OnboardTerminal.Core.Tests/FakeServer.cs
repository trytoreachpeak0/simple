using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Simple.Protocol;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>
/// A stand-in for the server's terminal endpoint: TLS 1.2, HTTP/2, a
/// certificate from a named persisted key (as the real server's), a desired
/// state every <see cref="StateInterval"/>, and every report kept for the
/// test to look at. It can
/// be stopped and started again on the same port, or refuse the stream as a
/// server of other protocol majors would.
/// </summary>
public sealed class FakeServer : IAsyncDisposable
{
    private readonly string keyName = $"Simple-Test-{Guid.NewGuid():N}";
    private readonly X509Certificate2 certificate;
    private WebApplication? app;

    public FakeServer()
    {
        using var key = CngKey.Create(
            CngAlgorithm.ECDsaP256,
            keyName,
            new CngKeyCreationParameters
            {
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = TestKeyStore.IsMachine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            });
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest("CN=fake-simple-server", ecdsa, HashAlgorithmName.SHA256);
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Pin = CertificatePin.Of(certificate);
    }

    public string Pin { get; }

    public int Port { get; private set; }

    public string Url => $"https://127.0.0.1:{Port}";

    /// <summary>When set, every stream is refused with FAILED_PRECONDITION and these majors in the trailer.</summary>
    public string? RefuseServingMajors { get; set; }

    public ConcurrentQueue<OnboardReport> Reports { get; } = new();

    /// <summary>How often a desired state goes out after the first; null for none after the first, so that no state wakes a report.</summary>
    public TimeSpan? StateInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public ConcurrentQueue<string?> MajorsAsked { get; } = new();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.SslProtocols = SslProtocols.Tls12;
            });
        }));
        app = builder.Build();
        app.MapGrpcService<Link>();
        await app.StartAsync(TestContext.Current.CancellationToken);
        Port = app.Urls.Select(u => new Uri(u).Port).Single();
    }

    /// <summary>Stops listening and ends every stream, as a server process that goes away.</summary>
    public async Task StopAsync()
    {
        if (app is not null)
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        certificate.Dispose();
        using var key = CngKey.Open(
            keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, TestKeyStore.IsMachine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        key.Delete();
    }

    private sealed class Link(FakeServer server, Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime) : OnboardLink.OnboardLinkBase
    {
        public override async Task Connect(IAsyncStreamReader<OnboardReport> requestStream, IServerStreamWriter<DesiredState> responseStream, ServerCallContext context)
        {
            server.MajorsAsked.Enqueue(context.RequestHeaders.GetValue(ProtocolVersion.MajorHeader));
            if (server.RefuseServingMajors is { } majors)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "not served"), new Metadata { { ProtocolVersion.SupportedMajorsTrailer, majors } });
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
            ulong seq = 0;
            ulong lastReport = 0;
            var sending = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await responseStream.WriteAsync(
                        new DesiredState { Seq = ++seq, LastReceivedSeq = Interlocked.Read(ref lastReport), Screen = new Screen { Text = "测试画面" } },
                        stop.Token);
                    await Task.Delay(server.StateInterval ?? Timeout.InfiniteTimeSpan, stop.Token);
                }
            });
            try
            {
                await foreach (var report in requestStream.ReadAllAsync(stop.Token))
                {
                    Interlocked.Exchange(ref lastReport, report.Seq);
                    server.Reports.Enqueue(report);
                }
            }
            catch (OperationCanceledException)
            {
                // The fake server is stopping.
            }
            finally
            {
                await stop.CancelAsync();
                try
                {
                    await sending;
                }
                catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException)
                {
                    // The stream is gone.
                }
            }
        }
    }
}
