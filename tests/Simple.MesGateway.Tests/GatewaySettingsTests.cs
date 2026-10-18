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
    [InlineData("Simple:Mes:Source", "Mes", "Simple:Mes:Source 'Mes' is not a known MES source")]
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

    /// <summary>The Oracle source as a person sets it for one run, from outside the repository.</summary>
    private static readonly (string Key, string? Value)[] Oracle =
    [
        ("Simple:Mes:Source", "Oracle"),
        ("Simple:Mes:Oracle:DataSource", "127.0.0.1:1521/FAKEMES"),
        ("Simple:Mes:Oracle:User", "simple_reader"),
        ("Simple:Mes:Oracle:Password", OracleMesSourceTests.Secret),
    ];

    [Fact]
    public void TheOracleSourceWithEverythingSetIsValid()
    {
        Assert.Empty(Check(Oracle));

        var settings = new GatewaySettings();
        settings.ReadFrom(new ConfigurationBuilder().AddInMemoryCollection(Oracle.Select(o => new KeyValuePair<string, string?>(o.Key, o.Value))).Build());
        Assert.True(settings.UsesOracleSource);
        Assert.False(settings.UsesFakeSource);
        Assert.Equal("127.0.0.1:1521/FAKEMES", settings.Oracle.DataSource);
        Assert.Equal("simple_reader", settings.Oracle.User);
        Assert.Equal(OracleMesSourceTests.Secret, settings.Oracle.Password);
        // The previous line's query timeout; with the connect timeout the whole read stays under the server's 90 s stall threshold with a 30 s retry wait on top (ADR 0027).
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Oracle.QueryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.Oracle.ConnectTimeout);
        Assert.True(settings.Oracle.ReadTimeout + settings.RetryMaxDelay < TimeSpan.FromSeconds(90));
    }

    [Theory]
    [InlineData("Simple:Mes:Oracle:DataSource", "", "Simple:Mes:Oracle:DataSource is missing")]
    [InlineData("Simple:Mes:Oracle:User", "", "Simple:Mes:Oracle:User is missing")]
    [InlineData("Simple:Mes:Oracle:Password", "", "Simple:Mes:Oracle:Password is missing")]
    [InlineData("Simple:Mes:Oracle:DataSource", "simple_reader/" + OracleMesSourceTests.Secret + "@127.0.0.1:1521/FAKEMES", "Simple:Mes:Oracle:DataSource holds an @")]
    [InlineData("Simple:Mes:Oracle:QueryTimeout", "00:00:00", "Simple:Mes:Oracle:QueryTimeout must be longer than zero")]
    [InlineData("Simple:Mes:Oracle:ConnectTimeout", "-00:00:01", "Simple:Mes:Oracle:ConnectTimeout must be longer than zero")]
    [InlineData("Simple:Mes:Oracle:QueryTimeout", "half a minute", "Simple:Mes:Oracle:QueryTimeout 'half a minute' is not a duration")]
    public void AnInvalidOracleSettingIsNamedWithoutThePassword(string key, string value, string message)
    {
        var failures = Check([.. Oracle.Where(o => o.Key != key), (key, value)]);

        Assert.Contains(failures, f => f.Contains(message, StringComparison.Ordinal));
        Assert.All(failures, f => Assert.DoesNotContain(OracleMesSourceTests.Secret, f, StringComparison.Ordinal));
    }

    /// <summary>
    /// The repository's own settings never pick the Oracle source or carry
    /// anything of it: where the MES is and how to log in to it come from
    /// outside the repository, for one run (repository CLAUDE.md, Safety).
    /// </summary>
    [Fact]
    public void TheShippedSettingsCarryNothingOfTheOracleSource()
    {
        var files = Directory.GetFiles(Path.Combine(MesTaskUnionQueryTests.RepositoryRoot(), "src", "Simple.MesGateway"), "appsettings*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();
            Assert.DoesNotContain(configuration.GetSection("Simple:Mes:Oracle").AsEnumerable(), e => e.Value is not null);
            Assert.True(configuration["Simple:Mes:Source"] is null or GatewaySettings.FakeSource, $"{file} picks the source {configuration["Simple:Mes:Source"]}.");
        }
    }

    /// <summary>Runs the real gateway executable, as a person would, and reads what it says.</summary>
    [Theory]
    [InlineData("--urls=http://0.0.0.0:5190", "urls 'http://0.0.0.0:5190' is not a loopback address")]
    [InlineData("--Simple:Mes:Source=Mes", "Simple:Mes:Source 'Mes' is not a known MES source")]
    [InlineData("--Simple:Mes:Source=Oracle", "Simple:Mes:Oracle:DataSource is missing")]
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
            // Run as a service, nobody reads standard error: the reason is in the program log
            // too, unless the log settings themselves are what is wrong.
            if (!argument.StartsWith("--Simple:Logs:", StringComparison.Ordinal))
            {
                var log = Simple.Logging.ProgramLog.ReadText(logDirectory);
                Assert.Contains("[FTL] Simple.Logging.ProgramLog: The MES gateway refuses to start", log, StringComparison.Ordinal);
                Assert.Contains(message, log, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            {
                Directory.Delete(logDirectory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file just closed can still be held for a moment
                // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
            }
        }
    }
}
