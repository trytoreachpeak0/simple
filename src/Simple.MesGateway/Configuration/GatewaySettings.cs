using System.Globalization;

namespace Simple.MesGateway.Configuration;

/// <summary>
/// The gateway's settings, read from configuration. The repository's
/// appsettings.json carries the fake source and a loopback address only;
/// anything real comes from a settings file outside the repository
/// (<c>Simple:SettingsFile</c>), environment variables or the command line.
/// </summary>
public sealed class GatewaySettings
{
    public const string Section = "MesGateway";
    public const string FakeSource = "Fake";

    /// <summary>Which MES source the gateway reads. <see cref="FakeSource"/> is the only one so far.</summary>
    public string? Source { get; set; }

    /// <summary>The wait after a good round before the next one.</summary>
    public TimeSpan RoundInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The first wait after a failed round; later waits grow from it, with jitter.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between retries (ADR 0008: 30 s).</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The fake source's starting rows. A relative path is relative to the gateway's own directory.</summary>
    public string? FakeSeedFile { get; set; }

    /// <summary>Every address the gateway is configured to listen on, keyed by where it came from.</summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

    /// <summary>Settings that could not be read; the validator reports them.</summary>
    public IList<string> ReadErrors { get; } = [];

    public string? FakeSeedPath =>
        string.IsNullOrWhiteSpace(FakeSeedFile) ? null : Path.GetFullPath(FakeSeedFile, AppContext.BaseDirectory);

    public bool UsesFakeSource => string.Equals(Source, FakeSource, StringComparison.OrdinalIgnoreCase);

    public void ReadFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        ReadErrors.Clear();
        Source = section["Source"];
        RoundInterval = Duration(section, "RoundInterval") ?? RoundInterval;
        RetryDelay = Duration(section, "RetryDelay") ?? RetryDelay;
        RetryMaxDelay = Duration(section, "RetryMaxDelay") ?? RetryMaxDelay;
        FakeSeedFile = section["Fake:SeedFile"];

        // The same keys the server checks (Simple.Server's ServerSettings).
        ListenAddresses.Clear();
        AddList("urls", configuration["urls"]);
        AddList("http_ports", configuration["http_ports"]);
        AddList("https_ports", configuration["https_ports"]);
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var key = $"Kestrel:Endpoints:{endpoint.Key}:Url";
            AddList(key, configuration[key]);
        }
    }

    private TimeSpan? Duration(IConfigurationSection section, string key)
    {
        var text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        ReadErrors.Add($"{Section}:{key} '{text}' is not a duration such as 00:00:05.");
        return null;
    }

    private void AddList(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ListenAddresses.Add(new(key, item));
        }
    }
}
