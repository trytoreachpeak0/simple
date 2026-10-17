using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;

namespace Simple.IoSimulator.Core;

/// <summary>
/// The Modbus TCP face of the simulated IO module, as the onboard terminal
/// sees a C2000 module: unit <see cref="UnitId"/> only, DO1-DO16 as coils from
/// address <see cref="FirstOutputAddress"/>, DI1-DI16 as discrete inputs from
/// address <see cref="FirstInputAddress"/>. Reads come from the cabinet,
/// writes to the outputs go to it. It listens on loopback only.
/// </summary>
public sealed partial class IoModuleServer : IDisposable
{
    public const byte UnitId = 255;

    public const ushort FirstOutputAddress = 100;

    public const ushort FirstInputAddress = 200;

    private readonly Cabinet cabinet;
    private readonly ModbusTcpServer server;
    private readonly ModbusFaultLink link;
    private readonly ILogger logger;

    private IoModuleServer(Cabinet cabinet, ModbusTcpServer server, ModbusFaultLink link, ILogger logger)
    {
        this.cabinet = cabinet;
        this.server = server;
        this.link = link;
        this.logger = logger;
    }

    /// <summary>Where clients connect.</summary>
    public IPEndPoint Endpoint => link.Endpoint;

    /// <summary>The communication fault in force, and its delay for <see cref="ModbusFault.Delay"/>.</summary>
    public (ModbusFault Fault, TimeSpan Delay) Fault => link.Fault;

    /// <summary>Starts listening on <paramref name="endpoint"/>, which must be a loopback address.</summary>
    public static IoModuleServer Start(Cabinet cabinet, IPEndPoint endpoint, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(loggers);
        if (!IPAddress.IsLoopback(endpoint.Address))
        {
            throw new ArgumentException($"{endpoint} is not a loopback address. The IO simulator listens on loopback only.", nameof(endpoint));
        }

        var logger = loggers.CreateLogger<IoModuleServer>();
        var server = new ModbusTcpServer(logger, isAsynchronous: true)
        {
            EnableRaisingEvents = true,
            // A write of 1 to an output that is already 1 restarts its pulse.
            AlwaysRaiseChangedEvent = true,
            RequestValidator = Validate,
        };
        // Answer unit 255 only, as the module is configured; FluentModbus
        // otherwise answers every unit id.
        server.AddUnit(UnitId);
        var link = new ModbusFaultLink(endpoint);
        var module = new IoModuleServer(cabinet, server, link, logger);
        cabinet.Changed += module.Mirror;
        server.CoilsChanged += module.OnCoilsChanged;
        module.Mirror();
        server.Start(link);
        LogListening(logger, module.Endpoint);
        return module;
    }

    public void SetFault(ModbusFault fault, TimeSpan delay)
    {
        link.SetFault(fault, delay);
        var (set, delayBy) = link.Fault;
        LogFault(logger, set, (int)delayBy.TotalMilliseconds);
    }

    public void Dispose()
    {
        cabinet.Changed -= Mirror;
        server.Stop();
        server.Dispose();
    }

    /// <summary>
    /// Only the module's 16 outputs and 16 inputs exist; anything else is an
    /// illegal address, and registers an illegal function.
    /// </summary>
    private static ModbusExceptionCode Validate(byte unit, ModbusFunctionCode function, ushort address, ushort quantity) => function switch
    {
        ModbusFunctionCode.ReadCoils or ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteMultipleCoils =>
            Within(address, quantity, FirstOutputAddress),
        ModbusFunctionCode.ReadDiscreteInputs => Within(address, quantity, FirstInputAddress),
        _ => ModbusExceptionCode.IllegalFunction,
    };

    private static ModbusExceptionCode Within(ushort address, ushort quantity, ushort first) =>
        address >= first && address + quantity <= first + Cabinet.ChannelCount ? ModbusExceptionCode.OK : ModbusExceptionCode.IllegalDataAddress;

    /// <summary>Copies the cabinet's current outputs and inputs into the server's buffers, under the server's lock.</summary>
    private void Mirror()
    {
        lock (server.Lock)
        {
            var state = cabinet.State();
            var coils = server.GetCoils(UnitId);
            var inputs = server.GetDiscreteInputs(UnitId);
            for (var i = 0; i < Cabinet.ChannelCount; i++)
            {
                SetBit(coils, FirstOutputAddress + i, state.Outputs[i]);
                SetBit(inputs, FirstInputAddress + i, state.Inputs[i]);
            }
        }
    }

    /// <summary>A client wrote outputs. Raised by FluentModbus under the server's lock, after it wrote them into its buffer.</summary>
    private void OnCoilsChanged(object? sender, CoilsChangedEventArgs e)
    {
        // Read every written value first: each write to the cabinet mirrors
        // its outputs back into this buffer.
        var coils = server.GetCoils(UnitId);
        var written = new (int Number, bool Value)[e.Coils.Length];
        for (var i = 0; i < written.Length; i++)
        {
            written[i] = (e.Coils[i] - FirstOutputAddress + 1, GetBit(coils, e.Coils[i]));
        }

        foreach (var (number, value) in written)
        {
            cabinet.WriteOutput(number, value);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The simulated IO module listens for Modbus TCP on {Endpoint}, unit 255")]
    private static partial void LogListening(ILogger logger, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modbus communication fault now {Fault} (delay {DelayMs} ms)")]
    private static partial void LogFault(ILogger logger, ModbusFault fault, int delayMs);

    private static bool GetBit(Span<byte> bits, int address) => (bits[address / 8] & (1 << (address % 8))) != 0;

    private static void SetBit(Span<byte> bits, int address, bool value)
    {
        if (value)
        {
            bits[address / 8] |= (byte)(1 << (address % 8));
        }
        else
        {
            bits[address / 8] &= (byte)~(1 << (address % 8));
        }
    }
}
