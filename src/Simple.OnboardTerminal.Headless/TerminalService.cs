using Microsoft.Extensions.Hosting;
using Simple.OnboardTerminal.Core;

namespace Simple.OnboardTerminal.Headless;

/// <summary>Runs the terminal's connection for as long as the process runs.</summary>
internal sealed class TerminalService(TerminalConnection connection) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => connection.RunAsync(stoppingToken);
}
