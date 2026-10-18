using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Simple.Cli.Scenarios;

/// <summary>
/// The part of a site's starting configuration (sites/&lt;site&gt;/site.yaml)
/// that scenarios check against: which areas, task types and vehicles the
/// site has. The server reads and validates the whole file when it imports it
/// (<c>import-site</c>, which <c>simple up</c> runs); this reads only what the
/// command line needs and leaves the rest to the server.
/// </summary>
public sealed record Site(string Name, IReadOnlyList<string> Areas, IReadOnlyList<string> TaskTypes, IReadOnlyList<SiteVehicle> Vehicles)
{
    public const string FileName = "site.yaml";

    /// <summary>The site <c>simple up</c> imports unless told another: the fictional one scenarios use.</summary>
    public const string Default = "sample";

    public static string PathOf(string repository, string site) => System.IO.Path.Combine(repository, "sites", site, FileName);

    /// <summary>Reads a site's file; throws <see cref="InvalidDataException"/> saying where it is wrong.</summary>
    public static Site Read(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
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
            [.. (file.Areas ?? []).Select(a => (a.Area ?? string.Empty).Trim())],
            [.. (file.TaskTypes ?? []).Select(t => t.Code ?? string.Empty)],
            [.. (file.Vehicles ?? []).Select(v => new SiteVehicle(v.Id ?? string.Empty, v.RiotDeviceKey ?? string.Empty))]);
    }

    private sealed class SiteFile
    {
        public string? Name { get; set; }

        public List<AreaEntry>? Areas { get; set; }

        public List<TaskTypeEntry>? TaskTypes { get; set; }

        public List<VehicleEntry>? Vehicles { get; set; }
    }

    private sealed class AreaEntry
    {
        public string? Area { get; set; }
    }

    private sealed class TaskTypeEntry
    {
        public string? Code { get; set; }
    }

    private sealed class VehicleEntry
    {
        public string? Id { get; set; }

        public string? RiotDeviceKey { get; set; }
    }
}

/// <param name="Id">The vehicle ID SIMPLE assigned (ADR 0007).</param>
/// <param name="RiotDeviceKey">How the fleet manager knows the vehicle.</param>
public sealed record SiteVehicle(string Id, string RiotDeviceKey);
