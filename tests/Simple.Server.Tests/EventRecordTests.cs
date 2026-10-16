using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;

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
    public async Task TheDatabaseRefusesToChangeOrRemoveAnEvent()
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

        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);
        Assert.Contains("append-only", save.InnerException?.Message, StringComparison.Ordinal);
        await using var fresh = server.CreateDbContext();
        Assert.Equal(count, await fresh.Events.CountAsync(e => e.Type != "changed", Cancel));
    }

    /// <summary>Adds events "test.tick" n = 0..count-1, one minute apart; returns the time of n = 0.</summary>
    private async Task<DateTimeOffset> AddTicksAsync(int count)
    {
        var first = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var db = server.CreateDbContext();
        for (var n = 0; n < count; n++)
        {
            db.Events.Add(new EventRecord
            {
                OccurredAt = first.AddMinutes(n),
                Type = "test.tick",
                Source = "test",
                Data = $$"""{"n":{{n}}}""",
            });
        }

        await db.SaveChangesAsync(Cancel);
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
