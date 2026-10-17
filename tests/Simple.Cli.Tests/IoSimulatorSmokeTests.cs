using System.Net.Http.Json;
using System.Text.Json;
using Simple.Cli.Instances;

namespace Simple.Cli.Tests;

/// <summary>
/// A local system with two onboard terminals, each paired with its own
/// simulated IO module (#70): the server sees every terminal's 16 inputs as
/// its simulator has them, a light curtain pinned through the control API
/// reaches the server within 2 s with its change counted, and a simulator
/// that stops answering makes its terminal report the IO module unreadable
/// within 10 s, which the server records.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class IoSimulatorSmokeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Timeout = 600_000)]
    public async Task EachTerminalReportsItsSimulatorsRawValuesAndAnUnansweringOneIsRecorded()
    {
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();
        using var http = new HttpClient();

        var up = await cli.RunJsonAsync("up", "--instance", instance, "--terminals", "2");
        Assert.True(up.ExitCode == ExitCodes.Success, $"up exited with {up.ExitCode}: {up.Output}{Environment.NewLine}{up.Error}");
        var processes = up.Output.GetProperty("result").GetProperty("processes").EnumerateArray().ToList();
        var names = processes.Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["io-simulator-1", "io-simulator-2"], names.Where(n => n!.StartsWith("io-simulator", StringComparison.Ordinal)));
        var simulators = new[] { Url(processes, "io-simulator-1"), Url(processes, "io-simulator-2") };
        // The server's status is read directly, logged in as the instance's
        // administrator (ADR 0045), not through simple status, so that the
        // 2 s below are the system's and not the command line's.
        await using var status = (await ServerApi.LogInAsync(
            http, new Uri(Url(processes, "server")), new InstanceStore(cli.Home).ReadAdminPassword(instance)!, Cancel))!;

        try
        {
            // Every terminal's 16 inputs as its own simulator has them. Slot 3
            // of the second simulator is pinned first, so the pairing shows.
            await PutAsync(http, simulators[1], "/api/v1/slots/3/lock-feedback", new { value = 0 });
            for (var i = 0; i < 2; i++)
            {
                var expected = await InputsAsync(http, simulators[i]);
                var vehicle = $"vehicle-{i + 1}";
                var terminal = await UntilTerminalAsync(status, vehicle, t => ReportedInputs(t).SequenceEqual(expected), TimeSpan.FromSeconds(10));
                Assert.True(terminal.GetProperty("ioModuleReadable").GetBoolean());
            }

            Assert.Equal(0, (await InputsAsync(http, simulators[1]))[2]);
            Assert.Equal(1, (await InputsAsync(http, simulators[0]))[2]);

            // Slot 4's light curtain (DI12) of the first simulator reads cargo:
            // the server sees it within 2 s, counted as one change.
            await PutAsync(http, simulators[0], "/api/v1/slots/4/light-curtain", new { value = 0 });
            var changed = await UntilTerminalAsync(status, "vehicle-1", t => Input(t, 12).GetProperty("value").GetInt32() == 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1UL, Input(changed, 12).GetProperty("changes").GetUInt64());

            // The second simulator stops answering: within 10 s its terminal
            // reports the IO module unreadable and the server records it.
            var stoppedAt = DateTimeOffset.UtcNow;
            await PutAsync(http, simulators[1], "/api/v1/modbus-fault", new { mode = "no-response" });
            await UntilTerminalAsync(status, "vehicle-2", t => !t.GetProperty("ioModuleReadable").GetBoolean(), TimeSpan.FromSeconds(10));
            var unreadable = await UntilEventAsync(
                cli,
                instance,
                e => e.GetProperty("type").GetString() == "onboard.io-unreadable" && e.GetProperty("data").GetProperty("vehicleId").GetString() == "vehicle-2",
                TimeSpan.FromSeconds(5));
            Assert.InRange(unreadable.GetProperty("occurredAt").GetDateTimeOffset() - stoppedAt, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10));
            var vehicle1 = await UntilTerminalAsync(status, "vehicle-1", _ => true, TimeSpan.FromSeconds(1));
            Assert.True(vehicle1.GetProperty("ioModuleReadable").GetBoolean());

            var text = await cli.RunAsync("status", "--instance", instance);
            Assert.Contains("IO module NOT READABLE", text.Output, StringComparison.Ordinal);
            var log = await cli.RunAsync("logs", "io-simulator-1", "--instance", instance);
            Assert.Contains("Simple.Logging.ProgramLog: IoSimulator started (version ", log.Output, StringComparison.Ordinal);
        }
        finally
        {
            var down = await cli.RunJsonAsync("down", "--reset", "--instance", instance);
            Assert.True(down.ExitCode == ExitCodes.Success, $"down exited with {down.ExitCode}: {down.Output}{Environment.NewLine}{down.Error}");
        }
    }

    private static string Url(List<JsonElement> processes, string name) =>
        processes.Single(p => p.GetProperty("name").GetString() == name).GetProperty("url").GetString()!;

    private static async Task PutAsync(HttpClient http, string simulator, string path, object body)
    {
        using var response = await http.PutAsJsonAsync(new Uri(new Uri(simulator), path), body, Cancel);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
    }

    private static async Task<int[]> InputsAsync(HttpClient http, string simulator)
    {
        var state = await http.GetFromJsonAsync<JsonElement>(new Uri(new Uri(simulator), "/api/v1/state"), Cancel);
        return [.. state.GetProperty("inputs").EnumerateArray().Select(e => e.GetInt32())];
    }

    private static int[] ReportedInputs(JsonElement terminal) =>
        [.. terminal.GetProperty("io").GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("value").GetInt32())];

    private static JsonElement Input(JsonElement terminal, int number) =>
        terminal.GetProperty("io").GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("number").GetInt32() == number);

    private static async Task<JsonElement> UntilTerminalAsync(ServerApi status, string vehicle, Func<JsonElement, bool> match, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var server = await status.GetJsonAsync("/api/v1/status", Cancel);
            var terminal = server.GetProperty("onboardTerminals").EnumerateArray()
                .Where(t => t.GetProperty("vehicleId").GetString() == vehicle)
                .ToList();
            if (terminal.Count == 1 && match(terminal[0]))
            {
                return terminal[0];
            }

            Assert.True(DateTime.UtcNow < deadline, $"{vehicle} not so within {wait.TotalSeconds} s: {server}");
            await Task.Delay(100, Cancel);
        }
    }

    private static async Task<JsonElement> UntilEventAsync(CliRunner cli, string instance, Func<JsonElement, bool> match, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var events = await cli.RunJsonAsync("events", "--instance", instance);
            var found = events.Output.GetProperty("result").GetProperty("events").EnumerateArray().Where(match).ToList();
            if (found.Count > 0)
            {
                return found[0];
            }

            Assert.True(DateTime.UtcNow < deadline, $"No such event within {wait.TotalSeconds} s: {events.Output}");
            await Task.Delay(250, Cancel);
        }
    }
}
