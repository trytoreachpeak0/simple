using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentModbus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Simple.IoSimulator.Core;

namespace Simple.IoSimulator.Headless.Tests;

/// <summary>
/// The control API through which tests, scenarios and AI sessions play the
/// person at the slots and the faults, while the onboard terminal talks
/// Modbus to the same module.
/// </summary>
public sealed class ControlApiTests : IAsyncLifetime
{
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), "simple-iosimulator-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient http;
    private readonly ModbusTcpClient modbus = new() { ReadTimeout = 2000, WriteTimeout = 2000 };

    public ControlApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("Simple:Logs:Directory", logDirectory)
            .UseSetting("IoSimulator:Modbus", "127.0.0.1:0"));
        http = factory.CreateClient();
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var state = await StateAsync();
        modbus.Connect(IPEndPoint.Parse(state.GetProperty("modbus").GetString()!), ModbusEndianness.BigEndian);
    }

    public async ValueTask DisposeAsync()
    {
        modbus.Dispose();
        http.Dispose();
        await factory.DisposeAsync();
        try
        {
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Hosted in process, the program may not have let go of its log
            // yet when the factory returns (#69), or a scanner holds it; a
            // folder left in %TEMP% harms nothing.
        }
    }

    [Fact]
    public async Task TheStateShowsTheRawValuesTheModuleReads()
    {
        var state = await StateAsync();

        // Every slot locked (DI1-8 = 1) and empty (DI9-16 = 1), every output off.
        Assert.Equal(Enumerable.Repeat(1, 16), Ints(state, "inputs"));
        Assert.Equal(Enumerable.Repeat(0, 16), Ints(state, "outputs"));
        Assert.Equal(0xFF, modbus.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16)[1]);
        Assert.Equal(8, state.GetProperty("slots").GetArrayLength());
        Assert.Equal("none", state.GetProperty("modbusFault").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task CargoGoesInOnlyThroughADoorTheTerminalUnlocked()
    {
        using var refused = await http.PostAsync(new Uri("/api/v1/slots/2/load", UriKind.Relative), null, Cancel);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("door-not-open", (await refused.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("error").GetString());

        modbus.WriteSingleCoil(IoModuleServer.UnitId, IoModuleServer.FirstOutputAddress + 1, true);
        using var loaded = await http.PostAsync(new Uri("/api/v1/slots/2/load", UriKind.Relative), null, Cancel);
        using var closed = await http.PostAsync(new Uri("/api/v1/slots/2/close", UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var slot = (await StateAsync()).GetProperty("slots")[1];
        Assert.True(slot.GetProperty("cargo").GetBoolean());
        Assert.False(slot.GetProperty("doorOpen").GetBoolean());
    }

    [Fact]
    public async Task APinnedLightCurtainReadsOverModbusUntilCleared()
    {
        using var pinned = await http.PutAsJsonAsync(new Uri("/api/v1/slots/4/light-curtain", UriKind.Relative), new { value = 0 }, Cancel);

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        Assert.Equal(0b1111_0111, modbus.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16)[1]);
        Assert.Equal(0, Ints(await StateAsync(), "inputs")[11]);

        using var cleared = await http.PutAsJsonAsync(new Uri("/api/v1/slots/4/light-curtain", UriKind.Relative), new { value = (int?)null }, Cancel);
        Assert.Equal(0xFF, modbus.ReadDiscreteInputs(IoModuleServer.UnitId, IoModuleServer.FirstInputAddress, 16)[1]);
    }

    [Theory]
    [InlineData("/api/v1/slots/9/close")]
    [InlineData("/api/v1/slots/0/load")]
    public async Task ThereAreEightSlots(string path)
    {
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AModbusFaultIsSwitchedOnAndOff()
    {
        using var on = await http.PutAsJsonAsync(new Uri("/api/v1/modbus-fault", UriKind.Relative), new { mode = "delay", delayMs = 300 }, Cancel);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var fault = (await StateAsync()).GetProperty("modbusFault");
        Assert.Equal("delay", fault.GetProperty("mode").GetString());
        Assert.Equal(300, fault.GetProperty("delayMs").GetInt32());

        using var bad = await http.PutAsJsonAsync(new Uri("/api/v1/modbus-fault", UriKind.Relative), new { mode = "delay", delayMs = -1 }, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var off = await http.PutAsJsonAsync(new Uri("/api/v1/modbus-fault", UriKind.Relative), new { mode = "none" }, Cancel);
        Assert.Equal("none", (await StateAsync()).GetProperty("modbusFault").GetProperty("mode").GetString());
        await factory.DisposeAsync();
        var log = Simple.Logging.ProgramLog.ReadText(logDirectory);
        Assert.Contains("Modbus communication fault now \"Delay\" (delay 300 ms)", log, StringComparison.Ordinal);
        Assert.Contains("Modbus communication fault now \"None\" (delay 0 ms)", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAutoResponderAnswersTheNextDoorsAndWaitingTellsHowItEnded()
    {
        using var started = await http.PostAsJsonAsync(
            new Uri("/api/v1/auto-responder", UriKind.Relative),
            new { action = "load", doors = 1, actSeconds = 0.1, closeSeconds = 0.1 },
            Cancel);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        modbus.WriteSingleCoil(IoModuleServer.UnitId, IoModuleServer.FirstOutputAddress + 6, true);
        using var done = await http.PostAsync(new Uri("/api/v1/auto-responder/wait?timeoutSeconds=10", UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("done", (await done.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("state").GetString());
        Assert.True((await StateAsync()).GetProperty("slots")[6].GetProperty("cargo").GetBoolean());
    }

    [Fact]
    public async Task AFailedAutoResponderIsNotADoneOne()
    {
        await http.PostAsJsonAsync(
            new Uri("/api/v1/auto-responder", UriKind.Relative),
            new { action = "unload", doors = 1, actSeconds = 0.1, closeSeconds = 0.1 },
            Cancel);
        modbus.WriteSingleCoil(IoModuleServer.UnitId, IoModuleServer.FirstOutputAddress, true);

        using var failed = await http.PostAsync(new Uri("/api/v1/auto-responder/wait?timeoutSeconds=10", UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var body = await failed.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Equal("failed", body.GetProperty("state").GetString());
        Assert.Equal("slot 1: could not unload: SlotEmpty", body.GetProperty("failure").GetString());
        await factory.DisposeAsync();
        Assert.Contains("Auto-responder failed: slot 1: could not unload: SlotEmpty", Simple.Logging.ProgramLog.ReadText(logDirectory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitingOutItsTimeoutIsNotDoneEither()
    {
        await http.PostAsJsonAsync(
            new Uri("/api/v1/auto-responder", UriKind.Relative),
            new { action = "load", doors = 1, actSeconds = 0.1, closeSeconds = 0.1 },
            Cancel);

        using var waited = await http.PostAsync(new Uri("/api/v1/auto-responder/wait?timeoutSeconds=0.2", UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, waited.StatusCode);
        Assert.Equal("running", (await waited.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("/api/v1/auto-responder/wait?timeoutSeconds=NaN")]
    [InlineData("/api/v1/auto-responder/wait?timeoutSeconds=1e300")]
    [InlineData("/api/v1/auto-responder/wait?timeoutSeconds=0")]
    public async Task AWaitThatIsNotANumberOfSecondsIsRefused(string path)
    {
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1e300, 4.0)]
    [InlineData(4.0, -1.0)]
    public async Task AnAutoResponseWithSecondsOutOfRangeIsRefused(double actSeconds, double closeSeconds)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri("/api/v1/auto-responder", UriKind.Relative), new { action = "load", doors = 1, actSeconds, closeSeconds }, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ItWritesAProgramLog()
    {
        await factory.DisposeAsync();

        Assert.NotEmpty(Simple.Logging.ProgramLog.Files(logDirectory));
    }

    private static int[] Ints(JsonElement state, string name) => [.. state.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];

    private async Task<JsonElement> StateAsync() =>
        await http.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/state", UriKind.Relative), Cancel);
}
