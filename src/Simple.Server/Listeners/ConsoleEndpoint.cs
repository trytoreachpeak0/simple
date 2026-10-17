using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Events;
using Simple.Server.Persistence;

namespace Simple.Server.Listeners;

/// <summary>
/// The two addresses people on the plant network use (ADR 0046): the
/// console's HTTPS address, with the certificate the server makes once and
/// keeps, and the vehicle overview's plain HTTP address. Each listens on the
/// address of one network interface and serves only its own part
/// (<see cref="Listener.KeepToOwnListener"/>). Neither is configured by
/// default: development and CI use the API on loopback.
/// </summary>
public sealed partial class ConsoleEndpoint(
    IOptions<ServerSettings> settings, IServiceScopeFactory scopes, TimeProvider time, ILogger<ConsoleEndpoint> logger) : IDisposable
{
    /// <summary>How many days before the certificate expires the status says so (ADR 0046 leaves it to this ticket).</summary>
    public const int WarnDaysBefore = 90;

    private readonly Lock gate = new();
    private X509Certificate2? certificate;
    private string? problem;
    private ListenOptions? consoleListener;
    private ListenOptions? overviewListener;

    public string? ConsoleUrl => settings.Value.ConsoleUrl;

    public string? OverviewUrl => settings.Value.OverviewUrl;

    /// <summary>The console's certificate with its key, once <see cref="LoadAsync"/> has run; null before, or without a console address.</summary>
    public X509Certificate2? Certificate
    {
        get
        {
            lock (gate)
            {
                return certificate;
            }
        }
    }

    /// <summary>Binds the console's and the overview's addresses, when configured.</summary>
    public void Configure(KestrelServerOptions kestrel)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        if (ConsoleUrl is { } console)
        {
            Listener.Listen(kestrel, BindingAddress.Parse(console), listen =>
            {
                consoleListener = listen;
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                Listener.Mark(listen, ListenerKind.Console);
                // Kestrel takes its addresses before the database is read, so
                // the address is bound either way; without a usable certificate
                // (LoadAsync) every connection is closed at once.
                listen.Use(next => connection =>
                {
                    if (Certificate is null)
                    {
                        connection.Abort();
                        return Task.CompletedTask;
                    }

                    return next(connection);
                });
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    // Loaded from the database when the server starts, before
                    // it accepts a connection (LoadAsync).
                    ServerCertificateSelector = (_, _) => Certificate,
                    // factory01 (Windows Server 2016) has no TLS 1.3; the
                    // browsers all speak 1.2 (ADR 0046).
                    SslProtocols = SslProtocols.Tls12,
                });
            });
        }

        if (OverviewUrl is { } overview)
        {
            Listener.Listen(kestrel, BindingAddress.Parse(overview), listen =>
            {
                overviewListener = listen;
                listen.Protocols = HttpProtocols.Http1;
                Listener.Mark(listen, ListenerKind.Overview);
            });
        }
    }

    /// <summary>
    /// Reads the console's certificate, or makes it on the first start with a
    /// console address. When the stored certificate does not name the
    /// configured address and this machine, or its key is gone or not its
    /// own, only the console closes (the product owner, 2026-10-18): the
    /// server runs on, the console's address closes every connection, and the
    /// status and the program log say why and what to do. A new certificate
    /// has to be installed on every console PC, so it is never made without a
    /// person asking for it (<see cref="ConsoleCertificateCommands.DiscardName"/>).
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (ConsoleUrl is not { } url)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The console certificate's key lives in the Windows key store (CNG); the server runs on Windows.");
        }

        var address = IPAddress.Parse(BindingAddress.Parse(url).Host.Trim('[', ']'));
        // The DNS host name, as people type it; not the 15-character NetBIOS name.
        var machine = Dns.GetHostName();
        var keyName = settings.Value.ConsoleKeyName;
        var machineKey = settings.Value.ServerKeyIsMachineKey;
        var store = machineKey ? "machine" : $@"user {Environment.UserDomainName}\{Environment.UserName}";
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        var stored = await db.ConsoleCertificates.SingleOrDefaultAsync(c => c.Id == ConsoleCertificateRecord.TheOne, cancellationToken);
        X509Certificate2 loaded;
        if (stored is null)
        {
            try
            {
                using var key = OnboardTerminals.ServerCertificate.OpenOrCreateKey(keyName, machineKey);
                loaded = ConsoleCertificate.Create(key, address, machine, time.GetUtcNow());
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                Close($"The server cannot open or create the console certificate's key {keyName} in the {store} key store: {e.Message}");
                return;
            }

            db.ConsoleCertificates.Add(new ConsoleCertificateRecord { Certificate = loaded.RawData, CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            var names = ConsoleCertificate.NamesOf(loaded);
            var sha256 = ConsoleCertificate.Sha256Of(loaded);
            await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(
                EventTypes.ConsoleCertificateCreated,
                EventSources.Server,
                new { Sha256 = sha256, Names = names, NotAfter = new DateTimeOffset(loaded.NotAfter.ToUniversalTime()), KeyName = keyName, Store = store },
                cancellationToken);
            var createdNames = string.Join(", ", names);
            LogCreated(logger, sha256, createdNames, keyName, store);
        }
        else
        {
            X509Certificate2? joined;
            try
            {
                joined = ConsoleCertificate.Join(stored.Certificate, keyName, machineKey);
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                Close($"The server cannot open the console certificate's key {keyName} in the {store} key store: {e.Message}");
                return;
            }

            if (joined is null)
            {
                Close(
                    $"The console certificate's key {keyName} is not in the {store} key store, or is not the certificate's. "
                    + $"Restore the key from its backup, or run Simple.Server {ConsoleCertificateCommands.DiscardName} and restart, "
                    + "so that the server makes a new certificate, and install that one on every console PC (ADR 0046).");
                return;
            }

            loaded = joined;
            var names = ConsoleCertificate.NamesOf(loaded);
            if (!names.Contains(address.ToString()) || !names.Contains(machine, StringComparer.OrdinalIgnoreCase))
            {
                var listed = string.Join(", ", names);
                loaded.Dispose();
                Close(
                    $"The console certificate is for {listed}, but the server is {machine} listening at {url}. "
                    + $"If the address or the machine name has changed, run Simple.Server {ConsoleCertificateCommands.DiscardName} and restart, "
                    + "so that the server makes a new certificate, and install that one on every console PC (ADR 0046).");
                return;
            }

            var loadedSha256 = ConsoleCertificate.Sha256Of(loaded);
            var loadedNames = string.Join(", ", names);
            LogLoaded(logger, loadedSha256, loadedNames, keyName, store);
        }

        lock (gate)
        {
            certificate = loaded;
        }
    }

    /// <summary>Only the console closes; the server runs on (the product owner, 2026-10-18).</summary>
    private void Close(string why)
    {
        lock (gate)
        {
            problem = why;
        }

        LogClosed(logger, why);
    }

    /// <summary>For the status API: where the console is and how long its certificate lasts; null without a console address.</summary>
    public ConsoleEndpointStatus? Describe()
    {
        if (ConsoleUrl is not { } url)
        {
            return null;
        }

        string? closed;
        lock (gate)
        {
            closed = problem;
        }

        ConsoleCertificateStatus? shown = null;
        if (Certificate is { } current)
        {
            var notAfter = new DateTimeOffset(current.NotAfter.ToUniversalTime());
            var left = notAfter - time.GetUtcNow();
            shown = new ConsoleCertificateStatus(
                ConsoleCertificate.Sha256Of(current),
                ConsoleCertificate.NamesOf(current),
                notAfter,
                (int)Math.Floor(left.TotalDays),
                left <= TimeSpan.FromDays(WarnDaysBefore));
        }

        return new ConsoleEndpointStatus(Listener.Describe(url, consoleListener), shown is not null, closed, shown);
    }

    /// <summary>For the status API: the overview's plain address; null without one.</summary>
    public OverviewEndpointStatus? DescribeOverview() =>
        OverviewUrl is { } url ? new OverviewEndpointStatus(Listener.Describe(url, overviewListener)) : null;

    public void Dispose()
    {
        lock (gate)
        {
            // Releases the handle only: the persisted key stays.
            certificate?.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The console is closed; the server runs on. {Why}")]
    private static partial void LogClosed(ILogger logger, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Made the console certificate {Sha256} for {Names}, key {KeyName} in the {Store} key store. Export it (Simple.Server export-console-certificate) and install it on every console PC.")]
    private static partial void LogCreated(ILogger logger, string sha256, string names, string keyName, string store);

    [LoggerMessage(Level = LogLevel.Information, Message = "Console certificate {Sha256} for {Names}, key {KeyName} in the {Store} key store")]
    private static partial void LogLoaded(ILogger logger, string sha256, string names, string keyName, string store);
}

/// <summary>Loads the console certificate once the database is migrated, before the server takes connections.</summary>
public sealed class ConsoleCertificateLoader(ConsoleEndpoint endpoint) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => endpoint.LoadAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The one stored console certificate: the certificate only, never its key.</summary>
public sealed class ConsoleCertificateRecord
{
    public const int TheOne = 1;

    public int Id { get; init; } = TheOne;

    /// <summary>DER, what the console PCs install.</summary>
    public required byte[] Certificate { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <param name="Url">The console's HTTPS address.</param>
/// <param name="Serving">Whether the console answers there: false before the server has started, and while it is closed.</param>
/// <param name="Problem">Why the console is closed and what to do; null while it serves.</param>
/// <param name="Certificate">What the console PCs install; null while the console does not serve.</param>
public sealed record ConsoleEndpointStatus(string Url, bool Serving, string? Problem, ConsoleCertificateStatus? Certificate);

/// <param name="Sha256">The SHA-256 of the whole certificate, to check an installed copy against.</param>
/// <param name="Names">The IP addresses and machine name it is for.</param>
/// <param name="DaysLeft">Whole days until it expires.</param>
/// <param name="ExpiresSoon">It expires within <see cref="ConsoleEndpoint.WarnDaysBefore"/> days: a new one must be made and installed (ADR 0046).</param>
public sealed record ConsoleCertificateStatus(string Sha256, IReadOnlyList<string> Names, DateTimeOffset NotAfter, int DaysLeft, bool ExpiresSoon);

/// <param name="Url">The vehicle overview's plain HTTP address.</param>
public sealed record OverviewEndpointStatus(string Url);
