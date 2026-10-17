using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;
using Simple.Server.Persistence;

namespace Simple.Server.Tests;

public sealed class EventRecordTests : IAsyncLifetime
{
    private readonly ServerInstance server = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => server.DisposeAsync();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PagesFollowTheCursorThroughEveryEventOnceInOrder()
    {
        var client = server.Start();
        await AddTicksAsync(5);

        var seen = new List<EventView>();
        long? cursor = null;
        EventPage page;
        do
        {
            page = await GetPageAsync(client, $"limit=2{(cursor is null ? "" : $"&after={cursor}")}");
            Assert.InRange(page.Items.Count, 1, 2);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (page.HasMore);

        Assert.Equal(
            [EventTypes.ServerStarted, "test.tick", "test.tick", "test.tick", "test.tick", "test.tick"],
            seen.Select(e => e.Type));
        Assert.Equal(seen.Select(e => e.Id).Order(), seen.Select(e => e.Id));
        Assert.Equal(seen.Count, seen.Select(e => e.Id).Distinct().Count());

        // Nothing new: the cursor stays put so a caller can keep polling with it.
        var empty = await GetPageAsync(client, $"after={cursor}");
        Assert.Empty(empty.Items);
        Assert.Equal(cursor, empty.NextCursor);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public async Task PollingWithTheCursorNeverSkipsAnEventCommittedLate()
    {
        var client = server.Start();
        var cursor = (await GetPageAsync(client, "")).NextCursor;

        // A appends inside a transaction it has not committed yet; B appends meanwhile.
        await using var dbA = server.CreateDbContext();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Cancel);
        await new EventStore(dbA, TimeProvider.System).AppendAsync("test.a", "test", null, Cancel);
        var appendB = Task.Run(
            async () =>
            {
                await using var dbB = server.CreateDbContext();
                await new EventStore(dbB, TimeProvider.System).AppendAsync("test.b", "test", null, Cancel);
            },
            Cancel);
        // Long enough for B to commit, if nothing holds it back.
        await Task.WhenAny(appendB, Task.Delay(TimeSpan.FromSeconds(2), Cancel));

        var first = await GetPageAsync(client, $"after={cursor}");
        await transactionA.CommitAsync(Cancel);
        await appendB;
        var second = await GetPageAsync(client, $"after={first.NextCursor}");

        Assert.Equal(["test.a", "test.b"], first.Items.Concat(second.Items).Select(e => e.Type));
    }

    [Fact]
    public async Task SinceAndUntilSelectATimeWindow()
    {
        var client = server.Start();
        var first = await AddTicksAsync(5);

        var since = Uri.EscapeDataString(first.AddMinutes(1).ToString("O"));
        var until = Uri.EscapeDataString(first.AddMinutes(3).ToString("O"));
        var page = await GetPageAsync(client, $"since={since}&until={until}");

        Assert.Equal([1, 2], page.Items.Select(e => e.Data!.Value.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task EventsSurviveARestartAndGoWithTheDatabase()
    {
        server.Start();
        await server.StopAsync();
        var client = server.Start();

        var afterRestart = await GetPageAsync(client, "");
        Assert.Equal(
            [EventTypes.ServerStarted, EventTypes.ServerStopped, EventTypes.ServerStarted],
            afterRestart.Items.Select(e => e.Type));
        Assert.All(afterRestart.Items, e => Assert.Equal(EventSources.Server, e.Source));
        Assert.Equal(server.Instance, afterRestart.Items[0].Data!.Value.GetProperty("instance").GetString());

        await server.StopAsync();
        await server.DropDatabaseAsync();
        client = server.Start();

        var afterDrop = await GetPageAsync(client, "");
        Assert.Equal([EventTypes.ServerStarted], afterDrop.Items.Select(e => e.Type));
    }

    [Fact]
    public async Task TheDatabaseAcceptsOnlyLockedAppends()
    {
        server.Start();
        await using var db = server.CreateDbContext();
        var count = await db.Events.CountAsync(Cancel);

        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Events.ExecuteDeleteAsync(Cancel));
        var update = await Assert.ThrowsAsync<SqlException>(
            () => db.Events.ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, "changed"), Cancel));
        var tracked = await db.Events.FirstAsync(Cancel);
        db.Entry(tracked).Property(e => e.Type).CurrentValue = "changed";
        var save = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        await using var other = server.CreateDbContext();
        other.Events.Add(new EventRecord { Type = "test.unlocked", Source = "test" });
        var unlocked = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Cancel));

        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);
        Assert.Contains("append-only", save.InnerException?.Message, StringComparison.Ordinal);
        Assert.Contains(SimpleDbContext.EventsAppendLock, unlocked.InnerException?.Message, StringComparison.Ordinal);
        await using var fresh = server.CreateDbContext();
        Assert.Equal(count, await fresh.Events.CountAsync(e => e.Type != "changed", Cancel));
    }

    /// <summary>Adds events "test.tick" n = 0..count-1, one minute apart; returns the time of n = 0.</summary>
    private async Task<DateTimeOffset> AddTicksAsync(int count)
    {
        var first = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var db = server.CreateDbContext();
        var store = new EventStore(db, TimeProvider.System);
        for (var n = 0; n < count; n++)
        {
            await store.AppendAsync(
                new EventRecord
                {
                    OccurredAt = first.AddMinutes(n),
                    Type = "test.tick",
                    Source = "test",
                    Data = $$"""{"n":{{n}}}""",
                },
                Cancel);
        }

        return first;
    }

    private static async Task<EventPage> GetPageAsync(HttpClient client, string query)
    {
        var page = await client.GetFromJsonAsync<EventPage>(
            new Uri($"/api/v1/events?{query}", UriKind.Relative), Cancel);
        Assert.NotNull(page);
        return page;
    }
}
