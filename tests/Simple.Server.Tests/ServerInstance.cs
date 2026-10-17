using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Simple.Server.Accounts;
using Simple.Server.Configuration;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;
using Simple.Server.Persistence;
using Simple.Server.Riot;

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
/// <param name="riot">Configure a fleet manager, the <see cref="StubRiot"/>; false leaves <c>Simple:Riot:Url</c> empty.</param>
/// <param name="extraSettings">Settings that override the test defaults, for example a lower login rate limit.</param>
public sealed class ServerInstance(
    string? instance = null,
    bool kestrel = false,
    TimeProvider? time = null,
    bool riot = true,
    IReadOnlyDictionary<string, string?>? extraSettings = null) : IAsyncDisposable
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

    public StubRiot Riot { get; } = new();

    /// <summary>The server's program log directory (Simple:Logs:Directory), under the temp folder; deleted on dispose.</summary>
    public string LogDirectory => Path.Combine(Path.GetTempPath(), "simple-server-tests", Instance);

    public ServerSettings Database => new() { Instance = Instance, ConnectionString = SqlServer };

    public IServiceProvider Services =>
        factory?.Services ?? throw new InvalidOperationException("The server is not running.");

    /// <summary>With <c>kestrel</c>: where the API listens.</summary>
    public Uri ApiAddress => Addresses().Single(a => a.Scheme == Uri.UriSchemeHttp);

    /// <summary>The administrator's password in every test server, set the way the server command sets it.</summary>
    public const string AdminPassword = "admin-password-for-tests";

    private bool adminPasswordSet;

    /// <summary>
    /// Starts the server (migrating its database) and returns a client for its
    /// API, logged in as the administrator through the console entry. The
    /// administrator's password is set on the first start only, so a restart
    /// keeps every login.
    /// </summary>
    public HttpClient Start()
    {
        StartServer();
        if (!adminPasswordSet)
        {
            AdminPasswordCommand.SetAsync(Services, AdminPassword, CancellationToken.None).GetAwaiter().GetResult();
            adminPasswordSet = true;
        }

        return LogInAsync("admin", AdminPassword).GetAwaiter().GetResult().Client
            ?? throw new InvalidOperationException("The test administrator could not log in.");
    }

    /// <summary>A client for the API that is not logged in and sends no request header.</summary>
    public HttpClient CreateClient()
    {
        if (factory is null)
        {
            throw new InvalidOperationException("The server is not running.");
        }

        return kestrel
            ? new HttpClient { BaseAddress = ApiAddress }
            : factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
    }

    /// <summary>
    /// Logs in through the login API. The client of a successful login carries
    /// its cookie and the request header every changing request needs, the way
    /// the console's browser would; the cookie is sent by hand because it is
    /// <c>Secure</c> and the test API is plain HTTP on loopback.
    /// </summary>
    /// <param name="remoteAddress">The address the request seems to come from (in-memory server only, <see cref="TestRemoteAddress"/>).</param>
    public async Task<TestLogin> LogInAsync(string name, string password, string entry = "console", string? remoteAddress = null)
    {
        using var anonymous = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name, password, entry }),
        };
        request.Headers.Add(RequestHeader.Name, "1");
        if (remoteAddress is not null)
        {
            request.Headers.Add(TestRemoteAddress.Header, remoteAddress);
        }

        var response = await anonymous.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return new TestLogin(response, null, null);
        }

        var cookie = TestLogin.CookieOf(response)
            ?? throw new InvalidOperationException("A successful login set no login cookie.");
        return new TestLogin(response, cookie, WithCookie(cookie, remoteAddress));
    }

    /// <summary>A client that sends the given login cookie and the request header.</summary>
    public HttpClient WithCookie(string cookie, string? remoteAddress = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{LoginSetup.CookieName}={cookie}");
        client.DefaultRequestHeaders.Add(RequestHeader.Name, "1");
        if (remoteAddress is not null)
        {
            client.DefaultRequestHeaders.Add(TestRemoteAddress.Header, remoteAddress);
        }

        return client;
    }

    private void StartServer()
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
            // Nothing listens there either; the stub RIoT answers.
            [$"{RiotSettings.Section}:Url"] = riot ? "http://127.0.0.1:9" : "",
            [$"{RiotSettings.Section}:CallApiKey"] = StubRiot.CallApiKey,
            [$"{RiotSettings.Section}:PollInterval"] = "00:00:00.1",
            [$"{RiotSettings.Section}:RetryMaxDelay"] = "00:00:00.3",
            // Tests log in often; LoginRateLimitTests sets its own limit.
            [$"{LoginSettings.Section}:AttemptsPerWindow"] = "1000",
        };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }
        // Without real sockets there is no terminal endpoint to reach, so none
        // is configured, and no server key is made for the test.
        settings[ServerSettings.OnboardTerminalsUrlKey] = kestrel ? "https://127.0.0.1:0" : "";
        settings[ServerSettings.ServerKeyStoreKey] = TestKeyStore.Name;
        if (kestrel)
        {
            settings["urls"] = "http://127.0.0.1:0";
        }

        var created = new Factory(settings, time, MesGateway, Riot);
        try
        {
            if (kestrel)
            {
                created.UseKestrel();
                created.StartServer();
            }
            else
            {
                // Starts the in-memory test server; StartServer would listen on real sockets.
                _ = created.Server;
            }

            factory = created;
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
        // The administrator went with the database: the next start makes it again.
        adminPasswordSet = false;
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

                try
                {
                    Directory.Delete(LogDirectory, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a file just closed can still be held for a moment
                    // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
                }
            }
        }
    }

    private List<Uri> Addresses() =>
        Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToList();

    private sealed class Factory(Dictionary<string, string?> settings, TimeProvider? time, StubMesGateway mesGateway, StubRiot riot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
                .ConfigureTestServices(services => services.AddTransient<IStartupFilter, TestRemoteAddress>())
                .ConfigureTestServices(services => services
                    .AddHttpClient(MesGatewayPoller.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(mesGateway.CreateHandler))
                .ConfigureTestServices(services => services
                    .AddHttpClient(RiotConnectionProbe.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(riot.CreateHandler));
            if (time is not null)
            {
                builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(time)));
            }
        }
    }
}
