using Microsoft.Extensions.Hosting;
using Simple.OnboardTerminal.Core;

namespace Simple.OnboardTerminal.Headless;

/// <summary>Runs the terminal's connection and reads its IO module for as long as the process runs.</summary>
internal sealed class TerminalService(TerminalConnection connection, IoModuleReader io) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reading = io.RunAsync(stoppingToken);
        // The first report then carries the first reading (IoModuleReader.FirstAttempt).
        await io.FirstAttempt.WaitAsync(stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        var connecting = connection.RunAsync(stoppingToken);
        // An unexpected failure of either ends the service now, and with it
        // the host, instead of waiting for the stop while the process runs on
        // without a connection or without reading its IO module.
        await await Task.WhenAny(reading, connecting);
        await Task.WhenAll(reading, connecting);
    }
}
