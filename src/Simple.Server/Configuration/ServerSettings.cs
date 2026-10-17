using Microsoft.Data.SqlClient;

namespace Simple.Server.Configuration;

/// <summary>
/// The server's own settings, read from configuration. The repository's
/// appsettings.json carries development defaults only (loopback, LocalDB);
/// anything real comes from a settings file outside the repository
/// (<c>Simple:SettingsFile</c>), environment variables or the command line.
/// </summary>
public sealed class ServerSettings
{
    public const string InstanceKey = "Simple:Instance";
    public const string ConnectionStringKey = "ConnectionStrings:Simple";

    /// <summary>
    /// Names this server instance. Two instances on one machine (for example
    /// two worktrees) differ in this name and so use different databases.
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>
    /// Connection string to the SQL Server instance, without a database: the
    /// database name is derived from <see cref="Instance"/>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Every address the server is configured to listen on, keyed by where it came from.</summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

    public string DatabaseName => $"Simple_{Instance}";

    public string DatabaseConnectionString =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = DatabaseName }.ConnectionString;

    public void ReadFrom(IConfiguration configuration)
    {
        Instance = configuration[InstanceKey];
        ConnectionString = configuration[ConnectionStringKey];

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
