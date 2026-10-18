using System.Globalization;
using System.Text.Json;

namespace Simple.Cli;

/// <summary>
/// Writes a local onboard terminal's own settings file, as an installer would
/// on a vehicle PC: the vehicle it is on, where its server is and which
/// certificate pin to trust, both read from the running server's status, its
/// IO module (the io-simulator with the same number), and its own key
/// (<see cref="Components.TerminalKeyName"/>). Once the terminal is up, the
/// command line confirms it, as an engineer at the vehicle would
/// (<see cref="ConfirmAsync(ComponentContext, HttpClient, CancellationToken)"/>).
/// </summary>
public static class OnboardTerminalSettings
{
    /// <summary>How long a terminal may take to show up on the server's pending list, or as its vehicle's terminal.</summary>
    public static readonly TimeSpan ConfirmWait = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { WriteIndented = true };

    /// <summary>
    /// The vehicle local terminal <paramref name="index"/> is on: the site's
    /// vehicle with that number, in the order of its site file, so that the
    /// server, which confirms only vehicles of its imported configuration,
    /// can confirm it (ADR 0029).
    /// </summary>
    /// <exception cref="InvalidOperationException">The site has fewer vehicles.</exception>
    public static string VehicleId(string siteFile, int index)
    {
        var vehicles = Scenarios.Site.Read(siteFile).Vehicles;
        return index >= 1 && index <= vehicles.Count
            ? vehicles[index - 1].Id
            : throw new InvalidOperationException($"{siteFile} has {vehicles.Count.ToString(CultureInfo.InvariantCulture)} vehicles, so there is no terminal {index.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static string VehicleId(ComponentContext context) =>
        VehicleId(context.SiteFile ?? throw new InvalidOperationException("up names no site file."), context.Index);

    public static async Task WriteAsync(ComponentContext context, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(http);
        await using var api = await LogInAsync(context, http, cancellationToken);
        var status = await api.GetJsonAsync("/api/v1/status", cancellationToken);
        if (!status.TryGetProperty("onboardTerminalEndpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The server reports no onboard terminal endpoint.");
        }

        Directory.CreateDirectory(context.Directory);
        var settings = new
        {
            VehicleId = VehicleId(context),
            Server = endpoint.GetProperty("url").GetString(),
            ServerCertificatePin = endpoint.GetProperty("certificatePin").GetString(),
            IoModule = $"127.0.0.1:{IoSimulatorPorts(context)[Components.IoSimulatorModbusPort].ToString(CultureInfo.InvariantCulture)}",
            // Its own key, in the store the server's is in (the machine's over
            // SSH, where the user's cannot be used), never in the TPM: a local
            // system leaves nothing there for down --reset to miss.
            KeyName = Components.TerminalKeyName(context.Instance, context.Index),
            KeyStore = context.KeyStore == ServerKeyStore.Machine ? "machine" : "user",
            KeyProvider = "software",
        };
        await File.WriteAllTextAsync(
            Path.Combine(context.Directory, SettingsFile), JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }

    /// <summary>The settings file in a terminal's process directory.</summary>
    public const string SettingsFile = "terminal.json";

    /// <summary>Unless up was told not to, confirms a local terminal once it is up (see <see cref="ConfirmAsync(HttpClient, Uri, Uri, string, string, TimeSpan, CancellationToken)"/>).</summary>
    public static async Task ConfirmAsync(ComponentContext context, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Options.ConfirmTerminals)
        {
            return;
        }

        var server = context.PortsOf[Components.Server.Name][Components.Server.ApiPort ?? 0];
        await ConfirmAsync(
            http,
            new Uri($"http://127.0.0.1:{server.ToString(CultureInfo.InvariantCulture)}"),
            context.Url(),
            context.AdminPassword ?? throw new InvalidOperationException("The instance has no administrator password."),
            VehicleId(context),
            ConfirmWait,
            cancellationToken);
    }

    /// <summary>
    /// Does what an engineer at the vehicle does (ADR 0025, 0029): finds the
    /// terminal claiming <paramref name="vehicleId"/> on the server's pending
    /// list, checks that the terminal's screen (its test input's status) shows
    /// the same pairing code, and confirms it as the administrator. Returns
    /// what it did; done at once when the vehicle's terminal is in use
    /// already (its key confirmed by an earlier up). Neither what it returns
    /// nor what it throws carries the code, which is treated as a credential
    /// (ADR 0034): both end up in evidence and logs.
    /// </summary>
    /// <exception cref="InvalidOperationException">It did not get there within <paramref name="wait"/>; the message says how far it got.</exception>
    public static async Task<string> ConfirmAsync(
        HttpClient http, Uri server, Uri terminalTestInput, string adminPassword, string vehicleId, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        await using var api = await ServerApi.LogInAsync(http, server, adminPassword, cancellationToken)
            ?? throw new InvalidOperationException("The server refused the instance's administrator password.");
        var deadline = DateTime.UtcNow + wait;
        var seen = "nothing on the server's pending list or as the vehicle's terminal";
        while (true)
        {
            var status = await api.GetJsonAsync("/api/v1/status", cancellationToken);
            if (status.GetProperty("onboardTerminals").EnumerateArray()
                .Any(t => t.GetProperty("vehicleId").GetString() == vehicleId && t.GetProperty("connected").GetBoolean()))
            {
                return $"{vehicleId}'s terminal is in use";
            }

            var screen = await ScreenTextAsync(http, terminalTestInput, cancellationToken);
            foreach (var waiting in status.GetProperty("pendingOnboardTerminals").EnumerateArray()
                .Where(p => p.GetProperty("vehicleId").GetString() == vehicleId && p.GetProperty("connected").GetBoolean()))
            {
                var code = waiting.GetProperty("pairingCode").GetString()!;
                if (screen?.Contains(code, StringComparison.Ordinal) != true)
                {
                    seen = screen is null
                        ? "a terminal waiting on the server's pending list, but no screen text from the terminal's test input"
                        : "a terminal waiting on the server's pending list, but the terminal's screen does not show its pairing code";
                    continue;
                }

                using var confirmed = await api.PostJsonAsync(
                    $"/api/v1/onboard-terminals/{Uri.EscapeDataString(vehicleId)}/confirm", new { PairingCode = code }, cancellationToken);
                if (confirmed.IsSuccessStatusCode)
                {
                    return $"confirmed {vehicleId}'s terminal: its screen and the server's pending list showed the same pairing code";
                }

                seen = $"confirming it answered {(int)confirmed.StatusCode}: {await confirmed.Content.ReadAsStringAsync(cancellationToken)}";
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"did not get confirmed as {vehicleId} within {wait.TotalSeconds} s: {seen}");
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>The keys the local terminals of an instance were given, from their settings files: what down --reset deletes.</summary>
    public static IReadOnlyList<(string Name, ServerKeyStore Store)> KeysOf(string processesDirectory)
    {
        if (!Directory.Exists(processesDirectory))
        {
            return [];
        }

        var keys = new List<(string, ServerKeyStore)>();
        foreach (var file in Directory.EnumerateFiles(processesDirectory, SettingsFile, SearchOption.AllDirectories))
        {
            try
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(file));
                if (settings.RootElement.TryGetProperty("keyName", out var name) && name.GetString() is { Length: > 0 } keyName)
                {
                    var machine = settings.RootElement.TryGetProperty("keyStore", out var store) && store.GetString() == "machine";
                    keys.Add((keyName, machine ? ServerKeyStore.Machine : ServerKeyStore.User));
                }
            }
            catch (JsonException)
            {
                // Not one this command line wrote.
            }
        }

        return keys;
    }

    private static async Task<ServerApi> LogInAsync(ComponentContext context, HttpClient http, CancellationToken cancellationToken)
    {
        var server = context.PortsOf[Components.Server.Name][Components.Server.ApiPort ?? 0];
        return await ServerApi.LogInAsync(
                http,
                new Uri($"http://127.0.0.1:{server.ToString(CultureInfo.InvariantCulture)}"),
                context.AdminPassword ?? throw new InvalidOperationException("The instance has no administrator password."),
                cancellationToken)
            ?? throw new InvalidOperationException("The server refused the instance's administrator password.");
    }

    private static async Task<string?> ScreenTextAsync(HttpClient http, Uri terminalTestInput, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(terminalTestInput, "/test/status"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return status.RootElement.TryGetProperty("screenText", out var text) ? text.GetString() : null;
    }

    /// <summary>The ports of the io-simulator paired with this terminal: as many simulators run as terminals, numbered alike.</summary>
    private static IReadOnlyList<int> IoSimulatorPorts(ComponentContext context)
    {
        var name = Components.IoSimulator.Name;
        var count = context.PortsOf.Keys.Count(k => k == name || k.StartsWith(name + "-", StringComparison.Ordinal));
        return context.PortsOf[Components.ProcessNames(Components.IoSimulator, count).ElementAt(context.Index - 1)];
    }
}
