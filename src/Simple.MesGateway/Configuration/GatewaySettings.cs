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
    /// <summary>
    /// Under Simple, like the server's settings, so <c>simple up</c> strips
    /// <c>Simple__*</c> variables from the caller's environment for the
    /// gateway too and nothing real reaches a local gateway that way.
    /// </summary>
    public const string Section = "Simple:Mes";
    public const string FakeSource = "Fake";
    public const string OracleSource = "Oracle";

    /// <summary>Which MES source the gateway reads: <see cref="FakeSource"/>, the repository's default, or <see cref="OracleSource"/>, the customer's MES.</summary>
    public string? Source { get; set; }

    /// <summary>The wait after a good round before the next one.</summary>
    public TimeSpan RoundInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The first wait after a failed round; later waits grow from it, with jitter.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between retries (ADR 0008: 30 s).</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The fake source's starting rows. A relative path is relative to the gateway's own directory.</summary>
    public string? FakeSeedFile { get; set; }

    /// <summary>Where and how the Oracle source reads the MES. Never set in the repository.</summary>
    public OracleSettings Oracle { get; } = new();

    /// <summary>Every address the gateway is configured to listen on, keyed by where it came from.</summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

    /// <summary>Settings that could not be read; the validator reports them.</summary>
    public IList<string> ReadErrors { get; } = [];

    public string? FakeSeedPath =>
        string.IsNullOrWhiteSpace(FakeSeedFile) ? null : Path.GetFullPath(FakeSeedFile, AppContext.BaseDirectory);

    public bool UsesFakeSource => string.Equals(Source, FakeSource, StringComparison.OrdinalIgnoreCase);

    public bool UsesOracleSource => string.Equals(Source, OracleSource, StringComparison.OrdinalIgnoreCase);

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
        Oracle.DataSource = section["Oracle:DataSource"];
        Oracle.User = section["Oracle:User"];
        Oracle.Password = section["Oracle:Password"];
        Oracle.ConnectTimeout = Duration(section, "Oracle:ConnectTimeout") ?? Oracle.ConnectTimeout;
        Oracle.QueryTimeout = Duration(section, "Oracle:QueryTimeout") ?? Oracle.QueryTimeout;

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

/// <summary>
/// The Oracle source's settings (<c>Simple:Mes:Oracle</c>). Where the MES is
/// and the account to read it with come from outside the repository, for the
/// run a person chose them for: a settings file named by
/// <c>Simple:SettingsFile</c>, or environment variables.
/// </summary>
public sealed class OracleSettings
{
    /// <summary>What a read may take beyond its connect and query timeouts: fetching the rows, closing the connection.</summary>
    public static readonly TimeSpan ReadTimeoutMargin = TimeSpan.FromSeconds(5);

    /// <summary>The database as Oracle's driver takes it: <c>host:port/service</c> or a descriptor; no user or password in it.</summary>
    public string? DataSource { get; set; }

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>How long opening a connection may take.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the query may run: the previous line's 30 s.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a whole read may take before it is abandoned and its round
    /// fails. With the defaults and the 30 s retry wait it stays below the
    /// server's 90 s stall threshold (ADR 0027), so a hanging MES shows as
    /// failed rounds, not as a gateway that stopped.
    /// </summary>
    public TimeSpan ReadTimeout => ConnectTimeout + QueryTimeout + ReadTimeoutMargin;
}
