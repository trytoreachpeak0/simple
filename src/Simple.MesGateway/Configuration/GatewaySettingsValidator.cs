using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Simple.MesGateway.Fake;
using Simple.MesGateway.Oracle;

namespace Simple.MesGateway.Configuration;

/// <summary>
/// Refuses to start the gateway on invalid settings, naming the setting at
/// fault. Runs at host start (ValidateOnStart), before a port is opened or
/// the MES is read.
/// </summary>
public sealed class GatewaySettingsValidator : IValidateOptions<GatewaySettings>
{
    public ValidateOptionsResult Validate(string? name, GatewaySettings options)
    {
        var failures = Check(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> Check(GatewaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var failures = new List<string>(settings.ReadErrors);
        const string section = GatewaySettings.Section;

        if (settings.UsesOracleSource)
        {
            CheckOracle(settings.Oracle, failures);
        }
        else if (!settings.UsesFakeSource)
        {
            failures.Add(
                $"{section}:Source '{settings.Source}' is not a known MES source. Use {GatewaySettings.FakeSource} (the default) or {GatewaySettings.OracleSource} (the customer's MES, set outside the repository).");
        }
        else if (settings.FakeSeedPath is not { } seed)
        {
            failures.Add($"{section}:Fake:SeedFile is missing. Name the JSON file with the fake source's starting rows.");
        }
        else
        {
            try
            {
                FakeMesSource.ReadSeed(seed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                failures.Add($"{section}:Fake:SeedFile '{seed}' cannot be read: {e.Message}");
            }
        }

        foreach (var (key, value) in new[]
                 {
                     ("RoundInterval", settings.RoundInterval),
                     ("RetryDelay", settings.RetryDelay),
                     ("RetryMaxDelay", settings.RetryMaxDelay),
                 })
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{section}:{key} must be longer than zero.");
            }
        }

        if (settings.RetryMaxDelay < settings.RetryDelay)
        {
            failures.Add($"{section}:RetryMaxDelay must not be shorter than {section}:RetryDelay.");
        }

        // The gateway's API has no authentication, so, like the server's, it
        // must not be reachable from another machine.
        foreach (var (key, address) in settings.ListenAddresses)
        {
            if (key is "http_ports" or "https_ports")
            {
                failures.Add(
                    $"{key} '{address}' listens on every network interface. Set urls to a loopback address instead, for example http://127.0.0.1:5190.");
            }
            else if (!IsLoopbackListenAddress(address))
            {
                failures.Add(
                    $"{key} '{address}' is not a loopback address. The gateway API has no authentication yet and listens on loopback only (127.0.0.1, [::1] or localhost).");
            }
        }

        return failures;
    }

    /// <summary>The Oracle source's settings. No message here repeats a value that could hold the password.</summary>
    private static void CheckOracle(OracleSettings oracle, List<string> failures)
    {
        const string section = GatewaySettings.Section + ":Oracle";
        foreach (var (key, value) in new[] { ("DataSource", oracle.DataSource), ("User", oracle.User), ("Password", oracle.Password) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add(
                    $"{section}:{key} is missing. Set it outside the repository: in the settings file named by Simple:SettingsFile, or as the environment variable Simple__Mes__Oracle__{key}.");
            }
        }

        // user/password@database is how Oracle's own tools take a login; here the
        // login has settings of its own.
        if (oracle.DataSource?.Contains('@', StringComparison.Ordinal) == true)
        {
            failures.Add(
                $"{section}:DataSource holds an @. Give only the database there (host:port/service), and the login in {section}:User and {section}:Password.");
        }

        foreach (var (key, value) in new[] { ("ConnectTimeout", oracle.ConnectTimeout), ("QueryTimeout", oracle.QueryTimeout) })
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{section}:{key} must be longer than zero.");
            }
        }

        try
        {
            MesTaskUnionQuery.Load();
        }
        catch (InvalidDataException e)
        {
            failures.Add(e.Message);
        }
    }

    private static bool IsLoopbackListenAddress(string address)
    {
        try
        {
            var host = BindingAddress.Parse(address).Host.Trim('[', ']');
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
