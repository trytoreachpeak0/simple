using System.Net;
using Microsoft.Extensions.Options;
using Simple.IoSimulator.Core;

namespace Simple.IoSimulator.Headless;

/// <summary>The one simulated IO module of this process: its cabinet, its Modbus face (listening from construction on) and its auto-responder.</summary>
public sealed class SimulatedIoModule : IDisposable
{
    public SimulatedIoModule(IOptions<IoSimulatorSettings> settings, TimeProvider time, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Cabinet = new Cabinet(time, settings.Value.CabinetTiming);
        Server = IoModuleServer.Start(Cabinet, IPEndPoint.Parse(settings.Value.Modbus), loggers);
        AutoResponder = new AutoResponder(Cabinet, time, loggers);
    }

    public Cabinet Cabinet { get; }

    public IoModuleServer Server { get; }

    public AutoResponder AutoResponder { get; }

    public void Dispose()
    {
        AutoResponder.Dispose();
        Server.Dispose();
        Cabinet.Dispose();
    }
}
