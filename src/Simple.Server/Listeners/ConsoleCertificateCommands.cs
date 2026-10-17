using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;

namespace Simple.Server.Listeners;

/// <summary>
/// Server commands for the console's certificate (ADR 0046), run on the
/// server machine with the server's own settings. Neither touches the key:
/// the server makes and opens it under the account it runs as, which need not
/// be the account of whoever runs a command.
/// </summary>
public static class ConsoleCertificateCommands
{
    /// <summary><c>Simple.Server export-console-certificate &lt;file&gt;</c>: writes the certificate (DER, <c>.cer</c>) to install on a console PC.</summary>
    public const string ExportName = "export-console-certificate";

    /// <summary>
    /// <c>Simple.Server discard-console-certificate</c>: forgets the stored
    /// certificate, so that the server's next start makes a new one for the
    /// address and machine name it has then. Every console PC then installs the
    /// new one.
    /// </summary>
    public const string DiscardName = "discard-console-certificate";

    public static async Task<int> ExportAsync(IServiceProvider services, string? file, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrWhiteSpace(file))
        {
            await output.WriteLineAsync($"Give the file to write: Simple.Server {ExportName} <file>.cer");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        var stored = await db.ConsoleCertificates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            await output.WriteLineAsync("There is no console certificate yet: the server makes it at its first start with Simple:Console:Url set.");
            return 2;
        }

        var path = Path.GetFullPath(file);
        await File.WriteAllBytesAsync(path, stored.Certificate, cancellationToken);
        using var certificate = X509CertificateLoader.LoadCertificate(stored.Certificate);
        await output.WriteLineAsync(
            $"Wrote the console certificate to {path}: SHA-256 {ConsoleCertificate.Sha256Of(certificate)}, for {string.Join(", ", ConsoleCertificate.NamesOf(certificate))}, valid until {certificate.NotAfter:yyyy-MM-dd}.");
        return 0;
    }

    public static async Task<int> DiscardAsync(IServiceProvider services, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var discarded = await db.ConsoleCertificates.ExecuteDeleteAsync(cancellationToken);
        if (discarded > 0)
        {
            await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(
                EventTypes.ConsoleCertificateDiscarded, new Actor(null, EventSources.ServerCommand, null), null, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await output.WriteLineAsync(discarded > 0
            ? "The console certificate is discarded. The server makes a new one at its next start; install that one on every console PC."
            : "There was no console certificate to discard.");
        return 0;
    }
}
