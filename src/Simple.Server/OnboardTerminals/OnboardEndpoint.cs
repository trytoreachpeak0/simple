using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Simple.Protocol;
using Simple.Server.Configuration;
using Simple.Server.Status;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// The listeners of the server process (ADR 0025). The API listens on
/// loopback only, over plain HTTP, because it has no authentication yet. The
/// onboard terminal endpoint may listen on an address the vehicles reach,
/// because nothing but the terminal protocol is served there, over TLS 1.2
/// with the certificate the terminals pin. Requests are kept to their own
/// listener by the connection they arrive on (<see cref="KeepToOwnListener"/>),
/// not by host name or port, which a caller chooses.
/// </summary>
public sealed partial class OnboardEndpoint(IOptions<ServerSettings> settings, ILogger<OnboardEndpoint> logger) : IDisposable
{
    private const string TerminalListenerItem = "Simple.OnboardTerminalListener";

    private readonly Lock gate = new();
    private X509Certificate2? certificate;
    private ListenOptions? listener;

    public string? ConfiguredUrl => settings.Value.OnboardTerminalsUrl;

    /// <summary>Whether this process had to create the server key: a new key, a new pin.</summary>
    public bool KeyCreated { get; private set; }

    /// <summary>The certificate terminals pin, made from the server's persisted key on first use.</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (gate)
            {
                if (certificate is null)
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("The server's key lives in the Windows key store (CNG); the server runs on Windows.");
                    }

                    var store = settings.Value.ServerKeyIsMachineKey ? "machine" : $@"user {Environment.UserDomainName}\{Environment.UserName}";
                    (certificate, KeyCreated) = ServerCertificate.LoadOrCreate(settings.Value.ServerKeyName, settings.Value.ServerKeyIsMachineKey);
                    var pin = CertificatePin.Of(certificate);
                    if (KeyCreated)
                    {
                        // Never silently: on a server that had a key before
                        // (another account, a rebuilt profile) every terminal
                        // now refuses it until given the new pin.
                        LogKeyCreated(logger, settings.Value.ServerKeyName, store, pin);
                    }
                    else
                    {
                        LogKeyLoaded(logger, settings.Value.ServerKeyName, store, pin);
                    }
                }

                return certificate;
            }
        }
    }

    /// <summary>Binds the API's loopback addresses and, if configured, the terminal endpoint.</summary>
    public void Configure(KestrelServerOptions kestrel)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        // Kestrel sends no HTTP/2 keep-alive pings by default, so a stream
        // whose link died silently stays open until TCP gives up (#18). The
        // pings only reclaim dead streams; whether a terminal's reports are
        // fresh is judged by the reports themselves.
        kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(15);
        kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(15);

        // Binding in code makes Kestrel ignore `urls`, so the API's addresses
        // (checked to be loopback http:// by ServerSettingsValidator) are
        // bound here too.
        foreach (var (key, url) in settings.Value.ListenAddresses)
        {
            if (key == "urls")
            {
                Listen(kestrel, BindingAddress.Parse(url), _ => { });
            }
        }

        if (ConfiguredUrl is { } terminalUrl)
        {
            var certificate = Certificate;
            Listen(kestrel, BindingAddress.Parse(terminalUrl), listen =>
            {
                listener = listen;
                listen.Protocols = HttpProtocols.Http2;
                listen.Use(next => connection =>
                {
                    connection.Items[TerminalListenerItem] = true;
                    return next(connection);
                });
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    // Neither factory01 (Windows Server 2016) nor the onboard
                    // PCs (Windows 10) have TLS 1.3, so it is 1.2 everywhere,
                    // also on machines that would offer 1.3.
                    SslProtocols = SslProtocols.Tls12,
                    // Terminal keys and pairing are a later ticket (ADR 0025).
                    ClientCertificateMode = ClientCertificateMode.NoCertificate,
                });
            });
        }
    }

    /// <summary>For the status API: where terminals connect and what they pin; null without a terminal endpoint.</summary>
    public OnboardTerminalEndpointStatus? Describe()
    {
        if (ConfiguredUrl is not { } url)
        {
            return null;
        }

        // Once bound, the listener knows its port, also when 0 was configured.
        if (listener?.IPEndPoint is { Port: > 0 } bound)
        {
            var address = BindingAddress.Parse(url);
            url = $"{address.Scheme}://{address.Host}:{bound.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return new OnboardTerminalEndpointStatus(url, CertificatePin.Of(Certificate));
    }

    /// <summary>
    /// Middleware, after routing: on the terminal listener only the terminal
    /// protocol is found, and the terminal protocol is found nowhere else.
    /// </summary>
    public static Task KeepToOwnListener(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var onTerminalListener = context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(TerminalListenerItem) == true;
        var isTerminalProtocol = context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>() is not null;
        if (onTerminalListener != isTerminalProtocol)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }

    public void Dispose()
    {
        lock (gate)
        {
            // Releases the handle only: the persisted key stays.
            certificate?.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No server key {KeyName} in the {Store} key store: created a new one. Certificate pin {Pin}. If this server had a key before, its pin has changed and every terminal pinned to the old one refuses this server until it is given the new pin.")]
    private static partial void LogKeyCreated(ILogger logger, string keyName, string store, string pin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server key {KeyName} from the {Store} key store, certificate pin {Pin}")]
    private static partial void LogKeyLoaded(ILogger logger, string keyName, string store, string pin);

    private static void Listen(KestrelServerOptions kestrel, BindingAddress address, Action<ListenOptions> configure)
    {
        var host = address.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(address.Port, configure);
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(host), address.Port, configure);
        }
    }
}
