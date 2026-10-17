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
    private string? serverKeyName;

    public const string InstanceKey = "Simple:Instance";
    public const string ConnectionStringKey = "ConnectionStrings:Simple";
    public const string OnboardTerminalsUrlKey = "Simple:OnboardTerminals:Url";
    public const string ServerKeyNameKey = "Simple:OnboardTerminals:ServerKeyName";
    public const string ServerKeyStoreKey = "Simple:OnboardTerminals:ServerKeyStore";
    public const string ConsoleUrlKey = "Simple:Console:Url";
    public const string ConsoleKeyNameKey = "Simple:Console:KeyName";
    public const string OverviewUrlKey = "Simple:Overview:Url";

    private string? consoleKeyName;

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

    /// <summary>
    /// Every address the server API is configured to listen on, keyed by where
    /// it came from. The onboard terminal endpoint is not among them.
    /// </summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

    /// <summary>
    /// The one address onboard terminals connect to (gRPC over TLS, ADR 0025),
    /// for example <c>https://127.0.0.1:5181</c>; null for none. Unlike the
    /// API it may be an address other machines reach.
    /// </summary>
    public string? OnboardTerminalsUrl { get; set; }

    /// <summary>The name of the persisted key behind the certificate terminals pin (see OnboardTerminals.ServerCertificate).</summary>
    public string ServerKeyName
    {
        get => serverKeyName ?? $"Simple-Server-{Instance}";
        set => serverKeyName = value;
    }

    /// <summary>
    /// The console's HTTPS address (ADR 0046), for example
    /// <c>https://172.19.205.222:5443</c>; null for none. The address of one
    /// network interface, which goes into the certificate the server makes.
    /// </summary>
    public string? ConsoleUrl { get; set; }

    /// <summary>The name of the persisted key behind the console's certificate (see Listeners.ConsoleCertificate).</summary>
    public string ConsoleKeyName
    {
        get => consoleKeyName ?? $"Simple-Console-{Instance}";
        set => consoleKeyName = value;
    }

    /// <summary>
    /// The vehicle overview's plain HTTP address (ADR 0046), for example
    /// <c>http://172.19.205.222:5080</c>; null for none. It serves the
    /// overview and nothing else, and takes no login.
    /// </summary>
    public string? OverviewUrl { get; set; }

    /// <summary>
    /// <c>User</c> (default): the key store of the account the server runs as; <c>Machine</c>: the machine's.
    /// It holds the terminal certificate's key and the console certificate's key alike.
    /// </summary>
    public string? ServerKeyStore { get; set; }

    public bool ServerKeyIsMachineKey => string.Equals(ServerKeyStore, "Machine", StringComparison.OrdinalIgnoreCase);

    public string DatabaseName => $"Simple_{Instance}";

    public string DatabaseConnectionString =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = DatabaseName }.ConnectionString;

    public void ReadFrom(IConfiguration configuration)
    {
        Instance = configuration[InstanceKey];
        ConnectionString = configuration[ConnectionStringKey];
        OnboardTerminalsUrl = configuration[OnboardTerminalsUrlKey] is { Length: > 0 } url ? url : null;
        serverKeyName = configuration[ServerKeyNameKey] is { Length: > 0 } keyName ? keyName : null;
        ServerKeyStore = configuration[ServerKeyStoreKey];
        ConsoleUrl = configuration[ConsoleUrlKey] is { Length: > 0 } console ? console : null;
        consoleKeyName = configuration[ConsoleKeyNameKey] is { Length: > 0 } consoleKey ? consoleKey : null;
        OverviewUrl = configuration[OverviewUrlKey] is { Length: > 0 } overview ? overview : null;

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
