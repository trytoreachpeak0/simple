using System.Diagnostics;
using System.Text.Json;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// Brings real systems up and down through the built command line: two
/// instances at once, as two worktrees would, each with its own processes,
/// ports and database.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class SmokeTests
{
    [Fact(Timeout = 600_000)]
    public async Task TwoInstancesComeUpAndGoDownIndependently()
    {
        await using var cli = new CliRunner(TestContext.Current.CancellationToken);
        var a = cli.NewInstance();
        var b = cli.NewInstance();

        var ups = await Task.WhenAll(cli.RunJsonAsync("up", "--instance", a), cli.RunJsonAsync("up", "--instance", b));
        foreach (var (exitCode, output, error) in ups)
        {
            Assert.True(exitCode == ExitCodes.Success, $"up exited with {exitCode}: {output}{Environment.NewLine}{error}");
        }

        var serverA = Server(ups[0].Output);
        var serverB = Server(ups[1].Output);
        Assert.NotEqual(serverA.Pid, serverB.Pid);
        Assert.NotEqual(serverA.Url, serverB.Url);
        var startedA = StartTime(serverA.Pid);

        // up again: nothing new is started.
        var again = await cli.RunJsonAsync("up", "--instance", a);
        Assert.Equal(ExitCodes.Success, again.ExitCode);
        Assert.True(again.Output.GetProperty("result").GetProperty("alreadyRunning").GetBoolean());
        Assert.Equal(serverA.Pid, Server(again.Output).Pid);

        var status = await cli.RunJsonAsync("status", "--instance", a);
        Assert.Equal(ExitCodes.Success, status.ExitCode);
        var server = status.Output.GetProperty("result").GetProperty("server");
        Assert.Equal(a, server.GetProperty("instance").GetString());
        Assert.Equal($"Simple_{a}", server.GetProperty("database").GetString());

        var events = await cli.RunJsonAsync("events", "--instance", a);
        Assert.Equal(ExitCodes.Success, events.ExitCode);
        Assert.Contains(
            events.Output.GetProperty("result").GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "server.started"
                && e.GetProperty("data").GetProperty("processId").GetInt32() == serverA.Pid);

        var log = await cli.RunAsync("logs", "server", "--instance", a);
        Assert.Equal(ExitCodes.Success, log.ExitCode);
        Assert.Contains("Application started", log.Output, StringComparison.Ordinal);

        // down stops a gracefully: its process is gone and it recorded its stop.
        var down = await cli.RunJsonAsync("down", "--instance", a);
        Assert.True(down.ExitCode == ExitCodes.Success, $"down exited with {down.ExitCode}: {down.Output}{Environment.NewLine}{down.Error}");
        var stopped = Assert.Single(down.Output.GetProperty("result").GetProperty("stopped").EnumerateArray());
        Assert.Equal("stopped", stopped.GetProperty("outcome").GetString());
        using (var gone = ChildProcess.Find(serverA.Pid, startedA))
        {
            Assert.Null(gone);
        }

        Assert.Equal(1, await cli.CountEventsAsync(a, "server.stopped", serverA.Pid));
        Assert.Equal(ExitCodes.NotRunning, (await cli.RunJsonAsync("status", "--instance", a)).ExitCode);

        // b is untouched.
        var statusB = await cli.RunJsonAsync("status", "--instance", b);
        Assert.Equal(ExitCodes.Success, statusB.ExitCode);
        Assert.Equal(serverB.Pid, Server(statusB.Output).Pid);

        // --reset drops a's database and no other.
        Assert.True(await cli.DatabaseExistsAsync(a));
        var reset = await cli.RunJsonAsync("down", "--reset", "--instance", a);
        Assert.Equal(ExitCodes.Success, reset.ExitCode);
        Assert.True(reset.Output.GetProperty("result").GetProperty("databaseDropped").GetBoolean());
        Assert.False(await cli.DatabaseExistsAsync(a));
        Assert.True(await cli.DatabaseExistsAsync(b));

        var downB = await cli.RunJsonAsync("down", "--reset", "--instance", b);
        Assert.Equal(ExitCodes.Success, downB.ExitCode);
        Assert.False(await cli.DatabaseExistsAsync(b));
    }

    private static (int Pid, string Url) Server(JsonElement output)
    {
        var server = output.GetProperty("result").GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "server");
        return (server.GetProperty("pid").GetInt32(), server.GetProperty("url").GetString()!);
    }

    private static DateTimeOffset StartTime(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
    }
}
