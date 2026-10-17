using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Simple.IoSimulator.Core;

namespace Simple.IoSimulator.Headless;

/// <summary>The IO simulator's settings, read from <c>IoSimulator</c>, and where its control API listens.</summary>
public sealed class IoSimulatorSettings
{
    public const string Section = "IoSimulator";

    /// <summary>Where the module's Modbus TCP face listens: a loopback address and port; port 0 picks a free one.</summary>
    public string Modbus { get; set; } = "127.0.0.1:1502";

    public IoSimulatorTiming Timing { get; set; } = new();

    /// <summary>The slots (1-8) holding cargo when the simulator starts: --IoSimulator:Cargo:0=1 --IoSimulator:Cargo:1=3. None by default.</summary>
    public IList<int> Cargo { get; } = [];

    /// <summary>Every address the control API is configured to listen on, keyed by where it came from.</summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

    public CabinetTiming CabinetTiming =>
        new(Timing.PulseWidth, Timing.UnlockFeedbackDelay, Timing.LockFeedbackDelay, Timing.LightCurtainDelay);

    public void ReadFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.GetSection(Section).Bind(this);
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

/// <summary>The cabinet's mechanics (<see cref="CabinetTiming"/>); the defaults are the field-confirmed ones.</summary>
public sealed class IoSimulatorTiming
{
    public TimeSpan PulseWidth { get; set; } = CabinetTiming.Default.PulseWidth;

    public TimeSpan UnlockFeedbackDelay { get; set; } = CabinetTiming.Default.UnlockFeedbackDelay;

    public TimeSpan LockFeedbackDelay { get; set; } = CabinetTiming.Default.LockFeedbackDelay;

    public TimeSpan LightCurtainDelay { get; set; } = CabinetTiming.Default.LightCurtainDelay;
}

/// <summary>Refuses to start the simulator on settings that would make it reachable from another machine.</summary>
public sealed class IoSimulatorSettingsValidator : IValidateOptions<IoSimulatorSettings>
{
    public ValidateOptionsResult Validate(string? name, IoSimulatorSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        const string section = IoSimulatorSettings.Section;
        if (!IPEndPoint.TryParse(options.Modbus, out var modbus) || !IPAddress.IsLoopback(modbus.Address))
        {
            failures.Add($"{section}:Modbus '{options.Modbus}' is not a loopback address and port. The simulated IO module listens on loopback only, for example 127.0.0.1:1502.");
        }

        var timing = options.Timing;
        foreach (var (key, value) in new[]
                 {
                     ("PulseWidth", timing.PulseWidth),
                     ("UnlockFeedbackDelay", timing.UnlockFeedbackDelay),
                     ("LockFeedbackDelay", timing.LockFeedbackDelay),
                     ("LightCurtainDelay", timing.LightCurtainDelay),
                 })
        {
            if (value < TimeSpan.Zero)
            {
                failures.Add($"{section}:Timing:{key} must not be negative.");
            }
        }

        foreach (var slot in options.Cargo.Where(n => n is < 1 or > Cabinet.SlotCount).Distinct())
        {
            failures.Add($"{section}:Cargo lists slot {slot}; the cabinet's slots are 1 to {Cabinet.SlotCount}.");
        }

        foreach (var (key, address) in options.ListenAddresses)
        {
            if (key is "http_ports" or "https_ports")
            {
                failures.Add($"{key} '{address}' listens on every network interface. Set urls to a loopback address instead, for example http://127.0.0.1:5210.");
            }
            else if (!IsLoopbackListenAddress(address))
            {
                failures.Add($"{key} '{address}' is not a loopback address. The IO simulator's control API listens on loopback only (127.0.0.1, [::1] or localhost).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
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
