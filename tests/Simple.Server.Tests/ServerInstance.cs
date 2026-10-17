using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Simple.Server.Configuration;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;
using Simple.Server.Persistence;

namespace Simple.Server.Tests;

/// <summary>
/// One server instance, run in process, with a database of its own: a random
/// instance name, so concurrent tests and sessions never share a database.
/// Disposing stops the server, drops the database and deletes the instance's
/// server key. Its MES gateway is a <see cref="StubMesGateway"/> that answers
/// a good, empty round unless a test tells it otherwise, so the MES adds
/// nothing to the event record.
/// </summary>
/// <param name="kestrel">
/// Listen on real loopback sockets (the API on http://127.0.0.1:0, the
/// terminal endpoint on https://127.0.0.1:0) instead of the in-memory test
/// server; needed to reach the terminal endpoint.
/// </param>
/// <param name="time">Replaces the server's clock, for example with a FakeTimeProvider.</param>
public sealed class ServerInstance(string? instance = null, bool kestrel = false, TimeProvider? time = null) : IAsyncDisposable
{
    /// <summary>
    /// The SQL Server instance the test databases are created on, without a
    /// database. SIMPLE_TEST_SQLSERVER overrides the LocalDB default, for
    /// example <c>Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true</c>.
    /// </summary>
    public static string SqlServer { get; } =
        Environment.GetEnvironmentVariable("SIMPLE_TEST_SQLSERVER") is { Length: > 0 } configured
            ? configured
            : @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";

    private WebApplicationFactory<Program>? factory;

    public string Instance { get; } = instance ?? $"test_{Guid.NewGuid():N}"[..17];

    public StubMesGateway MesGateway { get; } = new();

    /// <summary>The server's program log directory (Simple:Logs:Directory), under the temp folder; deleted on dispose.</summary>
    public string LogDirectory => Path.Combine(Path.GetTempPath(), "simple-server-tests", Instance);

    public ServerSettings Database => new() { Instance = Instance, ConnectionString = SqlServer };

    public IServiceProvider Services =>
        factory?.Services ?? throw new InvalidOperationException("The server is not running.");

    /// <summary>With <c>kestrel</c>: where the API listens.</summary>
    public Uri ApiAddress => Addresses().Single(a => a.Scheme == Uri.UriSchemeHttp);

    /// <summary>Starts the server (migrating its database) and returns a client for its API.</summary>
    public HttpClient Start()
    {
        if (factory is not null)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        var settings = new Dictionary<string, string?>
        {
            [ServerSettings.InstanceKey] = Instance,
            [ServerSettings.ConnectionStringKey] = SqlServer,
            ["Simple:Logs:Directory"] = LogDirectory,
            // Nothing listens there; the stub answers in its place.
            [$"{MesGatewaySettings.Section}:Url"] = "http://127.0.0.1:9",
            [$"{MesGatewaySettings.Section}:PollInterval"] = "00:00:00.1",
            [$"{MesGatewaySettings.Section}:RetryMaxDelay"] = "00:00:00.3",
            [$"{MesGatewaySettings.Section}:StaleAfter"] = "00:00:02",
        };
        // Without real sockets there is no terminal endpoint to reach, so none
        // is configured, and no server key is made for the test.
        settings[ServerSettings.OnboardTerminalsUrlKey] = kestrel ? "https://127.0.0.1:0" : "";
        settings[ServerSettings.ServerKeyStoreKey] = TestKeyStore.Name;
        if (kestrel)
        {
            settings["urls"] = "http://127.0.0.1:0";
        }

        var created = new Factory(settings, time, MesGateway);
        try
        {
            HttpClient client;
            if (kestrel)
            {
                created.UseKestrel();
                created.StartServer();
                factory = created;
                client = new HttpClient { BaseAddress = ApiAddress };
            }
            else
            {
                client = created.CreateClient();
                factory = created;
            }

            return client;
        }
        catch
        {
            factory = null;
            created.Dispose();
            throw;
        }
    }

    /// <summary>Stops the server the way the host stops on shutdown.</summary>
    public async Task StopAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
            factory = null;
        }
    }

    public SimpleDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleDbContext>().UseSqlServer(Database.DatabaseConnectionString).Options);

    public async Task DropDatabaseAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            try
            {
                await DropDatabaseAsync();
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    ServerCertificate.Delete(Database.ServerKeyName, TestKeyStore.IsMachine);
                }

                if (Directory.Exists(LogDirectory))
                {
                    Directory.Delete(LogDirectory, recursive: true);
                }
            }
        }
    }

    private List<Uri> Addresses() =>
        Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToList();

    private sealed class Factory(Dictionary<string, string?> settings, TimeProvider? time, StubMesGateway mesGateway) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
                .ConfigureTestServices(services => services
                    .AddHttpClient(MesGatewayPoller.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(mesGateway.CreateHandler));
            if (time is not null)
            {
                builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(time)));
            }
        }
    }
}
