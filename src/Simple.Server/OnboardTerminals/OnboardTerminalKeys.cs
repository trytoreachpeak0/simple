using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;
using Simple.Server.Sites;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// A terminal key an engineer confirmed for a vehicle (ADR 0025): the server
/// uses the reports of a terminal that presents this key and claims this
/// vehicle. A vehicle has at most one, and a key belongs to at most one vehicle.
/// </summary>
public sealed class OnboardTerminalKey
{
    public long Id { get; set; }

    /// <summary>The vehicle the terminal claimed when it was confirmed (ADR 0007, 0029).</summary>
    public required string VehicleId { get; init; }

    /// <summary>The pin of the key: the hex SHA-256 of the terminal certificate's public key (<see cref="Simple.Protocol.CertificatePin"/>).</summary>
    public required string KeyPin { get; init; }

    public DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>The account that confirmed it.</summary>
    public string? ConfirmedBy { get; init; }
}

/// <summary>Where the registered keys are kept: each change in one transaction with its entry in the event record.</summary>
public interface IOnboardTerminalKeyStore
{
    Task<IReadOnlyList<OnboardTerminalKey>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Registers <paramref name="keyPin"/> for <paramref name="vehicleId"/>, ending any registration of the vehicle or of the key, and records <see cref="EventTypes.OnboardConfirmed"/>.</summary>
    Task<OnboardTerminalKey> RegisterAsync(string vehicleId, string keyPin, DateTimeOffset at, Actor actor, object eventData, CancellationToken cancellationToken);

    /// <summary>Ends the registration <paramref name="id"/>, if it is still there, and records <paramref name="eventType"/>: a person's action with an actor, the server's own without.</summary>
    Task RemoveAsync(long id, string eventType, Actor? actor, object eventData, CancellationToken cancellationToken);
}

public sealed class OnboardTerminalKeyStore(SimpleDbContext db, EventStore events) : IOnboardTerminalKeyStore
{
    public async Task<IReadOnlyList<OnboardTerminalKey>> LoadAsync(CancellationToken cancellationToken) =>
        await db.OnboardTerminalKeys.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<OnboardTerminalKey> RegisterAsync(string vehicleId, string keyPin, DateTimeOffset at, Actor actor, object eventData, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.OnboardTerminalKeys.Where(k => k.VehicleId == vehicleId || k.KeyPin == keyPin).ExecuteDeleteAsync(cancellationToken);
        var key = new OnboardTerminalKey { VehicleId = vehicleId, KeyPin = keyPin, ConfirmedAt = at, ConfirmedBy = actor.Account };
        db.OnboardTerminalKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        await events.AppendAsync(EventTypes.OnboardConfirmed, actor, eventData, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return key;
    }

    public async Task RemoveAsync(long id, string eventType, Actor? actor, object eventData, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.OnboardTerminalKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken);
        await (actor is null
            ? events.AppendAsync(eventType, EventSources.Server, eventData, cancellationToken)
            : events.AppendAsync(eventType, actor, eventData, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Whether a vehicle id is in the vehicle list (ADR 0007): only a listed vehicle's terminal can be confirmed (ADR 0029).</summary>
public interface IVehicleList
{
    /// <summary>Whether <paramref name="vehicleId"/>, exactly as written, is in the vehicle list now.</summary>
    Task<bool> ContainsAsync(string vehicleId, CancellationToken cancellationToken);
}

/// <summary>
/// The vehicle list of the current site configuration (ADR 0017), compared
/// exactly (BIN2). SQL Server ignores trailing spaces even then, so the
/// registry hands it only ids without surrounding whitespace
/// (<see cref="OnboardTerminalRegistry.IsValidVehicleId"/>).
/// </summary>
public sealed class SiteVehicleList(IServiceScopeFactory scopes) : IVehicleList
{
    public async Task<bool> ContainsAsync(string vehicleId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SiteConfigurationStore>().HasVehicleAsync(vehicleId, cancellationToken);
    }
}

/// <summary>Reads the registered keys into the registry at start, before the terminal endpoint takes streams.</summary>
public sealed class OnboardTerminalKeyLoader(OnboardTerminalRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.LoadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
