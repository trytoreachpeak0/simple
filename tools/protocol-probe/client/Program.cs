using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Simple.ProtocolProbe.Client;

var parsed = ClientOptions.ParseArgs(args);
var dataDirectory = ClientOptions.DataDirectoryFrom(parsed);
Directory.CreateDirectory(dataDirectory);

if (parsed.ContainsKey("reset"))
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(outputTemplate: ClientOptions.LogTemplate, formatProvider: CultureInfo.InvariantCulture)
        .CreateLogger();
    TerminalIdentity.Reset(dataDirectory);
    await Log.CloseAndFlushAsync();
    return 0;
}

var options = ClientOptions.From(parsed);
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Grpc", LogEventLevel.Information)
    .MinimumLevel.Override("Grpc.Net.Client.Balancer", LogEventLevel.Debug)
    .WriteTo.File(options.LogFile, outputTemplate: ClientOptions.LogTemplate, formatProvider: CultureInfo.InvariantCulture,
        flushToDiskInterval: TimeSpan.FromSeconds(1));
if (options.ConsoleLog)
{
    loggerConfig = loggerConfig.WriteTo.Console(outputTemplate: ClientOptions.LogTemplate, formatProvider: CultureInfo.InvariantCulture);
}

Log.Logger = loggerConfig.CreateLogger();
try
{
    Log.Information("START client pid={Pid} os={Os} runtime={Runtime} arch={Arch} data={Data} log={LogFile}",
        Environment.ProcessId, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture, options.DataDirectory, options.LogFile);
    var identity = TerminalIdentity.LoadOrCreate(options.DataDirectory, options.KeyProvider, options.KeyAlgorithm);
    identity.Describe();

    using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
    using var client = new ProbeClient(options, identity, loggerFactory);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    _ = Task.Run(() => ReadCommandsAsync(client, stop));
    _ = Task.Run(() => ResourceLogAsync(stop.Token));
    await client.RunAsync(stop.Token);
    Log.Information("STOP client pid={Pid}", Environment.ProcessId);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "FATAL client stopped");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task ReadCommandsAsync(ProbeClient client, CancellationTokenSource stop)
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        Log.Information("CONSOLE> {Line}", line);
        switch (parts)
        {
            case ["interval", var seconds]:
                client.ReportInterval = TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture));
                Log.Information("INTERVAL now {Seconds}s", seconds);
                break;
            case ["extra", var onOff]:
                client.Extra = onOff == "on";
                break;
            case ["status"]:
                Log.Information("STATE {State}", client.State);
                break;
            case ["quit"]:
                await stop.CancelAsync();
                return;
            default:
                Log.Warning("CONSOLE unknown command; use interval <s> | extra on|off | status | quit");
                break;
        }
    }
}

static async Task ResourceLogAsync(CancellationToken token)
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
            Log.Information("RESOURCE client workingSetMB={Ws:F1} privateMB={Private:F1} gcHeapMB={Heap:F1} cpuPercent={Cpu:F2} cpuTotalS={CpuTotal:F1} threads={Threads}",
                process.WorkingSet64 / 1048576.0, process.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                cpuPercent, cpu.TotalSeconds, process.Threads.Count);
        }
        while (await timer.WaitForNextTickAsync(token));
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
}
