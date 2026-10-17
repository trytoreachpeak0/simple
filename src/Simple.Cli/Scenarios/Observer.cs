using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Simple.Cli.Scenarios;

/// <summary>
/// Looks at the system through its one observation surface, the server's
/// API: GET /api/v1/status, and GET /api/v1/events from where the last look
/// stopped. Every new entry of the event record goes to the timeline.
/// </summary>
public sealed class Observer(HttpClient http, Uri server, Evidence evidence)
{
    private readonly List<RecordedEvent> events = [];
    private long? cursor;
    private string? lastStatusError;
    private string? lastEventsError;

    /// <summary>Every entry seen so far, oldest first.</summary>
    public IReadOnlyList<RecordedEvent> Events => events;

    public async Task<Snapshot> LookAsync(CancellationToken cancellationToken)
    {
        JsonElement? status = null;
        string? statusError = null;
        try
        {
            status = await http.GetFromJsonAsync<JsonElement>(new Uri(server, "/api/v1/status"), cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
            || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            statusError = e.Message;
        }

        string? eventsError = null;
        try
        {
            while (true)
            {
                var query = "limit=1000" + (cursor is { } after ? $"&after={after.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
                var page = await http.GetFromJsonAsync<JsonElement>(new Uri(server, $"/api/v1/events?{query}"), cancellationToken);
                foreach (var item in page.GetProperty("items").EnumerateArray())
                {
                    var recorded = new RecordedEvent(
                        item.GetProperty("id").GetInt64(),
                        item.GetProperty("occurredAt").GetDateTimeOffset(),
                        item.GetProperty("type").GetString()!,
                        item.GetProperty("source").GetString()!,
                        item.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ? data.Clone() : null,
                        item.Clone());
                    events.Add(recorded);
                    cursor = recorded.Id;
                    evidence.Write("event", new { Event = recorded.Raw });
                }

                if (!page.GetProperty("hasMore").GetBoolean())
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            eventsError = e.Message;
        }

        // Only changes go to the timeline: when a look first fails, and when it works again.
        if (statusError != lastStatusError)
        {
            evidence.Write(statusError is null ? "status-readable" : "status-unreadable", new { Error = statusError });
            lastStatusError = statusError;
        }

        if (eventsError != lastEventsError)
        {
            evidence.Write(eventsError is null ? "events-readable" : "events-unreadable", new { Error = eventsError });
            lastEventsError = eventsError;
        }

        return new Snapshot(DateTimeOffset.UtcNow, status, statusError, eventsError is null ? [.. events] : null, eventsError);
    }
}
