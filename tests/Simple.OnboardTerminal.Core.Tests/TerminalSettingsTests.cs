namespace Simple.OnboardTerminal.Core.Tests;

public sealed class TerminalSettingsTests
{
    private static readonly string Pin = new('a', 64);

    [Fact]
    public void ASavedFileLoadsBackAndAMissingServerIsThisMachine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simple-terminal-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""{"vehicleId": "vehicle-1", "serverCertificatePin": "{{Pin}}"}""");
            var loaded = TerminalSettings.Load(path);
            Assert.Equal(new TerminalSettings("vehicle-1", TerminalSettings.DefaultServer, Pin), loaded);
            Assert.Empty(loaded.Problems());

            var other = loaded with { Server = "https://172.19.205.222:5181" };
            other.Save(path);
            Assert.Equal(other, TerminalSettings.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null, "https://127.0.0.1:5181", "aa", "vehicleId is missing")]
    [InlineData("vehicle-1", "http://127.0.0.1:5181", "aa", "is not an https:// address")]
    [InlineData("vehicle-1", "https://127.0.0.1:5181", null, "serverCertificatePin is missing")]
    [InlineData("vehicle-1", "https://127.0.0.1:5181", "not-hex", "serverCertificatePin is missing or not 64 hex digits")]
    public void WhatKeepsTheTerminalFromStartingIsNamed(string? vehicle, string server, string? pin, string problem)
    {
        var settings = new TerminalSettings(vehicle, server, pin == "aa" ? Pin : pin);

        Assert.Contains(problem, Assert.Single(settings.Problems()), StringComparison.Ordinal);
    }
}
