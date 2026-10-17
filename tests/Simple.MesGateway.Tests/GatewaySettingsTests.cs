using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Simple.MesGateway.Configuration;

namespace Simple.MesGateway.Tests;

public sealed class GatewaySettingsTests
{
    /// <summary>The gateway's settings from its shipped appsettings.json, with the given overrides on top.</summary>
    private static IReadOnlyList<string> Check(params (string Key, string? Value)[] overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>(o.Key, o.Value)))
            .Build();
        var settings = new GatewaySettings();
        settings.ReadFrom(configuration);
        return GatewaySettingsValidator.Check(settings);
    }

    [Fact]
    public void TheShippedSettingsAreValidAndUseTheFakeSourceOnLoopback()
    {
        Assert.Empty(Check());

        var settings = new GatewaySettings();
        settings.ReadFrom(new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build());
        Assert.True(settings.UsesFakeSource);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.RetryMaxDelay);
        Assert.Equal(new KeyValuePair<string, string>("urls", "http://127.0.0.1:5190"), Assert.Single(settings.ListenAddresses));
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5190", "urls 'http://0.0.0.0:5190' is not a loopback address")]
    [InlineData("urls", "http://127.0.0.1:5190;http://10.1.2.3:5190", "urls 'http://10.1.2.3:5190' is not a loopback address")]
    [InlineData("urls", "http://*:5190", "urls 'http://*:5190' is not a loopback address")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://factory01:5190", "Kestrel:Endpoints:Http:Url 'http://factory01:5190' is not a loopback address")]
    [InlineData("http_ports", "5190", "http_ports '5190' listens on every network interface")]
    [InlineData("MesGateway:Source", "Oracle", "MesGateway:Source 'Oracle' is not a known MES source")]
    [InlineData("MesGateway:Source", "", "MesGateway:Source '' is not a known MES source")]
    [InlineData("MesGateway:Fake:SeedFile", "", "MesGateway:Fake:SeedFile is missing")]
    [InlineData("MesGateway:Fake:SeedFile", "no-such-seed.json", "no-such-seed.json' cannot be read")]
    [InlineData("MesGateway:RoundInterval", "00:00:00", "MesGateway:RoundInterval must be longer than zero")]
    [InlineData("MesGateway:RoundInterval", "five seconds", "MesGateway:RoundInterval 'five seconds' is not a duration")]
    [InlineData("MesGateway:RetryMaxDelay", "00:00:00.5", "MesGateway:RetryMaxDelay must not be shorter than MesGateway:RetryDelay")]
    public void AnInvalidSettingIsNamed(string key, string value, string message)
    {
        var failures = Check((key, value));

        Assert.Contains(failures, f => f.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>Runs the real gateway executable, as a person would, and reads what it says.</summary>
    [Theory]
    [InlineData("--urls=http://0.0.0.0:5190", "urls 'http://0.0.0.0:5190' is not a loopback address")]
    [InlineData("--MesGateway:Source=Oracle", "MesGateway:Source 'Oracle' is not a known MES source")]
    [InlineData("--Simple:SettingsFile=no-such-settings.json", "Simple:SettingsFile")]
    public async Task TheGatewayRefusesToStartAndNamesTheSetting(string argument, string message)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Simple.MesGateway.exe" : "Simple.MesGateway"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { argument },
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await output;

            Assert.Equal(2, process.ExitCode);
            Assert.Contains("The MES gateway refuses to start", error, StringComparison.Ordinal);
            Assert.Contains(message, error, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
