using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Simple.Cli;

/// <summary>
/// Writes a local onboard terminal's own settings file, as an installer would
/// on a vehicle PC: the vehicle it is on, where its server is and which
/// certificate pin to trust, both read from the running server's status, and
/// its IO module: the io-simulator with the same number.
/// </summary>
public static class OnboardTerminalSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { WriteIndented = true };

    /// <summary>The vehicle a local terminal reports: vehicle-1, vehicle-2…</summary>
    public static string VehicleId(int index) => $"vehicle-{index.ToString(CultureInfo.InvariantCulture)}";

    public static async Task WriteAsync(ComponentContext context, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(http);
        var server = context.PortsOf[Components.Server.Name][Components.Server.ApiPort ?? 0];
        var status = await http.GetFromJsonAsync<JsonElement>(
            new Uri($"http://127.0.0.1:{server.ToString(CultureInfo.InvariantCulture)}/api/v1/status"), cancellationToken);
        if (!status.TryGetProperty("onboardTerminalEndpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The server reports no onboard terminal endpoint.");
        }

        Directory.CreateDirectory(context.Directory);
        var settings = new
        {
            VehicleId = VehicleId(context.Index),
            Server = endpoint.GetProperty("url").GetString(),
            ServerCertificatePin = endpoint.GetProperty("certificatePin").GetString(),
            IoModule = $"127.0.0.1:{IoSimulatorPorts(context)[Components.IoSimulatorModbusPort].ToString(CultureInfo.InvariantCulture)}",
        };
        await File.WriteAllTextAsync(
            Path.Combine(context.Directory, "terminal.json"), JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }

    /// <summary>The ports of the io-simulator paired with this terminal: as many simulators run as terminals, numbered alike.</summary>
    private static IReadOnlyList<int> IoSimulatorPorts(ComponentContext context)
    {
        var name = Components.IoSimulator.Name;
        var count = context.PortsOf.Keys.Count(k => k == name || k.StartsWith(name + "-", StringComparison.Ordinal));
        return context.PortsOf[Components.ProcessNames(Components.IoSimulator, count).ElementAt(context.Index - 1)];
    }
}
