using System.Diagnostics;
using Serilog;

namespace Simple.ProtocolProbe.Server;

/// <summary>Commands typed on the server's standard input (or piped in by the
/// loopback runner). Every command and its outcome goes to the log.</summary>
internal static class OperatorConsole
{
    private const string Help =
        "list | confirm <pairing-code> <vehicle-id> | revoke <vehicle-id|key-prefix> | forget <vehicle-id|key-prefix> | quiet on|off | hint on|off | stats | quit";

    public static async Task RunAsync(TerminalHub hub, IHostApplicationLifetime lifetime)
    {
        Log.Information("CONSOLE ready: {Help}", Help);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            Log.Information("CONSOLE> {Line}", line);
            try
            {
                if (!Execute(hub, lifetime, parts))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CONSOLE command failed; the console keeps running");
            }
        }
    }

    private static bool Execute(TerminalHub hub, IHostApplicationLifetime lifetime, string[] parts)
    {
        switch (parts)
        {
            case ["list"]:
                hub.List();
                break;
            case ["confirm", var code, var vehicle]:
                hub.Confirm(code, vehicle);
                break;
            case ["revoke", var text]:
                hub.Revoke(text);
                break;
            case ["forget", var text]:
                hub.Forget(text);
                break;
            case ["quiet", var onOff]:
                hub.Quiet = onOff == "on";
                Log.Information("QUIET {State}: commands {What}", onOff, hub.Quiet ? "paused" : "resumed");
                break;
            case ["hint", var onOff]:
                hub.Hint = onOff == "on";
                Log.Information("HINT {State}: downstream messages {What} field 50", onOff, hub.Hint ? "carry" : "omit");
                break;
            case ["stats"]:
                hub.Stats();
                break;
            case ["quit"]:
                lifetime.StopApplication();
                return false;
            default:
                Log.Warning("CONSOLE unknown command; use {Help}", Help);
                break;
        }

        return true;
    }
}

/// <summary>Working set, private bytes, managed heap and CPU once a minute.</summary>
internal static class ResourceLog
{
    public static async Task RunAsync(string who, Func<int> sessions, CancellationToken token)
    {
        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        var lastCpu = process.TotalProcessorTime;
        var lastAt = Stopwatch.GetTimestamp();
        try
        {
            do
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var cpuPercent = (cpu - lastCpu).TotalMilliseconds / Stopwatch.GetElapsedTime(lastAt).TotalMilliseconds / Environment.ProcessorCount * 100;
                lastCpu = cpu;
                lastAt = Stopwatch.GetTimestamp();
                Log.Information("RESOURCE {Who} workingSetMB={Ws:F1} privateMB={Private:F1} gcHeapMB={Heap:F1} cpuPercent={Cpu:F2} cpuTotalS={CpuTotal:F1} threads={Threads} sessions={Sessions}",
                    who, process.WorkingSet64 / 1048576.0, process.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                    cpuPercent, cpu.TotalSeconds, process.Threads.Count, sessions());
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
