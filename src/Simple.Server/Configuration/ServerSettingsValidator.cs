using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Simple.Server.Configuration;

/// <summary>
/// Refuses to start the server on invalid settings, naming the setting at
/// fault. Runs at host start (ValidateOnStart), before the database is
/// touched or a port is opened.
/// </summary>
public sealed partial class ServerSettingsValidator : IValidateOptions<ServerSettings>
{
    public ValidateOptionsResult Validate(string? name, ServerSettings options)
    {
        var failures = Check(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> Check(ServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var failures = new List<string>();

        if (string.IsNullOrEmpty(settings.Instance) || !InstanceName().IsMatch(settings.Instance))
        {
            failures.Add(
                $"{ServerSettings.InstanceKey} '{settings.Instance}' is not a valid instance name: use 1-50 letters, digits, '-' or '_'. It becomes part of the database name Simple_<instance>.");
        }

        CheckConnectionString(settings.ConnectionString, failures);

        // The API on its own listens on loopback only: other machines reach the
        // console through its HTTPS address, with its certificate (ADR 0046).
        foreach (var (key, address) in settings.ListenAddresses)
        {
            if (key is "http_ports" or "https_ports")
            {
                failures.Add(
                    $"{key} '{address}' listens on every network interface. Set urls to a loopback address instead, for example http://127.0.0.1:5180.");
            }
            else if (!IsLoopbackListenAddress(address))
            {
                failures.Add(
                    $"{key} '{address}' is not a loopback address. The server API listens on loopback only (127.0.0.1, [::1] or localhost); other computers reach the console at {ServerSettings.ConsoleUrlKey}, over HTTPS.");
            }
            else if (key == "urls" && !address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{key} '{address}' is not http://. The server API is plain HTTP on loopback; the console's address ({ServerSettings.ConsoleUrlKey}) and the onboard terminal endpoint use TLS.");
            }
        }

        CheckOnboardTerminals(settings, failures);
        CheckOneInterface(ServerSettings.ConsoleUrlKey, settings.ConsoleUrl, "https", allowLocalhost: false, "https://172.19.205.222:5443", failures);
        CheckOneInterface(ServerSettings.OverviewUrlKey, settings.OverviewUrl, "http", allowLocalhost: true, "http://172.19.205.222:5080", failures);
        if (InstanceName().IsMatch(settings.Instance ?? string.Empty) && !KeyName().IsMatch(settings.ConsoleKeyName))
        {
            failures.Add(
                $"{ServerSettings.ConsoleKeyNameKey} '{settings.ConsoleKeyName}' is not a valid key name: use 1-100 letters, digits, '.', '-' or '_'.");
        }

        return failures;
    }

    /// <summary>
    /// An address other computers reach (ADR 0046): the given scheme, on the
    /// IP address of one network interface, never a wildcard, which would
    /// also open it on every other network the server sits on. The console's
    /// names an IP, which goes into its certificate.
    /// </summary>
    private static void CheckOneInterface(string key, string? url, string scheme, bool allowLocalhost, string example, List<string> failures)
    {
        if (url is null)
        {
            return;
        }

        BindingAddress? address = null;
        try
        {
            address = BindingAddress.Parse(url);
        }
        catch (FormatException)
        {
            // Reported below.
        }

        var host = address?.Host.Trim('[', ']');
        if (address is null || !string.Equals(address.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{key} '{url}' is not an {scheme}:// address; give for example {example}.");
        }
        else if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (!(allowLocalhost && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{key} '{url}' does not name an IP address. Give the address of the one network interface it listens on, for example {example}.");
            }
        }
        else if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any))
        {
            failures.Add($"{key} '{url}' listens on every network interface. Give the address of the one interface it listens on.");
        }
    }

    /// <summary>
    /// The onboard terminal endpoint, unlike the API, may listen where other
    /// machines reach it (ADR 0025): terminals connect from the vehicles, and
    /// nothing but the terminal protocol is served there, over TLS with the
    /// pinned certificate. It names one interface: a wildcard would also open
    /// it on every other network the server sits on.
    /// </summary>
    private static void CheckOnboardTerminals(ServerSettings settings, List<string> failures)
    {
        const string key = ServerSettings.OnboardTerminalsUrlKey;
        if (settings.OnboardTerminalsUrl is { } url)
        {
            BindingAddress? address = null;
            try
            {
                address = BindingAddress.Parse(url);
            }
            catch (FormatException)
            {
                // Reported below.
            }

            if (address is null || !string.Equals(address.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{key} '{url}' is not an https:// address. Terminals connect over TLS only; give for example https://127.0.0.1:5181.");
            }
            else if (!string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !System.Net.IPAddress.TryParse(address.Host.Trim('[', ']'), out _))
            {
                failures.Add(
                    $"{key} '{url}' does not name an IP address. Give the address of the one network interface terminals connect to, for example https://172.19.205.222:5181.");
            }
            else if (System.Net.IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip)
                && (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)))
            {
                failures.Add(
                    $"{key} '{url}' listens on every network interface. Give the address of the one interface terminals connect to.");
            }
        }

        // Named after the instance unless configured: an invalid instance
        // name is reported once, above, not again as a key name.
        if (InstanceName().IsMatch(settings.Instance ?? string.Empty) && !KeyName().IsMatch(settings.ServerKeyName))
        {
            failures.Add(
                $"{ServerSettings.ServerKeyNameKey} '{settings.ServerKeyName}' is not a valid key name: use 1-100 letters, digits, '.', '-' or '_'.");
        }

        if (settings.ServerKeyStore is { Length: > 0 } store
            && !string.Equals(store, "User", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(store, "Machine", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{ServerSettings.ServerKeyStoreKey} '{store}' is neither User nor Machine.");
        }
    }

    public static bool IsLoopbackHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host = host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    private static bool IsLoopbackListenAddress(string address)
    {
        try
        {
            return IsLoopbackHost(BindingAddress.Parse(address).Host);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void CheckConnectionString(string? connectionString, List<string> failures)
    {
        const string key = ServerSettings.ConnectionStringKey;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add($"{key} is missing. Give the SQL Server instance without a database, for example Server=(localdb)\\MSSQLLocalDB;Integrated Security=true.");
            return;
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException e)
        {
            failures.Add($"{key} cannot be parsed: {e.Message}");
            return;
        }

        if (string.IsNullOrEmpty(builder.DataSource))
        {
            failures.Add($"{key} names no server (Server=...).");
        }

        if (!string.IsNullOrEmpty(builder.InitialCatalog) || !string.IsNullOrEmpty(builder.AttachDBFilename))
        {
            failures.Add(
                $"{key} names a database. Leave it out: the database is Simple_<instance>, from {ServerSettings.InstanceKey}.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,50}$")]
    private static partial Regex InstanceName();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex KeyName();
}
