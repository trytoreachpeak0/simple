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

        // The API has no authentication yet, so it must not be reachable from
        // another machine.
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
                    $"{key} '{address}' is not a loopback address. The server API has no authentication yet and listens on loopback only (127.0.0.1, [::1] or localhost).");
            }
        }

        return failures;
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
}
