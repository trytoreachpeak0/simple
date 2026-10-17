using Microsoft.Extensions.Configuration;

namespace Simple.IoSimulator.Headless.Tests;

/// <summary>Both of the simulator's listeners are on loopback only: nothing on another machine is meant to reach it.</summary>
public sealed class SettingsTests
{
    [Theory]
    [InlineData("urls", "http://0.0.0.0:5210")]
    [InlineData("urls", "http://10.20.30.40:5210")]
    [InlineData("urls", "http://*:5210")]
    [InlineData("http_ports", "5210")]
    [InlineData("IoSimulator:Modbus", "0.0.0.0:1502")]
    [InlineData("IoSimulator:Modbus", "192.168.71.150:502")]
    [InlineData("IoSimulator:Modbus", "localhost")]
    public void ItRefusesToListenBeyondLoopback(string key, string value)
    {
        var failures = Validate(new Dictionary<string, string?> { [key] = value });

        Assert.Contains(failures, f => f.Contains(value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5210", "127.0.0.1:1502")]
    [InlineData("http://[::1]:5210", "[::1]:1502")]
    [InlineData("http://localhost:5210", "127.0.0.1:0")]
    public void LoopbackIsAccepted(string url, string modbus) =>
        Assert.Empty(Validate(new Dictionary<string, string?> { ["urls"] = url, ["IoSimulator:Modbus"] = modbus }));

    [Fact]
    public void ADelayIsNeverNegative()
    {
        var failures = Validate(new Dictionary<string, string?> { ["IoSimulator:Timing:LockFeedbackDelay"] = "-00:00:01" });

        Assert.Contains(failures, f => f.Contains("LockFeedbackDelay", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    public void CargoIsOnlyInTheCabinetsSlots(string slot)
    {
        var failures = Validate(new Dictionary<string, string?> { ["IoSimulator:Cargo:0"] = "2", ["IoSimulator:Cargo:1"] = slot });

        Assert.Contains(failures, f => f.Contains($"slot {slot}", StringComparison.Ordinal));
    }

    [Fact]
    public void CargoInTheSlotsIsAccepted()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IoSimulator:Cargo:0"] = "1", ["IoSimulator:Cargo:1"] = "8" }).Build();
        var settings = new IoSimulatorSettings();
        settings.ReadFrom(configuration);

        Assert.Equal([1, 8], settings.Cargo);
        Assert.False(new IoSimulatorSettingsValidator().Validate(null, settings).Failed);
    }

    private static List<string> Validate(Dictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var settings = new IoSimulatorSettings();
        settings.ReadFrom(configuration);
        var result = new IoSimulatorSettingsValidator().Validate(null, settings);
        return result.Failed ? [.. result.Failures] : [];
    }
}
