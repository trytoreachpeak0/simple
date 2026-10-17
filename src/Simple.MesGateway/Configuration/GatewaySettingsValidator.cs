using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Simple.MesGateway.Fake;

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

        if (!settings.UsesFakeSource)
        {
            failures.Add(
                $"{section}:Source '{settings.Source}' is not a known MES source. The only source so far is {GatewaySettings.FakeSource}; reading the real MES comes with its own ticket.");
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
