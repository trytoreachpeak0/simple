using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Events;

/// <summary>Appends to and reads the event record. There is no way to change or remove an entry.</summary>
public sealed class EventStore(SimpleDbContext db, TimeProvider time)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public async Task<EventRecord> AppendAsync(string type, string source, object? data, CancellationToken cancellationToken)
    {
        var record = new EventRecord
        {
            OccurredAt = time.GetUtcNow(),
            Type = type,
            Source = source,
            Data = data is null ? null : JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
        };
        db.Events.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    /// <summary>
    /// Reads events in insertion order, after the cursor <paramref name="after"/>
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

/// <param name="NextCursor">Pass as <c>after</c> to read on; null when nothing has been read yet.</param>
public sealed record EventPage(IReadOnlyList<EventView> Items, long? NextCursor, bool HasMore);

public sealed record EventView(long Id, DateTimeOffset OccurredAt, string Type, string Source, JsonElement? Data)
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

        return new EventView(record.Id, record.OccurredAt, record.Type, record.Source, data);
    }
}
