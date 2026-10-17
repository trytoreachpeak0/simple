using System.Globalization;
using System.Text.Json;

namespace Simple.Cli.Scenarios;

/// <summary>
/// Looks at the system through its one observation surface, the server's
/// API: GET /api/v1/status, and GET /api/v1/events from where the last look
/// stopped, logged in as the instance's administrator like simple status
/// (ADR 0045). Every new entry of the event record goes to the timeline.
/// </summary>
/// <param name="password">The instance's administrator password, which up keeps outside the clone.</param>
public sealed class Observer(HttpClient http, Uri server, string? password, Evidence evidence) : IAsyncDisposable
{
    private readonly List<RecordedEvent> events = [];
    private ServerApi? api;
    private long? cursor;
    private string? lastStatusError;
    private string? lastEventsError;

    /// <summary>Every entry seen so far, oldest first.</summary>
    public IReadOnlyList<RecordedEvent> Events => events;

    public async Task<Snapshot> LookAsync(CancellationToken cancellationToken)
    {
        JsonElement? status = null;
        string? statusError = null;
        DateTimeOffset at;
        try
        {
            status = await (await ApiAsync(cancellationToken)).GetJsonAsync("/api/v1/status", cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
            || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            statusError = e.Message;
            // Logged in again at the next look: the server may have restarted.
            api = null;
        }

        // The look is as of when the status was read: logging in and reading
        // the event record afterwards is not part of what it saw.
        at = DateTimeOffset.UtcNow;

        string? eventsError = null;
        try
        {
            while (true)
            {
                var query = "limit=1000" + (cursor is { } after ? $"&after={after.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
                var page = await (await ApiAsync(cancellationToken)).GetJsonAsync($"/api/v1/events?{query}", cancellationToken);
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
            api = null;
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

        return new Snapshot(at, status, statusError, eventsError is null ? [.. events] : null, eventsError);
    }

    /// <summary>Logs out the last login, so the run leaves none behind.</summary>
    public async ValueTask DisposeAsync()
    {
        if (api is not null)
        {
            await api.DisposeAsync();
            api = null;
        }
    }

    private async Task<ServerApi> ApiAsync(CancellationToken cancellationToken) =>
        api ??= password is null
            ? throw new HttpRequestException("the instance has no administrator password: it was not brought up by simple up")
            : await ServerApi.LogInAsync(http, server, password, cancellationToken)
                ?? throw new HttpRequestException("the server refused the instance's administrator password");
}
