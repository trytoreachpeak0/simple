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
    [InlineData("Simple:Mes:Source", "Oracle", "Simple:Mes:Source 'Oracle' is not a known MES source")]
    [InlineData("Simple:Mes:Source", "", "Simple:Mes:Source '' is not a known MES source")]
    [InlineData("Simple:Mes:Fake:SeedFile", "", "Simple:Mes:Fake:SeedFile is missing")]
    [InlineData("Simple:Mes:Fake:SeedFile", "no-such-seed.json", "no-such-seed.json' cannot be read")]
    [InlineData("Simple:Mes:RoundInterval", "00:00:00", "Simple:Mes:RoundInterval must be longer than zero")]
    [InlineData("Simple:Mes:RoundInterval", "five seconds", "Simple:Mes:RoundInterval 'five seconds' is not a duration")]
    [InlineData("Simple:Mes:RetryMaxDelay", "00:00:00.5", "Simple:Mes:RetryMaxDelay must not be shorter than Simple:Mes:RetryDelay")]
    public void AnInvalidSettingIsNamed(string key, string value, string message)
    {
        var failures = Check((key, value));

        Assert.Contains(failures, f => f.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>Runs the real gateway executable, as a person would, and reads what it says.</summary>
    [Theory]
    [InlineData("--urls=http://0.0.0.0:5190", "urls 'http://0.0.0.0:5190' is not a loopback address")]
    [InlineData("--Simple:Mes:Source=Oracle", "Simple:Mes:Source 'Oracle' is not a known MES source")]
    [InlineData("--Simple:SettingsFile=no-such-settings.json", "Simple:SettingsFile")]
    [InlineData("--Simple:Logs:RetainedFileCount=0", "Simple:Logs:RetainedFileCount '0' is not a whole number above 0")]
    public async Task TheGatewayRefusesToStartAndNamesTheSetting(string argument, string message)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "simple-gateway-tests", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Simple.MesGateway.exe" : "Simple.MesGateway"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { argument, $"--Simple:Logs:Directory={logDirectory}" },
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

            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }
}
