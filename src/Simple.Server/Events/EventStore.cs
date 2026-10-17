using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Events;

/// <summary>Appends to and reads the event record. There is no way to change or remove an entry.</summary>
public sealed class EventStore(SimpleDbContext db, TimeProvider time)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public Task<EventRecord> AppendAsync(string type, string source, object? data, CancellationToken cancellationToken) =>
        AppendAsync(
            new EventRecord
            {
                OccurredAt = time.GetUtcNow(),
                Type = type,
                Source = source,
                Data = data is null ? null : JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
            },
            cancellationToken);

    /// <summary>Appends a person's action: who did it, through which entry and from which computer (ADR 0019, 0045).</summary>
    public Task<EventRecord> AppendAsync(string type, Actor actor, object? data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return AppendAsync(
            new EventRecord
            {
                OccurredAt = time.GetUtcNow(),
                Type = type,
                Source = actor.Entry,
                Data = data is null ? null : JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
                Account = actor.Account,
                RemoteAddress = actor.RemoteAddress,
            },
            cancellationToken);
    }

    /// <summary>
    /// Appends one event. Appends take an exclusive application lock that is
    /// held until commit, so ids are assigned in commit order: a reader
    /// following the cursor never passes an id whose event commits later.
    /// The database refuses an insert made without the lock. Joins the
    /// context's transaction when there is one, and then commits with it.
    /// </summary>
    public async Task<EventRecord> AppendAsync(EventRecord record, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await db.Database.ExecuteSqlRawAsync(
            $"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = N'{SimpleDbContext.EventsAppendLock}', @LockMode = 'Exclusive', @LockOwner = 'Transaction';
            IF @result < 0 THROW 50002, 'Could not take the event append lock.', 1;
            """,
            cancellationToken);
        db.Events.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return record;
    }

    /// <summary>
    /// Reads events in commit order, after the cursor <paramref name="after"/>
    /// (an event id) and within [<paramref name="since"/>, <paramref name="until"/>).
    /// </summary>
    public async Task<EventPage> ReadAsync(
        long? after, DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        var query = db.Events.AsNoTracking();
        if (after is { } cursor)
        {
            query = query.Where(e => e.Id > cursor);
        }

        if (since is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (until is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        var rows = await query.OrderBy(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).Select(EventView.From).ToList();
        // With no new events the cursor stays where it was, so a caller can keep polling with it.
        return new EventPage(items, items.Count > 0 ? items[^1].Id : after, hasMore);
    }
}

/// <summary>Who did an action: the account (null for a server command), the entry (<see cref="EventSources"/>) and the computer.</summary>
public sealed record Actor(string? Account, string Entry, string? RemoteAddress);

/// <param name="NextCursor">Pass as <c>after</c> to read on; null when nothing has been read yet.</param>
public sealed record EventPage(IReadOnlyList<EventView> Items, long? NextCursor, bool HasMore);

/// <param name="Account">For a person's action, the account that did it.</param>
/// <param name="RemoteAddress">For a person's action through the console or MCP, the computer it came from.</param>
public sealed record EventView(long Id, DateTimeOffset OccurredAt, string Type, string Source, JsonElement? Data, string? Account = null, string? RemoteAddress = null)
{
    public static EventView From(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        JsonElement? data = null;
        if (record.Data is not null)
        {
            using var document = JsonDocument.Parse(record.Data);
            data = document.RootElement.Clone();
        }

        return new EventView(record.Id, record.OccurredAt, record.Type, record.Source, data, record.Account, record.RemoteAddress);
    }
}
