using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Fake;
using Simple.MesGateway.Rounds;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

/// <summary>The gateway in process, on its shipped settings with short waits, driven through its control API.</summary>
public sealed class GatewayApiTests : IAsyncLifetime
{
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), "simple-gateway-tests", Guid.NewGuid().ToString("N"));
    private readonly Factory factory;
    private HttpClient client = null!;

    public GatewayApiTests()
    {
        factory = new Factory(logDirectory);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A count of reads no test outlives: every read fails until the test says otherwise.</summary>
    private const int Persistent = 1_000_000;

    public ValueTask InitializeAsync()
    {
        client = factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        try
        {
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file just closed can still be held for a moment
            // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
        }
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

        await TellAsync("/fake-mes/failures", new FailRequest(Persistent, "ORA-03135: connection lost contact"), state => state.FailuresPending > 0);

        var failed = await NextSnapshotAsync(s => !s.Succeeded);
        // Every round before the injection succeeded (waited for above), and the
        // failure says what was injected: that is what ties it to the injection.
        Assert.Equal(new MesReadFailure(MesReadFailure.Read, "ORA-03135: connection lost contact"), failed.Failure);
        Assert.Null(failed.Rows);

        await TellAsync("/fake-mes/failures", new FailRequest(0, null), state => state.FailuresPending == 0);
        var recovered = await NextSnapshotAsync(s => s.Succeeded && s.Round > failed.Round);
        Assert.NotEmpty(recovered.Rows!);
    }

    [Fact]
    public async Task AFailedRoundIsInTheProgramLogWithoutTheCredentialItCarried()
    {
        await NextSnapshotAsync(s => s.Succeeded);

        await TellAsync(
            "/fake-mes/failures",
            new FailRequest(Persistent, "ORA-01017: invalid username/password for Data Source=mes01;User Id=simple;Password=Gateway-secret"),
            state => state.FailuresPending > 0);
        await NextSnapshotAsync(s => !s.Succeeded);
        await factory.DisposeAsync();

        var file = Assert.Single(Simple.Logging.ProgramLog.Files(logDirectory));
        Assert.Equal("MesGateway.log", Path.GetFileName(file));
        // In process, the host is released by both the factory and Program; when the factory returns,
        // Program's side may not have released the program log yet, so it is read the way simple logs reads.
        var text = Simple.Logging.ProgramLog.ReadText(logDirectory);
        Assert.DoesNotContain("Gateway-secret", text, StringComparison.Ordinal);
        Assert.Contains("failed (read), retrying with backoff: ORA-01017: invalid username/password for Data Source=mes01;User Id=simple;Password=***", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedReadIsAFormatFailure()
    {
        await NextSnapshotAsync(s => s.Succeeded);

        await TellAsync("/fake-mes/malformed", new MalformedRequest(Persistent), state => state.MalformedPending > 0);

        var failed = await NextSnapshotAsync(s => !s.Succeeded);
        // Every round before the injection succeeded (waited for above), and the
        // failure says what was injected: that is what ties it to the injection.
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
        // Both must take, or the reset below would have nothing to undo.
        var added = new MesRow("X", "Y", null, null, null, null, null);
        await TellAsync("/fake-mes/rows", added, state => state.Rows.Contains(added));
        await TellAsync("/fake-mes/failures", new FailRequest(100, null), state => state.FailuresPending > 0);

        var reset = await client.PostAsync(new Uri("/fake-mes/reset", UriKind.Relative), null, Cancel);
        var state = await reset.Content.ReadFromJsonAsync<FakeMesState>(Cancel);

        Assert.Equal(FakeMesSource.ReadSeed(Path.Combine(AppContext.BaseDirectory, "Fake", "seed-rows.json")), state!.Rows);
        Assert.Equal(0, state.FailuresPending);
    }

    [Fact]
    public async Task WithAnotherSourceThereIsNoFakeAndNoControlApi()
    {
        await using var other = new OtherSourceFactory(logDirectory + "-other");
        using var otherClient = other.CreateClient();

        var control = await otherClient.GetAsync(new Uri("/fake-mes/", UriKind.Relative), Cancel);
        var add = await otherClient.PostAsJsonAsync(new Uri("/fake-mes/rows", UriKind.Relative), new MesRow("X", "Y", null, null, null, null, null), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, control.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, add.StatusCode);
        Assert.Null(other.Services.GetService<FakeMesSource>());
        // The snapshot comes from that other source.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        MesSnapshot snapshot;
        do
        {
            snapshot = (await otherClient.GetFromJsonAsync<MesSnapshot>(new Uri("/api/v1/snapshot", UriKind.Relative), Cancel))!;
            Assert.True(DateTime.UtcNow < deadline, "No round from the other source within 10 s.");
        }
        while (!snapshot.Succeeded);

        Assert.Equal(OtherSourceFactory.Row, Assert.Single(snapshot.Rows!));
    }

    /// <summary>
    /// Tells the fake MES what to do and checks that it took. A failure told
    /// for one read shows in the snapshot only until the retry 50 ms or so
    /// later succeeds, which a slow poll misses (#68); the tests therefore
    /// make every read fail (<see cref="Persistent"/>) and end it themselves.
    /// </summary>
    private async Task TellAsync(string path, object request, Func<FakeMesState, bool> took)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), request, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<FakeMesState>(Cancel);
        Assert.True(state is not null && took(state), $"The fake MES did not take {path}: {await response.Content.ReadAsStringAsync(Cancel)}");
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

    /// <summary>
    /// The gateway with a source other than the fake, as a real one will be.
    /// The validator still knows only the fake, so it is taken out here.
    /// </summary>
    private sealed class OtherSourceFactory(string logDirectory) : WebApplicationFactory<Program>
    {
        public static readonly MesRow Row = new("OTHER", "O1", "S9-9", "E", "ST", "2026-01-01T00:00:00", "P");

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder
                .UseSetting("Simple:Logs:Directory", logDirectory)
                .UseSetting("Simple:Mes:Source", "Other")
                .UseSetting("Simple:Mes:RoundInterval", "00:00:00.1")
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<IValidateOptions<GatewaySettings>>();
                    services.AddSingleton<IMesSource, OtherSource>();
                });

        private sealed class OtherSource : IMesSource
        {
            public Task<MesReadResult> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromResult(new MesReadResult(MesColumns.All, [new object?[] { Row.TaskType, Row.Sublot, Row.Area, Row.Eqp, Row.Step, Row.Dates, Row.Package }]));
        }
    }

    private sealed class Factory(string logDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simple:Logs:Directory"] = logDirectory,
                ["Simple:Mes:RoundInterval"] = "00:00:00.1",
                ["Simple:Mes:RetryDelay"] = "00:00:00.05",
                ["Simple:Mes:RetryMaxDelay"] = "00:00:00.2",
            }));
    }
}
