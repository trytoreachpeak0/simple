using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Simple.Cli.Scenarios;

/// <summary>
/// The part of a site's starting configuration (sites/&lt;site&gt;/site.yaml)
/// that scenarios use: which areas, task types and vehicles the site has.
/// The server does not import it yet; scenarios check their MES rows and
/// vehicles against it.
/// </summary>
public sealed record Site(string Name, IReadOnlyList<string> Areas, IReadOnlyList<string> TaskTypes, IReadOnlyList<SiteVehicle> Vehicles)
{
    public const string FileName = "site.yaml";

    public static string PathOf(string repository, string site) => System.IO.Path.Combine(repository, "sites", site, FileName);

    /// <summary>Reads a site's file; throws <see cref="InvalidDataException"/> saying where it is wrong.</summary>
    public static Site Read(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .Build();
        SiteFile? file;
        try
        {
            using var reader = File.OpenText(path);
            file = deserializer.Deserialize<SiteFile>(reader);
        }
        catch (YamlException e)
        {
            throw new InvalidDataException($"{path} line {e.Start.Line}, column {e.Start.Column}: {e.InnerException?.Message ?? e.Message}", e);
        }

        if (file?.Name is not { Length: > 0 } name)
        {
            throw new InvalidDataException($"{path} has no name.");
        }

        return new Site(
            name,
            file.Areas ?? [],
            file.TaskTypes ?? [],
            (file.Vehicles ?? []).Select(v => new SiteVehicle(v.Name ?? string.Empty, v.RiotDeviceKey ?? string.Empty)).ToList());
    }

    private sealed class SiteFile
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<string>? Areas { get; set; }

        public List<string>? TaskTypes { get; set; }

        public List<VehicleEntry>? Vehicles { get; set; }
    }

    private sealed class VehicleEntry
    {
        public string? Name { get; set; }

        public string? RiotDeviceKey { get; set; }
    }
}

/// <param name="RiotDeviceKey">How the fleet manager knows the vehicle.</param>
public sealed record SiteVehicle(string Name, string RiotDeviceKey);
