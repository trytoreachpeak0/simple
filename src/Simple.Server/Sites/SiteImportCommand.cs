using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;
using Simple.Server.Riot;

namespace Simple.Server.Sites;

/// <summary>
/// <c>Simple.Server import-site &lt;file&gt;</c>: imports a site's starting
/// configuration (<c>sites/&lt;site&gt;/site.yaml</c>) at first install (ADR
/// 0017). It takes the server's own settings, migrates the database, reads
/// every map from the configured fleet manager (read only), validates the
/// whole configuration against them, and stores it as version 1 with every
/// task type switched off, recording the import in the event record. Once the
/// server has a configuration it imports nothing: from then on the server's
/// configuration is the live one, changed only through its API. Running it
/// again with the same site is harmless, which is what <c>simple up</c> does
/// before every start.
/// </summary>
public static class SiteImportCommand
{
    public const string Name = "import-site";

    /// <summary>Imported, or the server already has this site's configuration.</summary>
    public const int Imported = 0;

    /// <summary>Nothing imported: the file cannot be read, the configuration is not valid, or the server holds another site's.</summary>
    public const int NotImported = 2;

    /// <summary>Nothing imported: the fleet manager's maps could not be read.</summary>
    public const int MapsUnreadable = 3;

    public static async Task<int> RunAsync(IServiceProvider services, string? path, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrWhiteSpace(path))
        {
            await output.WriteLineAsync($"Nothing imported: give the site file, for example {Name} sites/d8/{SiteFile.FileName}.");
            return NotImported;
        }

        SiteFileRead read;
        try
        {
            read = SiteFile.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Nothing imported: {path} cannot be read: {e.Message}");
            return NotImported;
        }

        if (read.Configuration is not { } configuration)
        {
            return await RefuseAsync(output, $"Nothing imported: {path} is not a valid site file.", read.Problems);
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>();
        await db.Database.MigrateAsync(cancellationToken);
        if (await store.CurrentSummaryAsync(cancellationToken) is { } current)
        {
            return await AlreadyAsync(output, current, configuration.Site);
        }

        FleetMaps maps;
        try
        {
            maps = await scope.ServiceProvider.GetRequiredService<RiotMapReader>().ReadAsync(cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync($"Nothing imported: the fleet manager's maps could not be read, so the configuration cannot be checked against them: {e.Message}");
            return MapsUnreadable;
        }

        if (SiteValidator.Validate(configuration, maps) is { Count: > 0 } problems)
        {
            return await RefuseAsync(output, $"Nothing imported: the configuration in {path} is not valid.", problems);
        }

        // A first import switches every task type off (ADR 0017).
        configuration = configuration.WithEverySwitchOff();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var events = scope.ServiceProvider.GetRequiredService<EventStore>();
        var stored = await store.AddAsync(
            configuration,
            SiteVersionSources.Import,
            time.GetUtcNow(),
            basedOn: 0,
            version => events.AppendAsync(
                EventTypes.SiteConfigurationImported,
                EventSources.ServerCommand,
                new
                {
                    version.Version,
                    configuration.Site,
                    Projects = configuration.Projects.Count,
                    Zones = configuration.Zones.Count,
                    Areas = configuration.Areas.Count,
                    TaskTypes = configuration.TaskTypes.Count,
                    Vehicles = configuration.Vehicles.Count,
                },
                cancellationToken),
            cancellationToken);
        if (stored is null)
        {
            // Another import got there first, between the check above and the write.
            return await AlreadyAsync(output, (await store.CurrentSummaryAsync(cancellationToken))!, configuration.Site);
        }

        var version = stored.Version;
        await output.WriteLineAsync(
            $"Imported site {configuration.Site} as version {version.ToString(CultureInfo.InvariantCulture)}: "
            + $"{configuration.Projects.Count} projects, {configuration.Zones.Count} dispatch zones, {configuration.Areas.Count} areas, "
            + $"{configuration.TaskTypes.Count} task types (all switched off), {configuration.Vehicles.Count} vehicles.");
        return Imported;
    }

    private static async Task<int> AlreadyAsync(TextWriter output, SiteVersionSummary current, string site)
    {
        if (current.Site != site)
        {
            await output.WriteLineAsync(
                $"Nothing imported: the server already has the configuration of site {current.Site} (version {current.Version}), not {site}. "
                + "A server's configuration is imported once; for another site, use another instance or start from an empty database.");
            return NotImported;
        }

        await output.WriteLineAsync($"Nothing imported: the server already has the configuration of site {site}, version {current.Version}; it is changed only through the server.");
        return Imported;
    }

    private static async Task<int> RefuseAsync(TextWriter output, string headline, IReadOnlyList<SiteProblem> problems)
    {
        await output.WriteLineAsync(headline);
        foreach (var problem in problems)
        {
            await output.WriteLineAsync($"  {problem}");
        }

        return NotImported;
    }
}
