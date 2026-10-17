using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Simple.Server.Configuration;

namespace Simple.Server.Tests;

public sealed class ServerSettingsTests
{
    private const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";

    private static IReadOnlyList<string> Check(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            [ServerSettings.InstanceKey] = "dev",
            [ServerSettings.ConnectionStringKey] = LocalDb,
            ["urls"] = "http://127.0.0.1:5180",
        };
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        var settings = new ServerSettings();
        settings.ReadFrom(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return ServerSettingsValidator.Check(settings);
    }

    [Fact]
    public void TheDevelopmentDefaultsAreValid() => Assert.Empty(Check());

    [Theory]
    [InlineData("http://127.0.0.1:5180;http://[::1]:5180")]
    [InlineData("http://localhost:5180")]
    [InlineData("http://127.0.0.2:5181")]
    public void LoopbackAddressesAreAccepted(string urls) => Assert.Empty(Check(("urls", urls)));

    [Fact]
    public void TheApiIsPlainHttpOnly()
    {
        // TLS is the terminal endpoint's; the API is bound without a certificate.
        var failure = Assert.Single(Check(("urls", "https://127.0.0.1:5181")));
        Assert.Contains("is not http://", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5181")]
    [InlineData("https://localhost:5181")]
    [InlineData("https://172.19.205.222:5181")]
    [InlineData("https://[fe80::1]:5181")]
    public void TheTerminalEndpointMayListenWhereVehiclesReachIt(string url) =>
        Assert.Empty(Check((ServerSettings.OnboardTerminalsUrlKey, url)));

    [Fact]
    public void ATerminalEndpointOffLoopbackDoesNotLetTheApiOffLoopback()
    {
        var failures = Check(
            (ServerSettings.OnboardTerminalsUrlKey, "https://172.19.205.222:5181"),
            ("urls", "http://172.19.205.222:5180"));

        var failure = Assert.Single(failures);
        Assert.StartsWith("urls 'http://172.19.205.222:5180' is not a loopback address", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://172.19.205.222:5181", "is not an https:// address")]
    [InlineData("https://0.0.0.0:5181", "listens on every network interface")]
    [InlineData("https://[::]:5181", "listens on every network interface")]
    [InlineData("https://*:5181", "does not name an IP address")]
    [InlineData("https://+:5181", "does not name an IP address")]
    [InlineData("https://factory01:5181", "does not name an IP address")]
    [InlineData("not a url", "is not an https:// address")]
    public void ATerminalEndpointOnEveryInterfaceOrWithoutTlsIsRefusedByName(string url, string reason)
    {
        var failure = Assert.Single(Check((ServerSettings.OnboardTerminalsUrlKey, url)));
        Assert.StartsWith($"{ServerSettings.OnboardTerminalsUrlKey} '", failure, StringComparison.Ordinal);
        Assert.Contains(reason, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServerSettings.ServerKeyNameKey, "has space")]
    [InlineData(ServerSettings.ServerKeyStoreKey, "Elsewhere")]
    public void ABadServerKeySettingIsRefusedByName(string key, string value) =>
        Assert.StartsWith($"{key} '", Assert.Single(Check((key, value))), StringComparison.Ordinal);

    [Fact]
    public void TheServerKeyIsNamedAfterTheInstanceUnlessConfigured()
    {
        Assert.Equal("Simple-Server-wt25", new ServerSettings { Instance = "wt25" }.ServerKeyName);
        Assert.Equal("Other", new ServerSettings { Instance = "wt25", ServerKeyName = "Other" }.ServerKeyName);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5180")]
    [InlineData("http://*:5180")]
    [InlineData("http://+:5180")]
    [InlineData("http://[::]:5180")]
    [InlineData("http://10.20.30.40:5180")]
    [InlineData("http://factory01:5180")]
    [InlineData("http://127.0.0.1:5180;http://192.168.1.10:5180")]
    public void ANonLoopbackListenAddressIsRefusedByName(string urls)
    {
        var failure = Assert.Single(Check(("urls", urls)));
        Assert.StartsWith("urls '", failure, StringComparison.Ordinal);
        Assert.Contains("not a loopback address", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AKestrelEndpointOffLoopbackIsRefusedByName()
    {
        var failure = Assert.Single(Check(("Kestrel:Endpoints:Main:Url", "http://0.0.0.0:80")));
        Assert.StartsWith("Kestrel:Endpoints:Main:Url '", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpPortsAreRefusedBecauseTheyBindEveryInterface()
    {
        var failure = Assert.Single(Check(("http_ports", "8080")));
        Assert.StartsWith("http_ports '8080'", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("a_name_that_is_much_too_long_to_be_part_of_a_database_name")]
    public void AnInvalidInstanceNameIsRefusedByName(string instance)
    {
        var failure = Assert.Single(Check((ServerSettings.InstanceKey, instance)));
        Assert.StartsWith("Simple:Instance '", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "is missing")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Database=Other", "names a database")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Unknown Keyword=1", "cannot be parsed")]
    [InlineData("Integrated Security=true", "names no server")]
    public void ABadConnectionStringIsRefusedByName(string connectionString, string reason)
    {
        var failure = Assert.Single(Check((ServerSettings.ConnectionStringKey, connectionString)));
        Assert.StartsWith("ConnectionStrings:Simple ", failure, StringComparison.Ordinal);
        Assert.Contains(reason, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDatabaseNameCarriesTheInstance()
    {
        var settings = new ServerSettings { Instance = "wt25", ConnectionString = LocalDb };

        Assert.Equal("Simple_wt25", settings.DatabaseName);
        Assert.Contains("Initial Catalog=Simple_wt25", settings.DatabaseConnectionString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the real server executable, as a person would, and reads what it
    /// says. It gets an instance of its own and a connection string that
    /// reaches nothing (a closed loopback port), so a server that wrongly
    /// went on starting could not touch a real database.
    /// </summary>
    [Theory]
    [InlineData("--Simple:Instance=not valid!", "Simple:Instance 'not valid!' is not a valid instance name")]
    [InlineData("--urls=http://0.0.0.0:5180", "urls 'http://0.0.0.0:5180' is not a loopback address")]
    [InlineData("--Simple:OnboardTerminals:Url=https://0.0.0.0:5181", "Simple:OnboardTerminals:Url 'https://0.0.0.0:5181' listens on every network interface")]
    [InlineData("--Simple:SettingsFile=no-such-settings.json", "Simple:SettingsFile")]
    public async Task TheServerRefusesToStartAndNamesTheSetting(string argument, string message)
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        var logDirectory = Path.Combine(Path.GetTempPath(), "simple-server-tests", instance);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Simple.Server.exe" : "Simple.Server"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // Later arguments win, so the case's own argument comes last.
            ArgumentList =
            {
                $"--{ServerSettings.InstanceKey}={instance}",
                $"--{ServerSettings.ConnectionStringKey}=Server=tcp:127.0.0.1,1;Integrated Security=true;Connect Timeout=1",
                $"--{ServerSettings.ServerKeyStoreKey}={TestKeyStore.Name}",
                $"--Simple:Logs:Directory={logDirectory}",
                argument,
            },
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
            Assert.Contains("The server refuses to start", error, StringComparison.Ordinal);
            Assert.Contains(message, error, StringComparison.Ordinal);
            // Run as a service, nobody reads standard error: the reason is in the program log too.
            var log = string.Concat(Simple.Logging.ProgramLog.Files(logDirectory).Select(File.ReadAllText));
            Assert.Contains("[FTL] Simple.Logging.ProgramLog: The server refuses to start", log, StringComparison.Ordinal);
            Assert.Contains(message, log, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            // A server that wrongly went on starting has made its key by now.
            if (OperatingSystem.IsWindows())
            {
                Simple.Server.OnboardTerminals.ServerCertificate.Delete(new ServerSettings { Instance = instance }.ServerKeyName, TestKeyStore.IsMachine);
            }

            try
            {
                Directory.Delete(logDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a log file just closed can still be held for a moment
                // (a virus scan, for example), and a leftover temp folder does no harm.
            }
        }
    }
}
