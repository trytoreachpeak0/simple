using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Simple.Protocol;
using Simple.Server.Configuration;
using Simple.Server.Listeners;
using Simple.Server.Status;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// The API's loopback addresses and the onboard terminal endpoint (ADR 0025).
/// The terminal endpoint may listen on an address the vehicles reach,
/// because nothing but the terminal protocol is served there, over TLS 1.2
/// with the certificate the terminals pin (see <see cref="Listener.KeepToOwnListener"/>).
/// The console's and the overview's addresses are <see cref="ConsoleEndpoint"/>'s.
/// </summary>
public sealed partial class OnboardEndpoint(IOptions<ServerSettings> settings, ILogger<OnboardEndpoint> logger) : IDisposable
{
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
                Listener.Listen(kestrel, BindingAddress.Parse(url), _ => { });
            }
        }

        if (ConfiguredUrl is { } terminalUrl)
        {
            var certificate = Certificate;
            Listener.Listen(kestrel, BindingAddress.Parse(terminalUrl), listen =>
            {
                listener = listen;
                listen.Protocols = HttpProtocols.Http2;
                Listener.Mark(listen, ListenerKind.Terminal);
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    // Neither factory01 (Windows Server 2016) nor the onboard
                    // PCs (Windows 10) have TLS 1.3, so it is 1.2 everywhere,
                    // also on machines that would offer 1.3.
                    SslProtocols = SslProtocols.Tls12,
                    // Every terminal presents a certificate made from its own
                    // key (ADR 0025). Any self-signed one gets through the
                    // handshake: which key it is, and whether its reports are
                    // used, is the registry's call (OnboardTerminalRegistry).
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (_, _, _) => true,
                    CheckCertificateRevocation = false,
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
        return new OnboardTerminalEndpointStatus(Listener.Describe(url, listener), CertificatePin.Of(Certificate));
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
}
