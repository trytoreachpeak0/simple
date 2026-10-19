using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Simple.OnboardTerminal.Core;

namespace Simple.OnboardTerminal.Headless;

/// <summary>Runs the terminal's connection and reads its IO module for as long as the process runs.</summary>
internal sealed partial class TerminalService(TerminalConnection connection, IoModuleReader io, TerminalKeyInfo key, ILogger<TerminalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (key.TpmProblem is { } problem)
        {
            LogNoTpm(logger, problem);
        }

        var store = key.MachineKey ? "machine" : "user";
        if (key.Created)
        {
            LogKeyCreated(logger, key.Name, key.Provider, store, key.Pin);
        }
        else
        {
            LogKeyOpened(logger, key.Name, key.Provider, store, key.Pin);
        }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "No key in the TPM ({Problem}): the terminal's key is in the software key store, not exportable")]
    private static partial void LogNoTpm(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created the terminal's key {KeyName} ({Provider}, {Store} key store), pin {Pin}: the server does not know this terminal yet; an engineer confirms it with the pairing code on the screen")]
    private static partial void LogKeyCreated(ILogger logger, string keyName, string provider, string store, string pin);

    [LoggerMessage(Level = LogLevel.Information, Message = "The terminal's key {KeyName} ({Provider}, {Store} key store), pin {Pin}")]
    private static partial void LogKeyOpened(ILogger logger, string keyName, string provider, string store, string pin);
}

/// <summary>Which key the terminal presents (<see cref="TerminalKey"/>), for its program log.</summary>
/// <param name="TpmProblem">Why the key was not created in the TPM although the settings allowed it; null otherwise.</param>
internal sealed record TerminalKeyInfo(string Name, string Provider, bool MachineKey, bool Created, string Pin, string? TpmProblem);
