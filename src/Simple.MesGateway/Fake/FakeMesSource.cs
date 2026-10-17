using System.Text.Json;
using Microsoft.Extensions.Options;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Fake;

/// <summary>
/// An MES that tests control: it starts with the rows of a seed file, and its
/// control API (<see cref="FakeMesEndpoints"/>) adds and removes rows and
/// makes the next reads fail or return rows of the wrong shape.
/// </summary>
public sealed class FakeMesSource(IOptions<GatewaySettings> settings) : IMesSource
{
    public const string DefaultFailureMessage = "The fake MES was told to fail this read.";

    private static readonly MesRow Malformed = new("MALFORMED", "MALFORMED", null, null, null, null, null);

    private readonly Lock gate = new();
    private List<MesRow>? rows;
    private int failuresLeft;
    private string failureMessage = DefaultFailureMessage;
    private int malformedLeft;

    public static IReadOnlyList<MesRow> ReadSeed(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<MesRow>>(stream, JsonSerializerOptions.Web)
            ?? throw new JsonException($"{path} holds null instead of an array of rows.");
    }

    public Task<MesReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (failuresLeft > 0)
            {
                failuresLeft--;
                throw new IOException(failureMessage);
            }

            IReadOnlyList<IReadOnlyList<object?>> values = [.. Rows.Select(Values)];
            if (malformedLeft > 0)
            {
                malformedLeft--;
                // A row one value short: PACKAGE is missing.
                values = [.. values, Values(Malformed)[..^1]];
            }

            return Task.FromResult(new MesReadResult(MesColumns.All, values));
        }
    }

    public FakeMesState State()
    {
        lock (gate)
        {
            return new FakeMesState([.. Rows], failuresLeft, malformedLeft);
        }
    }

    /// <summary>Adds a row as it is, even one whose business key is already there: the MES can return duplicates.</summary>
    public void Add(MesRow row)
    {
        lock (gate)
        {
            Rows.Add(row);
        }
    }

    /// <summary>Removes every row with this business key (TASK_TYPE + SUBLOT, exact); returns how many.</summary>
    public int Remove(string? taskType, string? sublot)
    {
        lock (gate)
        {
            return Rows.RemoveAll(r => r.TaskType == taskType && r.Sublot == sublot);
        }
    }

    /// <summary>The next <paramref name="count"/> reads fail with <paramref name="message"/>.</summary>
    public void FailNext(int count, string? message)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            failuresLeft = count;
            failureMessage = string.IsNullOrWhiteSpace(message) ? DefaultFailureMessage : message;
        }
    }

    /// <summary>The next <paramref name="count"/> successful reads also return a row of the wrong shape.</summary>
    public void MalformedNext(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            malformedLeft = count;
        }
    }

    /// <summary>Back to the seed rows, with no failure or malformed read pending.</summary>
    public void Reset()
    {
        lock (gate)
        {
            rows = null;
            failuresLeft = 0;
            malformedLeft = 0;
            failureMessage = DefaultFailureMessage;
        }
    }

    // Read on first use, not at construction, so a bad seed file is reported
    // by the settings validator instead of failing the host while it is built.
    private List<MesRow> Rows => rows ??= [.. ReadSeed(settings.Value.FakeSeedPath!)];

    private static object?[] Values(MesRow row) =>
        [row.TaskType, row.Sublot, row.Area, row.Eqp, row.Step, row.Dates, row.Package];
}

public sealed record FakeMesState(IReadOnlyList<MesRow> Rows, int FailuresPending, int MalformedPending);
