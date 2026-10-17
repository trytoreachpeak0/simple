using System.Diagnostics;
using FluentModbus;

namespace Simple.IoSimulator.Core.Tests;

/// <summary>
/// The module's Modbus face as a client sees it: only unit 255, only its 16
/// outputs and 16 inputs, and the communication faults a test can switch on.
/// </summary>
public sealed class ModbusFaceTests : IDisposable
{
    private readonly IoModule module = new();

    [Fact]
    public void OnlyTheModulesOutputsAndInputsExist()
    {
        var outside = Assert.Throws<ModbusException>(() => module.Client.ReadCoils(IoModuleServer.UnitId, 0, 16));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, outside.ExceptionCode);
        var beyond = Assert.Throws<ModbusException>(() => module.Client.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 17));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, beyond.ExceptionCode);
        var registers = Assert.Throws<ModbusException>(() => module.Client.ReadHoldingRegisters<short>(IoModuleServer.UnitId, 0, 1));
        Assert.Equal(ModbusExceptionCode.IllegalFunction, registers.ExceptionCode);
    }

    [Fact]
    public void AnotherUnitIdGetsNoAnswer()
    {
        using var client = module.Connect();
        client.ReadTimeout = 300;

        Assert.ThrowsAny<IOException>(() => client.ReadDiscreteInputs(1, IoModuleServer.FirstInputAddress, 16));
    }

    [Fact]
    public void NoResponseKeepsTheConnectionButAnswersNothingUntilCleared()
    {
        module.Server.SetFault(ModbusFault.NoResponse, TimeSpan.Zero);
        using var client = module.Connect();
        client.ReadTimeout = 300;

        Assert.ThrowsAny<IOException>(() => client.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16));
        Assert.True(client.IsConnected);

        module.Server.SetFault(ModbusFault.None, TimeSpan.Zero);
        Assert.True(module.Input(1));
    }

    [Fact]
    public void DisconnectClosesOpenConnectionsAndRefusesNewOnesUntilCleared()
    {
        Assert.True(module.Input(1));

        module.Server.SetFault(ModbusFault.Disconnect, TimeSpan.Zero);

        Assert.ThrowsAny<IOException>(() => module.Input(1));
        using var refused = module.Connect();
        refused.ReadTimeout = 300;
        Assert.ThrowsAny<IOException>(() => refused.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16));

        module.Server.SetFault(ModbusFault.None, TimeSpan.Zero);
        using var again = module.Connect();
        Assert.Equal(0xFF, again.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16)[0]);
    }

    [Fact]
    public void DelayAnswersEveryRequestLate()
    {
        module.Server.SetFault(ModbusFault.Delay, TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();

        Assert.True(module.Input(1));

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2));
        Assert.Equal((ModbusFault.Delay, TimeSpan.FromMilliseconds(400)), module.Server.Fault);
    }

    public void Dispose() => module.Dispose();
}
