using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// A local system with onboard terminals, brought up and down through the
/// built command line (#32): the terminals connect and report fresh, a scan
/// typed into one reaches the event record, a killed one is seen going stale,
/// and down leaves nothing running.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class TerminalSmokeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Timeout = 600_000)]
    public async Task ThreeTerminalsReportFreshAScanIsRecordedAndAKilledOneGoesStale()
    {
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();

        var up = await cli.RunJsonAsync("up", "--instance", instance, "--terminals", "3");
        Assert.True(up.ExitCode == ExitCodes.Success, $"up exited with {up.ExitCode}: {up.Output}{Environment.NewLine}{up.Error}");
        var processes = up.Output.GetProperty("result").GetProperty("processes").EnumerateArray().ToList();
        // Other components (the MES gateway) come up too; this test is about the server and its terminals.
        var names = processes.Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("server", names);
        Assert.Equal(["terminal-1", "terminal-2", "terminal-3"], names.Where(n => n!.StartsWith("terminal", StringComparison.Ordinal)));
        // Pid and start time: after down, a pid alone could match a later process.
        var started = processes.Select(p => p.GetProperty("pid").GetInt32()).ToDictionary(pid => pid, StartTime);

        // simple status: three terminals, each with a report at most 2 s old.
        var status = await cli.RunJsonAsync("status", "--instance", instance);
        Assert.Equal(ExitCodes.Success, status.ExitCode);
        var terminals = status.Output.GetProperty("result").GetProperty("server").GetProperty("onboardTerminals").EnumerateArray().ToList();
        Assert.Equal(["vehicle-1", "vehicle-2", "vehicle-3"], terminals.Select(t => t.GetProperty("vehicleId").GetString()));
        Assert.All(terminals, t =>
        {
            Assert.True(t.GetProperty("connected").GetBoolean());
            Assert.False(t.GetProperty("stale").GetBoolean());
            Assert.InRange(t.GetProperty("freshReportAgeSeconds").GetDouble(), 0, 2);
        });
        var text = await cli.RunAsync("status", "--instance", instance);
        Assert.Contains("Onboard terminals", text.Output, StringComparison.Ordinal);

        // Each terminal writes its own program log (ADR 0034).
        foreach (var terminal in new[] { "terminal-1", "terminal-2", "terminal-3" })
        {
            var log = await cli.RunAsync("logs", terminal, "--instance", instance);
            Assert.True(log.ExitCode == ExitCodes.Success, $"logs {terminal} exited with {log.ExitCode}: {log.Error}");
            Assert.Contains("Simple.Logging.ProgramLog: OnboardTerminal started (version ", log.Output, StringComparison.Ordinal);
        }

        // A scan, typed into terminal-1's test input the way a scanner types: ends with Enter.
        using var http = new HttpClient();
        var input = new Uri(new Uri(Url(processes, "terminal-1")), "/test/keyboard");
        using (var typed = await http.PostAsJsonAsync(input, new { Text = "LOT-SMOKE-0001\r" }, Cancel))
        {
            Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync(Cancel));
        }

        var scan = await UntilEventAsync(cli, instance, e => e.GetProperty("type").GetString() == "onboard.input-received", TimeSpan.FromSeconds(10));
        Assert.Equal("vehicle-1", scan.GetProperty("data").GetProperty("vehicleId").GetString());
        Assert.Equal("LOT-SMOKE-0001", scan.GetProperty("data").GetProperty("text").GetString());

        // terminal-2 is killed outright: within 10 s its reports are stale.
        var victim = Pid(processes, "terminal-2");
        using (var process = Process.GetProcessById(victim))
        {
            process.Kill();
            await process.WaitForExitAsync(Cancel);
        }

        var killedAt = DateTimeOffset.UtcNow;
        var stale = await UntilEventAsync(
            cli,
            instance,
            e => e.GetProperty("type").GetString() == "onboard.stale" && e.GetProperty("data").GetProperty("vehicleId").GetString() == "vehicle-2",
            TimeSpan.FromSeconds(15));
        Assert.InRange(stale.GetProperty("occurredAt").GetDateTimeOffset() - killedAt, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10));

        // down stops everything still running; the server key stays for the next up.
        var down = await cli.RunJsonAsync("down", "--instance", instance);
        Assert.True(down.ExitCode == ExitCodes.Success, $"down exited with {down.ExitCode}: {down.Output}{Environment.NewLine}{down.Error}");
        foreach (var (pid, startedAt) in started)
        {
            using var left = ChildProcess.Find(pid, startedAt);
            Assert.Null(left);
        }

        // In the store the tests were told to use, and not quietly in the other one.
        Assert.True(ServerKey.Exists(Components.ServerKeyName(instance), TestKeyStore.Store));
        Assert.False(ServerKey.Exists(Components.ServerKeyName(instance), TestKeyStore.Other));
        var reset = await cli.RunJsonAsync("down", "--reset", "--instance", instance);
        Assert.Equal(ExitCodes.Success, reset.ExitCode);
        Assert.True(reset.Output.GetProperty("result").GetProperty("serverKeyDeleted").GetBoolean());
        Assert.False(ServerKey.Exists(Components.ServerKeyName(instance), TestKeyStore.Store));
    }

    private static string Url(List<JsonElement> processes, string name) =>
        processes.Single(p => p.GetProperty("name").GetString() == name).GetProperty("url").GetString()!;

    private static int Pid(List<JsonElement> processes, string name) =>
        processes.Single(p => p.GetProperty("name").GetString() == name).GetProperty("pid").GetInt32();

    private static DateTimeOffset StartTime(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
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
