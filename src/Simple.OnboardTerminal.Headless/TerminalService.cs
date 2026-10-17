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
        await Task.WhenAll(reading, connection.RunAsync(stoppingToken));
    }
}
