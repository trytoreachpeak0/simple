using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Simple.FakeRiot;

/// <summary>
/// The fake fleet manager's settings, read from <c>FakeRiot</c>. It accepts
/// <see cref="CallApiKey"/> as the web page's call key would be accepted
/// (BC-AUTH-002) and, when both are set, <see cref="Username"/> and
/// <see cref="Password"/> at the admin login (BC-AUTH-001). These are values
/// of the fake only; they open nothing real.
/// </summary>
public sealed class FakeRiotSettings
{
    public const string Section = "FakeRiot";

    public string? CallApiKey { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The maps, stations, edges and vehicles the fake starts with, relative to the program's directory.</summary>
    public string SeedFile { get; set; } = "seed.json";

    public FakeRiotTiming Timing { get; set; } = new();

    /// <summary>At this battery level or below RIoT gives the vehicle no order: 10 % (the product owner, BC-VEH-007).</summary>
    public int NoOrdersAtOrBelowBattery { get; set; } = 10;

    /// <summary>
    /// How long every read (a GET under /api) waits before it is answered,
    /// in real time; not compressed by <see cref="FakeRiotTiming.Speedup"/>.
    /// Real RIoT's reads took about 168 ms at p95 (research/riot-review.md R39).
    /// Zero by default; the control API changes it while the fake runs.
    /// </summary>
    public TimeSpan ReadDelay { get; set; }

    /// <summary>Every address the fake is configured to listen on, keyed by where it came from.</summary>
    public IList<KeyValuePair<string, string>> ListenAddresses { get; } = [];

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

/// <summary>
/// How fast the fake's vehicles and orders move on. The defaults are near
/// what real RIoT showed; tests shorten them, or compress them all at once
/// with <see cref="Speedup"/>.
/// </summary>
public sealed class FakeRiotTiming
{
    /// <summary>
    /// How many times faster than real RIoT the fake's world runs: every
    /// duration below is divided by it, travel included. 1 is real time;
    /// 100 runs a 20-second trip in a fifth of a second. <see cref="Tick"/>
    /// is not divided: it is how often the fake looks, in real time.
    /// Timestamps on the wire (createTime, updateTime) stay real.
    /// </summary>
    public double Speedup { get; set; } = 1;

    /// <summary>How often the fake moves its world on.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a queued order waits before it may start on a free vehicle.</summary>
    public TimeSpan QueueToStart { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Travel time is the route cost over this speed, and never below <see cref="MinimumTravel"/>.</summary>
    public double SpeedMillimetresPerSecond { get; set; } = 1000;

    public TimeSpan MinimumTravel { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// From the vehicle reaching its last station to the order turning
    /// SUCCESS: real RIoT showed the arrival about a second earlier (BC-STATE-003).
    /// </summary>
    public TimeSpan ArrivalToSuccess { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>From triggerEmergency to <c>CAN_RECOVER</c>: about a second on real RIoT.</summary>
    public TimeSpan EmergencyLock { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>From cancelEmergency to <c>OK</c>: 4 to 5 seconds on real RIoT (BC-VEH-005).</summary>
    public TimeSpan EmergencyRelease { get; set; } = TimeSpan.FromSeconds(4.5);

    /// <summary>How long some fault codes stay listed after <c>OK</c>: real RIoT cleared them 3 to 4 seconds later (BC-ORDER-020 item 7).</summary>
    public TimeSpan FaultCodeResidue { get; set; } = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// From CONTINUE_FROM_HELD to the vehicle moving: the order is executing at
    /// once but real RIoT's vehicle started about 16 seconds later (BC-ORDER-020 item 3).
    /// </summary>
    public TimeSpan ContinueStartDelay { get; set; } = TimeSpan.FromSeconds(16);
}

/// <summary>Refuses to start the fake on settings that would make it reachable from another machine or leave it without a key.</summary>
public sealed class FakeRiotSettingsValidator : IValidateOptions<FakeRiotSettings>
{
    public ValidateOptionsResult Validate(string? name, FakeRiotSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        const string section = FakeRiotSettings.Section;
        if (string.IsNullOrWhiteSpace(options.CallApiKey))
        {
            failures.Add($"{section}:CallApiKey is empty. The fake accepts only the key it is given, as real RIoT does.");
        }

        if (string.IsNullOrWhiteSpace(options.Username) != string.IsNullOrWhiteSpace(options.Password))
        {
            failures.Add($"{section}:Username and {section}:Password are set together or not at all.");
        }

        var timing = options.Timing;
        foreach (var (key, value) in new[]
                 {
                     ("Tick", timing.Tick),
                     ("MinimumTravel", timing.MinimumTravel),
                 })
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{section}:Timing:{key} must be longer than zero.");
            }
        }

        foreach (var (key, value) in new[]
                 {
                     ("QueueToStart", timing.QueueToStart),
                     ("ArrivalToSuccess", timing.ArrivalToSuccess),
                     ("EmergencyLock", timing.EmergencyLock),
                     ("EmergencyRelease", timing.EmergencyRelease),
                     ("FaultCodeResidue", timing.FaultCodeResidue),
                     ("ContinueStartDelay", timing.ContinueStartDelay),
                 })
        {
            if (value < TimeSpan.Zero)
            {
                failures.Add($"{section}:Timing:{key} must not be negative.");
            }
        }

        if (options.ReadDelay < TimeSpan.Zero)
        {
            failures.Add($"{section}:ReadDelay must not be negative.");
        }

        if (timing.Speedup is <= 0 or double.NaN or double.PositiveInfinity)
        {
            failures.Add($"{section}:Timing:Speedup must be above zero.");
        }

        if (timing.SpeedMillimetresPerSecond <= 0)
        {
            failures.Add($"{section}:Timing:SpeedMillimetresPerSecond must be above zero.");
        }

        // The fake stands in for a system on the plant network, but it is a
        // test tool: nothing on another machine is meant to reach it.
        foreach (var (key, address) in options.ListenAddresses)
        {
            if (key is "http_ports" or "https_ports")
            {
                failures.Add($"{key} '{address}' listens on every network interface. Set urls to a loopback address instead, for example http://127.0.0.1:5200.");
            }
            else if (!IsLoopbackListenAddress(address))
            {
                failures.Add($"{key} '{address}' is not a loopback address. The fake fleet manager listens on loopback only (127.0.0.1, [::1] or localhost).");
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
