using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Server.Events;
using Simple.Server.Status;

namespace Simple.Server.Tests;

/// <summary>One running server shared by tests that only read from it.</summary>
public sealed class RunningServer : IAsyncLifetime
{
    public ServerInstance Server { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Client = Server.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

public sealed class ServerApiTests(RunningServer running) : IClassFixture<RunningServer>
{
    private readonly HttpClient client = running.Client;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsReportHealthy(string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task StatusReportsVersionStartTimeInstanceAndItsParts()
    {
        var status = await client.GetFromJsonAsync<StatusResponse>(new Uri("/api/v1/status", UriKind.Relative), Cancel);

        Assert.NotNull(status);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
        Assert.InRange(status.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow);
        Assert.Equal(running.Server.Instance, status.Instance);
        Assert.Equal($"Simple_{running.Server.Instance}", status.Database);
        Assert.Empty(status.OnboardTerminals);
        Assert.Null(status.Riot);
        Assert.Equal(new Uri("http://127.0.0.1:9"), status.MesGateway.Url);
    }

    [Fact]
    public async Task StartupIsInTheEventRecord()
    {
        var page = await client.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);

        var started = Assert.Single(page!.Items, e => e.Type == EventTypes.ServerStarted);
        Assert.Equal(EventSources.Server, started.Source);
        Assert.Equal(running.Server.Instance, started.Data!.Value.GetProperty("instance").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EventStore.MaxLimit + 1)]
    public async Task AnEventLimitOutOfRangeIsRejected(int limit)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/events?limit={limit}", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("limit must be between 1 and", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiDocumentDescribesStatusAndEvents()
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Cancel));

        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/status", out _));
        Assert.True(paths.TryGetProperty("/api/v1/events", out _));
    }
}
