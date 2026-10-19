using System.Collections;
using System.Text.Json;
using Simple.Cli.Instances;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// ADR 0025's clone check in the simulated environment, as #18 did it on a
/// vehicle PC: a second terminal process on the same computer, with the same
/// settings file and so the same key, stands for a cloned disk running at the
/// same time as the original.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class TerminalCloneTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Timeout = 600_000)]
    public async Task TwoTerminalProcessesWithOneSettingsFileAreBothRefusedAndRecordedAsAClone()
    {
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var instance = cli.NewInstance();
        var up = await cli.RunJsonAsync("up", "--instance", instance, "--terminals", "1");
        Assert.True(up.ExitCode == ExitCodes.Success, $"up exited with {up.ExitCode}: {up.Output}{Environment.NewLine}{up.Error}");
        var store = new InstanceStore(cli.Home);
        var terminal = store.Read(instance)!.Processes.Single(p => p.Name == "terminal");
        var directory = store.ProcessDirectory(instance, "terminal");
        var status = await cli.RunJsonAsync("status", "--instance", instance);
        Assert.True(Vehicle(status.Output).GetProperty("connected").GetBoolean(), $"vehicle-1's terminal is not in use after up: {status.Output}");

        // The same program with the same settings file: the same key, another run.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string)entry.Value!;
        }

        var clone = ChildProcess.Start(
            terminal.Executable,
            [$"--config={Path.Combine(directory, "terminal.json")}", $"--Simple:Logs:Directory={Path.Combine(directory, "clone-logs")}"],
            Path.GetDirectoryName(terminal.Executable)!,
            environment,
            Path.Combine(directory, "clone-output.log"));
        try
        {
            var suspected = await UntilEventAsync(cli, instance, "onboard.clone-suspected", TimeSpan.FromSeconds(60));
            Assert.Equal("vehicle-1", suspected.GetProperty("data").GetProperty("vehicleId").GetString());

            // Neither is used now: the vehicle's terminal is gone, its key waits, marked.
            var after = await cli.RunJsonAsync("status", "--instance", instance);
            Assert.False(Vehicle(after.Output).GetProperty("connected").GetBoolean(), after.Output.ToString());
            var waiting = Assert.Single(after.Output.GetProperty("result").GetProperty("server").GetProperty("pendingOnboardTerminals").EnumerateArray());
            Assert.True(waiting.GetProperty("cloneSuspected").GetBoolean(), waiting.ToString());
        }
        finally
        {
            using var running = ChildProcess.Find(clone.Pid, clone.StartedAt);
            running?.Kill();
            if (running is not null)
            {
                await running.WaitForExitAsync(Cancel);
            }

            clone.Dispose();
        }

        var reset = await cli.RunJsonAsync("down", "--reset", "--instance", instance);
        Assert.True(reset.ExitCode == ExitCodes.Success, $"down --reset exited with {reset.ExitCode}: {reset.Output}{Environment.NewLine}{reset.Error}");
        Assert.False(ServerKey.Exists(Components.TerminalKeyName(instance, 1), TestKeyStore.Store));
    }

    private static JsonElement Vehicle(JsonElement status) =>
        status.GetProperty("result").GetProperty("server").GetProperty("onboardTerminals").EnumerateArray()
            .Single(t => t.GetProperty("vehicleId").GetString() == "vehicle-1");

    private static async Task<JsonElement> UntilEventAsync(CliRunner cli, string instance, string type, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var events = await cli.RunJsonAsync("events", "--instance", instance);
            var found = events.Output.GetProperty("result").GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() == type).ToList();
            if (found.Count > 0)
            {
                return found[0];
            }

            Assert.True(DateTime.UtcNow < deadline, $"No {type} within {wait.TotalSeconds} s: {events.Output}");
            await Task.Delay(500, Cancel);
        }
    }
}
