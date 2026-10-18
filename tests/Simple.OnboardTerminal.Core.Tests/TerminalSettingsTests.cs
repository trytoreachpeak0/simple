using System.Net;

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

            Assert.Null(loaded.IoModule);

            // Its own key: by default the one of this computer, in the user's store, in the TPM if there is one.
            Assert.Equal(TerminalSettings.DefaultKeyName, loaded.EffectiveKeyName);
            Assert.False(loaded.MachineKey);
            Assert.True(loaded.TryTpm);

            var other = loaded with
            {
                Server = "https://172.19.205.222:5181",
                IoModule = "192.168.71.150:502",
                KeyName = "Simple-Terminal-dev-1",
                KeyStore = "machine",
                KeyProvider = "software",
            };
            other.Save(path);
            Assert.Equal(other, TerminalSettings.Load(path));
            Assert.Equal(IPEndPoint.Parse("192.168.71.150:502"), other.IoModuleEndpoint);
            Assert.Equal(("Simple-Terminal-dev-1", true, false), (other.EffectiveKeyName, other.MachineKey, other.TryTpm));
            Assert.Empty(other.Problems());
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

    [Theory]
    [InlineData("a key", null, null, "keyName 'a key'")]
    [InlineData(null, "system", null, "keyStore 'system'")]
    [InlineData(null, null, "tpm", "keyProvider 'tpm'")]
    public void WhereTheKeyIsMustBeSaidPlainly(string? name, string? store, string? provider, string problem)
    {
        var settings = new TerminalSettings("vehicle-1", TerminalSettings.DefaultServer, Pin, KeyName: name, KeyStore: store, KeyProvider: provider);

        Assert.Contains(problem, Assert.Single(settings.Problems()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("192.168.71.150")]
    [InlineData("io-module:502")]
    [InlineData("192.168.71.150:0")]
    public void AnIoModuleIsAnAddressAndAPort(string module)
    {
        var settings = new TerminalSettings("vehicle-1", TerminalSettings.DefaultServer, Pin, module);

        Assert.Contains($"ioModule '{module}'", Assert.Single(settings.Problems()), StringComparison.Ordinal);
    }
}
