using System.Globalization;
using System.Text.Json;

namespace Simple.Cli;

/// <summary>
/// Writes a local onboard terminal's own settings file, as an installer would
/// on a vehicle PC: the vehicle it is on, and where its server is and which
/// certificate pin to trust, both read from the running server's status.
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
        await using var api = await ServerApi.LogInAsync(
                http,
                new Uri($"http://127.0.0.1:{server.ToString(CultureInfo.InvariantCulture)}"),
                context.AdminPassword ?? throw new InvalidOperationException("The instance has no administrator password."),
                cancellationToken)
            ?? throw new InvalidOperationException("The server refused the instance's administrator password.");
        var status = await api.GetJsonAsync("/api/v1/status", cancellationToken);
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
        };
        await File.WriteAllTextAsync(
            Path.Combine(context.Directory, "terminal.json"), JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }
}
