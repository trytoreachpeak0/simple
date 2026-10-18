using System.Diagnostics;
using System.Text.Json;
using Simple.Cli.Instances;
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
        var startedA = StartTime(cli, a, serverA.Pid);

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

        // The command line logged in with the administrator password up made,
        // kept outside the clone (ADR 0045); without a login the server answers nothing.
        var store = new InstanceStore(cli.Home);
        Assert.True(File.Exists(store.AdminPasswordPath(a)));
        Assert.False(store.AdminPasswordPath(a).StartsWith(cli.Repository, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(store.ReadAdminPassword(a), store.ReadAdminPassword(b));
        using (var http = new HttpClient())
        using (var anonymous = await http.GetAsync(new Uri(new Uri(serverA.Url), "/api/v1/status"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // The server reaches the fake fleet manager up started (it checks every 10 s).
        var fakeRiot = Process(ups[0].Output, "fake-riot");
        var riot = server.GetProperty("riot");
        for (var tries = 0; riot.GetProperty("state").GetString() != "connected" && tries < 30; tries++)
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            riot = (await cli.RunJsonAsync("status", "--instance", a)).Output.GetProperty("result").GetProperty("server").GetProperty("riot");
        }

        Assert.Equal("connected", riot.GetProperty("state").GetString());
        Assert.Equal(new Uri(fakeRiot.Url), new Uri(riot.GetProperty("url").GetString()!));
        Assert.Contains($"RIoT connected at {new Uri(fakeRiot.Url)}", (await cli.RunAsync("status", "--instance", a)).Output, StringComparison.Ordinal);

        var events = await cli.RunJsonAsync("events", "--instance", a);
        Assert.Equal(ExitCodes.Success, events.ExitCode);
        Assert.Contains(
            events.Output.GetProperty("result").GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "server.started"
                && e.GetProperty("data").GetProperty("processId").GetInt32() == serverA.Pid);

        // Each process writes its own program log (ADR 0034), beginning with a line saying it started.
        var log = await cli.RunAsync("logs", "server", "--instance", a);
        Assert.Equal(ExitCodes.Success, log.ExitCode);
        Assert.Contains("Simple.Logging.ProgramLog: Server started (version ", log.Output, StringComparison.Ordinal);
        Assert.Contains($", process {serverA.Pid})", log.Output, StringComparison.Ordinal);
        Assert.Contains("Application started", log.Output, StringComparison.Ordinal);
        var gatewayLog = await cli.RunAsync("logs", "mes-gateway", "--instance", a);
        Assert.Equal(ExitCodes.Success, gatewayLog.ExitCode);
        Assert.Contains("Simple.Logging.ProgramLog: MesGateway started (version ", gatewayLog.Output, StringComparison.Ordinal);

        // down stops a gracefully: its processes are gone and the server recorded its stop.
        var gatewayA = Process(ups[0].Output, "mes-gateway");
        var gatewayStartedA = StartTime(cli, a, gatewayA.Pid);
        var down = await cli.RunJsonAsync("down", "--instance", a);
        Assert.True(down.ExitCode == ExitCodes.Success, $"down exited with {down.ExitCode}: {down.Output}{Environment.NewLine}{down.Error}");
        var stopped = down.Output.GetProperty("result").GetProperty("stopped").EnumerateArray().ToList();
        Assert.Equal(["server", "mes-gateway", "fake-riot"], stopped.Select(s => s.GetProperty("name").GetString()));
        Assert.All(stopped, s => Assert.Equal("stopped", s.GetProperty("outcome").GetString()));
        using (var gone = ChildProcess.Find(serverA.Pid, startedA))
        {
            Assert.Null(gone);
        }

        using (var gone = ChildProcess.Find(gatewayA.Pid, gatewayStartedA))
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
        Assert.True(reset.ExitCode == ExitCodes.Success, $"down --reset exited with {reset.ExitCode}: {reset.Output}{Environment.NewLine}{reset.Error}");
        Assert.True(reset.Output.GetProperty("result").GetProperty("databaseDropped").GetBoolean());
        Assert.False(await cli.DatabaseExistsAsync(a));
        Assert.True(await cli.DatabaseExistsAsync(b));

        var downB = await cli.RunJsonAsync("down", "--reset", "--instance", b);
        Assert.True(downB.ExitCode == ExitCodes.Success, $"down --reset exited with {downB.ExitCode}: {downB.Output}{Environment.NewLine}{downB.Error}");
        Assert.False(await cli.DatabaseExistsAsync(b));
    }

    private static (int Pid, string Url) Server(JsonElement output) => Process(output, "server");

    internal static (int Pid, string Url) Process(JsonElement output, string name)
    {
        var process = output.GetProperty("result").GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
        return (process.GetProperty("pid").GetInt32(), process.GetProperty("url").GetString()!);
    }

    /// <summary>A process's start time as up recorded it, not read again by a pid that may by now be another process's (#85).</summary>
    internal static DateTimeOffset StartTime(CliRunner cli, string instance, int pid) =>
        new InstanceStore(cli.Home).Read(instance)!.Processes.Single(p => p.Pid == pid).ProcessStartedAt;
}
