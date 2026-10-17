using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Simple.IoSimulator.Core;

namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// A simulated IO module on a loopback port, its cabinet on a fake clock, and
/// a Modbus client connected to it the way the onboard terminal connects.
/// </summary>
internal sealed class IoModule : IDisposable
{
    public IoModule(CabinetTiming? timing = null)
    {
        Cabinet = new Cabinet(Time, timing ?? CabinetTiming.Default);
        Server = IoModuleServer.Start(Cabinet, new IPEndPoint(IPAddress.Loopback, 0), NullLoggerFactory.Instance);
        Client = Connect();
    }

    public FakeTimeProvider Time { get; } = new();

    public Cabinet Cabinet { get; }

    public IoModuleServer Server { get; }

    public ModbusTcpClient Client { get; }

    public ModbusTcpClient Connect()
    {
        var client = new ModbusTcpClient { ReadTimeout = 2000, WriteTimeout = 2000 };
        client.Connect(Server.Endpoint, ModbusEndianness.BigEndian);
        return client;
    }

    /// <summary>DI <paramref name="number"/> (1-16) as the client reads it.</summary>
    public bool Input(int number) => Bit(Client.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16), number);

    /// <summary>DO <paramref name="number"/> (1-16) as the client reads it.</summary>
    public bool Output(int number) => Bit(Client.ReadCoils(IoModuleServer.UnitId, IoModuleServer.FirstOutputAddress, 16), number);

    /// <summary>Writes DO <paramref name="number"/> (1-16) the way the terminal's unlock does.</summary>
    public void WriteOutput(int number, bool value) =>
        Client.WriteSingleCoil(IoModuleServer.UnitId, IoModuleServer.FirstOutputAddress + number - 1, value);

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Cabinet.Dispose();
    }

    private static bool Bit(Span<byte> bits, int number) => (bits[(number - 1) / 8] & (1 << ((number - 1) % 8))) != 0;
}
