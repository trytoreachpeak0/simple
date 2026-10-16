using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Persistence;
using Simple.Server.Status;

namespace Simple.Server.Events;

/// <summary>
/// Brings the database to the current schema before anything else starts,
/// and records the server's start and stop in the event record.
/// </summary>
public sealed partial class ServerLifecycleRecorder(
    IServiceScopeFactory scopes,
    ServerInfo info,
    IOptions<ServerSettings> settings,
    ILogger<ServerLifecycleRecorder> logger) : IHostedLifecycleService
{
    private readonly Lock gate = new();
    private bool started;
    private Task? stopped;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleDbContext>();
        LogMigrating(logger, settings.Value.DatabaseName);
        // Creates the database on first start. Concurrent starts against one
        // database are serialized by EF Core's migration lock.
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        await AppendAsync(EventTypes.ServerStarted, cancellationToken);
        lock (gate)
        {
            started = true;
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            // A server that never started did not stop. The host can be
            // stopped from two places at once (its run loop and its owner);
            // both wait for the one recording, so neither tears the services
            // down underneath it.
            return started ? stopped ??= RecordStopAsync(cancellationToken) : Task.CompletedTask;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RecordStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AppendAsync(EventTypes.ServerStopped, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Stopping goes on: a database that is gone must not keep the process alive.
            LogStopNotRecorded(logger, e);
        }
    }

    private async Task AppendAsync(string type, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<EventStore>();
        await store.AppendAsync(
            type,
            EventSources.Server,
            new { info.Version, settings.Value.Instance, Environment.ProcessId },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bringing database {Database} to the current schema")]
    private static partial void LogMigrating(ILogger logger, string database);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the server stop in the event record")]
    private static partial void LogStopNotRecorded(ILogger logger, Exception exception);
}
