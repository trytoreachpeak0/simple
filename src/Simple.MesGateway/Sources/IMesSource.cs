using System.Globalization;

namespace Simple.MesGateway.Sources;

/// <summary>
/// Where the gateway reads the MES from: the customer's Oracle MES
/// (OracleMesSource) or the fake source. A read has no side effect, so the
/// gateway retries a failed one for as long as it takes (ADR 0008).
/// </summary>
public interface IMesSource
{
    /// <summary>Runs the query once. Any exception is a failed read.</summary>
    Task<MesReadResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A query result as a database reader gives it: column names, then rows of values in that order.</summary>
public sealed record MesReadResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    /// <summary>
    /// Turns the result into <see cref="MesRow"/>s when it has the agreed
    /// shape: the seven columns of <see cref="MesColumns.All"/>, each once,
    /// every row with one value per column, every value text, a date or empty.
    /// Otherwise names the first thing that does not fit.
    /// </summary>
    public bool TryGetRows(out IReadOnlyList<MesRow> rows, out string? error)
    {
        rows = [];
        // The types say these are never null; a source that breaks that is malformed, not a crash.
        if (Columns is null || Rows is null)
        {
            error = Columns is null ? "The result has no column list." : "The result has no row list.";
            return false;
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i] is null)
            {
                error = $"Column {i + 1} has no name.";
                return false;
            }

            if (!MesColumns.All.Contains(Columns[i], StringComparer.OrdinalIgnoreCase))
            {
                error = $"The result has a column {Columns[i]} the query does not have.";
                return false;
            }

            if (!index.TryAdd(Columns[i], i))
            {
                error = $"The result has the column {Columns[i]} twice.";
                return false;
            }
        }

        if (MesColumns.All.FirstOrDefault(c => !index.ContainsKey(c)) is { } missing)
        {
            error = $"The result has no column {missing}.";
            return false;
        }

        var converted = new List<MesRow>(Rows.Count);
        for (var r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            if (row is null)
            {
                error = $"Row {r + 1} is missing.";
                return false;
            }

            if (row.Count != Columns.Count)
            {
                error = $"Row {r + 1} has {row.Count} values for {Columns.Count} columns.";
                return false;
            }

            var values = new string?[MesColumns.All.Count];
            for (var c = 0; c < MesColumns.All.Count; c++)
            {
                var value = row[index[MesColumns.All[c]]];
                if (!TryText(value, out values[c]))
                {
                    error = $"Row {r + 1} has a value of type {value!.GetType().Name} in {MesColumns.All[c]}; expected text or a date.";
                    return false;
                }
            }

            converted.Add(new MesRow(values[0], values[1], values[2], values[3], values[4], values[5], values[6]));
        }

        rows = converted;
        error = null;
        return true;
    }

    private static bool TryText(object? value, out string? text)
    {
        switch (value)
        {
            case null or DBNull:
                text = null;
                return true;
            case string s:
                text = s;
                return true;
            case DateTime d:
                // An MES DATE carries no time zone; it is passed on as it is.
                text = d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                return true;
            default:
                text = null;
                return false;
        }
    }
}
