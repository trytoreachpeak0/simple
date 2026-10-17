using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Simple.Server.Configuration;
using Simple.Server.Persistence;

namespace Simple.Server.Tests;

/// <summary>
/// One server instance, run in process, with a database of its own: a random
/// instance name, so concurrent tests and sessions never share a database.
/// Disposing stops the server and drops the database.
/// </summary>
public sealed class ServerInstance : IAsyncDisposable
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

    private readonly Dictionary<string, string?> settings;
    private WebApplicationFactory<Program>? factory;

    public ServerInstance(string? instance = null)
    {
        Instance = instance ?? $"test_{Guid.NewGuid():N}"[..17];
        settings = new()
        {
            [ServerSettings.InstanceKey] = Instance,
            [ServerSettings.ConnectionStringKey] = SqlServer,
        };
        Database = new ServerSettings { Instance = Instance, ConnectionString = SqlServer };
    }

    public string Instance { get; }

    public ServerSettings Database { get; }

    public IServiceProvider Services =>
        factory?.Services ?? throw new InvalidOperationException("The server is not running.");

    /// <summary>Starts the server (migrating its database) and returns a client for it.</summary>
    public HttpClient Start()
    {
        if (factory is not null)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        var created = new Factory(settings);
        try
        {
            var client = created.CreateClient();
            factory = created;
            return client;
        }
        catch
        {
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
            await DropDatabaseAsync();
        }
    }

    private sealed class Factory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
    }
}
