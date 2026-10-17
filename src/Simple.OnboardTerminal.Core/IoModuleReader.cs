using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core;

/// <param name="Readable">Whether the latest attempt to read the module succeeded.</param>
/// <param name="SinceRead">How long ago the module was last read successfully; null when it has not been since the terminal started.</param>
/// <param name="Points">Every point as last read, inputs first; empty before the first successful read.</param>
public sealed record IoReading(bool Readable, TimeSpan? SinceRead, IReadOnlyList<IoPointReading> Points);

/// <param name="Number">The module's channel, from 1.</param>
/// <param name="Value">The raw value: true for 1.</param>
/// <param name="Changes">How often it changed since the terminal started; the first read is not a change.</param>
/// <param name="SinceChange">How long ago it was read changing, or, if it never did, first read.</param>
public sealed record IoPointReading(IoPointKind Kind, int Number, bool Value, ulong Changes, TimeSpan SinceChange);

/// <summary>
/// Reads the vehicle's IO module (a C2000 module) over Modbus TCP every
/// <see cref="PollInterval"/>: its 16 outputs (coils from address 100) and 16
/// inputs (discrete inputs from address 200), unit 255. The terminal reports
/// the raw values and does not know which slot a point belongs to (ADR 0024).
/// A read that fails or takes longer than <see cref="ReadTimeout"/> drops the
/// connection; the next poll connects again.
/// </summary>
public sealed partial class IoModuleReader : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    public const byte UnitId = 255;

    public const ushort FirstOutputAddress = 100;

    public const ushort FirstInputAddress = 200;

    public const int Channels = 16;

    private readonly IPEndPoint? module;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly TaskCompletionSource firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Point[] points = [
        .. Enumerable.Range(1, Channels).Select(n => new Point(IoPointKind.Input, n)),
        .. Enumerable.Range(1, Channels).Select(n => new Point(IoPointKind.Output, n)),
    ];

    private ModbusTcpClient? client;
    private bool readable;
    private long? lastRead;

    /// <param name="module">The module's address; null when the terminal has none configured, and then nothing is read.</param>
    public IoModuleReader(IPEndPoint? module, TimeProvider time, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggers);
        this.module = module;
        this.time = time;
        logger = loggers.CreateLogger<IoModuleReader>();
    }

    /// <summary>Raised when a point's value or whether the module can be read changes, so that the next report goes out at once.</summary>
    public event Action? Changed;

    /// <summary>
    /// Completes once the first attempt to read the module has succeeded or
    /// failed (within about <see cref="ReadTimeout"/> twice), or at once with
    /// no module configured. The terminal waits for it before its first
    /// report, so that the report does not say "cannot be read" only because
    /// nothing was tried yet.
    /// </summary>
    public Task FirstAttempt => firstAttempt.Task;

    public IoReading Read()
    {
        lock (gate)
        {
            var now = time.GetTimestamp();
            return new IoReading(
                readable,
                lastRead is { } read ? time.GetElapsedTime(read, now) : null,
                lastRead is null ? [] : points.Select(p => new IoPointReading(p.Kind, p.Number, p.Value, p.Changes, time.GetElapsedTime(p.ChangedAt, now))).ToList());
        }
    }

    /// <summary>Polls the module until <paramref name="stopping"/>.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        if (module is null)
        {
            LogNoModule(logger);
            firstAttempt.TrySetResult();
            return;
        }

        LogStarting(logger, module);
        using var timer = new PeriodicTimer(PollInterval, time);
        try
        {
            do
            {
                await PollAsync(stopping);
                firstAttempt.TrySetResult();
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            firstAttempt.TrySetResult();
            Disconnect();
        }
    }

    public void Dispose() => Disconnect();

    private async Task PollAsync(CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            if (client is not { IsConnected: true })
            {
                Disconnect();
                client = new ModbusTcpClient
                {
                    ConnectTimeout = (int)ReadTimeout.TotalMilliseconds,
                    ReadTimeout = (int)ReadTimeout.TotalMilliseconds,
                    WriteTimeout = (int)ReadTimeout.TotalMilliseconds,
                };
                var connecting = client;
                // FluentModbus connects synchronously, waiting up to ConnectTimeout.
                await Task.Run(() => connecting.Connect(module!, ModbusEndianness.BigEndian), timeout.Token);
            }

            // What a read returns lies in the client's own frame buffer, which
            // the next read overwrites: copy it first.
            var inputs = (await client.ReadDiscreteInputsAsync(UnitId, FirstInputAddress, Channels, timeout.Token)).ToArray();
            var outputs = (await client.ReadCoilsAsync(UnitId, FirstOutputAddress, Channels, timeout.Token)).ToArray();
            Store(inputs, outputs);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // FluentModbus throws plain Exception for a connect timeout, and
            // IOException, SocketException, ModbusException or a cancellation
            // otherwise: whatever it was, the module could not be read. A
            // failure as the terminal stops is only the stop.
            Disconnect();
            if (!stopping.IsCancellationRequested)
            {
                Unreadable(e);
            }
        }
    }

    private void Store(ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> outputs)
    {
        var changed = false;
        bool wasReadable;
        lock (gate)
        {
            var now = time.GetTimestamp();
            var first = lastRead is null;
            foreach (var point in points)
            {
                var bits = point.Kind == IoPointKind.Input ? inputs : outputs;
                var value = (bits[(point.Number - 1) / 8] & (1 << ((point.Number - 1) % 8))) != 0;
                if (first)
                {
                    point.Value = value;
                    point.ChangedAt = now;
                }
                else if (point.Value != value)
                {
                    point.Value = value;
                    point.Changes++;
                    point.ChangedAt = now;
                    changed = true;
                }
            }

            lastRead = now;
            wasReadable = readable;
            readable = true;
        }

        if (!wasReadable)
        {
            LogReadable(logger, module!);
        }

        if (changed || !wasReadable)
        {
            Changed?.Invoke();
        }
    }

    private void Unreadable(Exception e)
    {
        bool wasReadable;
        lock (gate)
        {
            wasReadable = readable;
            readable = false;
        }

        if (wasReadable)
        {
            LogUnreadable(logger, module!, e.GetType().Name, e.Message);
            Changed?.Invoke();
        }
    }

    private void Disconnect()
    {
        try
        {
            client?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Gone already.
        }

        client = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No IO module is configured (ioModule): nothing is read, and every report says the IO module cannot be read")]
    private static partial void LogNoModule(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading the IO module at {Module}")]
    private static partial void LogStarting(ILogger logger, IPEndPoint module);

    [LoggerMessage(Level = LogLevel.Information, Message = "The IO module at {Module} reads")]
    private static partial void LogReadable(ILogger logger, IPEndPoint module);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The IO module at {Module} cannot be read ({Error}): {Detail}")]
    private static partial void LogUnreadable(ILogger logger, IPEndPoint module, string error, string detail);

    private sealed class Point(IoPointKind kind, int number)
    {
        public IoPointKind Kind { get; } = kind;

        public int Number { get; } = number;

        public bool Value { get; set; }

        public ulong Changes { get; set; }

        public long ChangedAt { get; set; }
    }
}
