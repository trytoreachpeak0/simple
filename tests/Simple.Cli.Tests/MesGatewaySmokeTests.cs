using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Cli.Instances;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// The MES gateway and the server as real processes, brought up by the
/// command line: rows and failures injected into the fake MES through the
/// gateway's control API reach the server's status and event record.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class MesGatewaySmokeTests
{
    /// <summary>The gateway's default round interval (5 s) plus the server's poll interval (2 s), with margin.</summary>
    private static readonly TimeSpan OnePollCycle = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Timeout = 600_000)]
    public async Task RowsAndFailuresOfTheFakeMesReachTheServer()
    {
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();
        var up = await cli.RunJsonAsync("up", "--instance", instance);
        Assert.True(up.ExitCode == ExitCodes.Success, $"up exited with {up.ExitCode}: {up.Output}{Environment.NewLine}{up.Error}");
        var gateway = SmokeTests.Process(up.Output, "mes-gateway");
        var server = SmokeTests.Process(up.Output, "server");
        var gatewayStarted = SmokeTests.StartTime(cli, instance, gateway.Pid);
        var serverStarted = SmokeTests.StartTime(cli, instance, server.Pid);
        var gatewayUrl = new Uri(gateway.Url);
        await using var serverApi = (await ServerApi.LogInAsync(Http, new Uri(server.Url), new InstanceStore(cli.Home).ReadAdminPassword(instance)!, Cancel))!;

        // A row added through the control API is in the server's status within one poll cycle.
        var row = new { taskType = "SAMPLE_TO_OVEN", sublot = "SMP0500.01", area = "S1-5", eqp = "SMP-DA-05", step = "SMP-CURE", dates = "2026-01-07T07:00:00", package = "SMP-PKG-A" };
        (await Http.PostAsJsonAsync(new Uri(gatewayUrl, "/fake-mes/rows"), row, Cancel)).EnsureSuccessStatusCode();
        var added = Stopwatch.StartNew();
        await UntilAsync(OnePollCycle, "the added row in the server's status", async () =>
        {
            var mes = await MesStatusAsync(serverApi);
            return mes.GetProperty("rows").ValueKind == JsonValueKind.Array
                && mes.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("sublot").GetString() == "SMP0500.01");
        });
        Assert.True(added.Elapsed < OnePollCycle);

        var status = await cli.RunAsync("status", "--instance", instance);
        Assert.Equal(ExitCodes.Success, status.ExitCode);
        Assert.Matches(@"MES gateway reachable; round \d+ at [0-9:]+, 4 rows", status.Output);

        // Failing reads: the gateway reports them, the server records the MES unreachable, then recovered.
        const string failure = "ORA-03113: end-of-file on communication channel (smoke test)";
        // Only what follows the injection counts: whether the server's first
        // fetch came before the gateway was ready (an early unreachable and
        // recovered pair) depends on start-up timing, not on what is tested here.
        var before = (await MesEventsAsync(serverApi)).Select(e => e.GetProperty("id").GetInt64()).DefaultIfEmpty(0).Max();
        (await Http.PostAsJsonAsync(new Uri(gatewayUrl, "/fake-mes/failures"), new { count = 5, message = failure }, Cancel)).EnsureSuccessStatusCode();
        await UntilAsync(OnePollCycle, "a failed round in the gateway's snapshot", async () =>
        {
            var snapshot = await Http.GetFromJsonAsync<JsonElement>(new Uri(gatewayUrl, "/api/v1/snapshot"), Cancel);
            return !snapshot.GetProperty("succeeded").GetBoolean()
                && snapshot.GetProperty("failure").GetProperty("message").GetString() == failure;
        });
        JsonElement[] mesEvents = [];
        // Five failed reads with waits growing from 1 s take well under a minute.
        await UntilAsync(TimeSpan.FromSeconds(90), "mes.unreachable and then mes.recovered in the event record", async () =>
        {
            mesEvents = [.. (await MesEventsAsync(serverApi)).Where(e => e.GetProperty("id").GetInt64() > before)];
            return mesEvents.Length >= 2;
        });
        Assert.Equal(["mes.unreachable", "mes.recovered"], mesEvents.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("read", mesEvents[0].GetProperty("data").GetProperty("cause").GetString());
        Assert.Equal(failure, mesEvents[0].GetProperty("data").GetProperty("error").GetString());

        // down leaves no process behind.
        var down = await cli.RunJsonAsync("down", "--reset", "--instance", instance);
        Assert.True(down.ExitCode == ExitCodes.Success, $"down exited with {down.ExitCode}: {down.Output}{Environment.NewLine}{down.Error}");
        using (var gone = ChildProcess.Find(gateway.Pid, gatewayStarted))
        {
            Assert.Null(gone);
        }

        using (var gone = ChildProcess.Find(server.Pid, serverStarted))
        {
            Assert.Null(gone);
        }
    }

    private static async Task<JsonElement> MesStatusAsync(ServerApi server) =>
        (await server.GetJsonAsync("/api/v1/status", Cancel)).GetProperty("mesGateway");

    private static async Task<JsonElement[]> MesEventsAsync(ServerApi server)
    {
        var page = await server.GetJsonAsync("/api/v1/events?limit=1000", Cancel);
        return page.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString()!.StartsWith("mes.", StringComparison.Ordinal))
            .ToArray();
    }

    private static async Task UntilAsync(TimeSpan within, string what, Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(elapsed.Elapsed < within, $"Not seen within {within.TotalSeconds} s: {what}.");
            await Task.Delay(200, Cancel);
        }
    }
}
