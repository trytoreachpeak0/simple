using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Simple.MesGateway.Fake;
using Simple.MesGateway.Rounds;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

/// <summary>The gateway in process, on its shipped settings with short waits, driven through its control API.</summary>
public sealed class GatewayApiTests : IAsyncLifetime
{
    private readonly Factory factory = new();
    private HttpClient client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        client = factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task ReadyAfterTheFirstRoundWithTheSeedRowsInTheSnapshot()
    {
        var snapshot = await NextSnapshotAsync(s => s.Round >= 1);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancel)).StatusCode);
        Assert.True(snapshot.Succeeded);
        Assert.Null(snapshot.Failure);
        Assert.NotNull(snapshot.ReadAt);
        Assert.Equal(FakeMesSource.ReadSeed(Path.Combine(AppContext.BaseDirectory, "Fake", "seed-rows.json")), snapshot.Rows);
    }

    [Fact]
    public async Task AnAddedRowIsInTheNextRoundAndARemovedOneIsGone()
    {
        var row = new MesRow("SAMPLE_TO_GATE", "SMP0100.01", "S2-2", "SMP-WB-02", "SMP-GATE", "2026-01-06T10:00:00", "SMP-PKG-B");

        var added = await client.PostAsJsonAsync(new Uri("/fake-mes/rows", UriKind.Relative), row, Cancel);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        await NextSnapshotAsync(s => s.Rows?.Contains(row) == true);

        var removed = await client.DeleteAsync(new Uri("/fake-mes/rows?taskType=SAMPLE_TO_GATE&sublot=SMP0100.01", UriKind.Relative), Cancel);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        await NextSnapshotAsync(s => s.Rows is { } rows && !rows.Contains(row));
    }

    [Fact]
    public async Task RemovingAnUnknownKeyIsNotFound()
    {
        var response = await client.DeleteAsync(new Uri("/fake-mes/rows?taskType=SAMPLE_TO_GATE&sublot=NONE", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No row has TASK_TYPE 'SAMPLE_TO_GATE' and SUBLOT 'NONE'.", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedReadsShowInTheSnapshotUntilTheSourceRecovers()
    {
        await NextSnapshotAsync(s => s.Succeeded);

        var told = await client.PostAsJsonAsync(new Uri("/fake-mes/failures", UriKind.Relative), new FailRequest(3, "ORA-03135: connection lost contact"), Cancel);
        Assert.Equal(HttpStatusCode.OK, told.StatusCode);

        var failed = await NextSnapshotAsync(s => !s.Succeeded);
        Assert.Equal(new MesReadFailure(MesReadFailure.Read, "ORA-03135: connection lost contact"), failed.Failure);
        Assert.Null(failed.Rows);
        var recovered = await NextSnapshotAsync(s => s.Succeeded && s.Round > failed.Round);
        Assert.NotEmpty(recovered.Rows!);
    }

    [Fact]
    public async Task AMalformedReadIsAFormatFailure()
    {
        await NextSnapshotAsync(s => s.Succeeded);

        await client.PostAsJsonAsync(new Uri("/fake-mes/malformed", UriKind.Relative), new MalformedRequest(1), Cancel);

        var failed = await NextSnapshotAsync(s => !s.Succeeded);
        Assert.Equal(MesReadFailure.Format, failed.Failure!.Kind);
        Assert.Matches("^Row [0-9]+ has 6 values for 7 columns\\.$", failed.Failure.Message);
    }

    [Theory]
    [InlineData("/fake-mes/failures")]
    [InlineData("/fake-mes/malformed")]
    public async Task ANegativeCountIsRefused(string path)
    {
        var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), new { count = -1 }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("count must be 0 or more.", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetBringsBackTheSeed()
    {
        await client.PostAsJsonAsync(new Uri("/fake-mes/rows", UriKind.Relative), new MesRow("X", "Y", null, null, null, null, null), Cancel);
        await client.PostAsJsonAsync(new Uri("/fake-mes/failures", UriKind.Relative), new FailRequest(100, null), Cancel);

        var reset = await client.PostAsync(new Uri("/fake-mes/reset", UriKind.Relative), null, Cancel);
        var state = await reset.Content.ReadFromJsonAsync<FakeMesState>(Cancel);

        Assert.Equal(FakeMesSource.ReadSeed(Path.Combine(AppContext.BaseDirectory, "Fake", "seed-rows.json")), state!.Rows);
        Assert.Equal(0, state.FailuresPending);
    }

    /// <summary>Reads the snapshot until it satisfies the condition, within 10 s (rounds here are 100 ms apart).</summary>
    private async Task<MesSnapshot> NextSnapshotAsync(Func<MesSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var snapshot = (await client.GetFromJsonAsync<MesSnapshot>(new Uri("/api/v1/snapshot", UriKind.Relative), Cancel))!;
            if (condition(snapshot))
            {
                return snapshot;
            }

            Assert.True(DateTime.UtcNow < deadline, $"No such snapshot within 10 s; the last was round {snapshot.Round}, succeeded {snapshot.Succeeded}.");
            await Task.Delay(20, Cancel);
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simple:Mes:RoundInterval"] = "00:00:00.1",
                ["Simple:Mes:RetryDelay"] = "00:00:00.05",
                ["Simple:Mes:RetryMaxDelay"] = "00:00:00.2",
            }));
    }
}
