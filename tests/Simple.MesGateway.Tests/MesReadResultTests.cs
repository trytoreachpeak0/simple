using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

public sealed class MesReadResultTests
{
    private static readonly object?[] Values = ["T", "S", "A1-1", "E", "ST", "2026-01-05T08:00:00", "P"];

    [Fact]
    public void RowsOfTheAgreedShapeComeThroughAsTheyAre()
    {
        var result = new MesReadResult(MesColumns.All, [Values, new object?[] { "T2", "S2", null, DBNull.Value, "", new DateTime(2026, 1, 5, 8, 30, 15), "P2" }]);

        Assert.True(result.TryGetRows(out var rows, out var error), error);
        Assert.Equal(
            [
                new MesRow("T", "S", "A1-1", "E", "ST", "2026-01-05T08:00:00", "P"),
                new MesRow("T2", "S2", null, null, "", "2026-01-05T08:30:15", "P2"),
            ],
            rows);
    }

    [Fact]
    public void ColumnsAreFoundByNameInAnyOrderAndCase()
    {
        string[] columns = ["package", "DATES", "STEP", "EQP", "AREA", "SUBLOT", "TASK_TYPE"];

        var result = new MesReadResult(columns, [Values.Reverse().ToArray()]);

        Assert.True(result.TryGetRows(out var rows, out var error), error);
        Assert.Equal(new MesRow("T", "S", "A1-1", "E", "ST", "2026-01-05T08:00:00", "P"), Assert.Single(rows));
    }

    [Fact]
    public void NoRowsIsAGoodResult()
    {
        Assert.True(new MesReadResult(MesColumns.All, []).TryGetRows(out var rows, out _));
        Assert.Empty(rows);
    }

    public static TheoryData<string[], object?[], string> WrongShapes => new()
    {
        { [.. MesColumns.All.Take(6)], Values[..6], "The result has no column PACKAGE." },
        { [.. MesColumns.All, "LOT"], [.. Values, "L"], "The result has a column LOT the query does not have." },
        { [.. MesColumns.All.Take(6), "TASK_TYPE"], Values, "The result has the column TASK_TYPE twice." },
        { [.. MesColumns.All], Values[..6], "Row 1 has 6 values for 7 columns." },
        { [.. MesColumns.All], ["T", "S", "A1-1", "E", "ST", "2026-01-05", 42], "Row 1 has a value of type Int32 in PACKAGE; expected text or a date." },
    };

    [Fact]
    public void NullsWhereTheTypesPromiseNoneAreAWrongShapeNotACrash()
    {
        Assert.False(new MesReadResult(null!, []).TryGetRows(out _, out var noColumns));
        Assert.Equal("The result has no column list.", noColumns);
        Assert.False(new MesReadResult(MesColumns.All, null!).TryGetRows(out _, out var noRows));
        Assert.Equal("The result has no row list.", noRows);
        Assert.False(new MesReadResult([.. MesColumns.All.Take(6), null!], []).TryGetRows(out _, out var unnamed));
        Assert.Equal("Column 7 has no name.", unnamed);
        Assert.False(new MesReadResult(MesColumns.All, [Values, null!]).TryGetRows(out _, out var missing));
        Assert.Equal("Row 2 is missing.", missing);
    }

    [Theory]
    [MemberData(nameof(WrongShapes))]
    public void AResultOfTheWrongShapeIsRefusedWithTheReason(string[] columns, object?[] row, string reason)
    {
        var result = new MesReadResult(columns, [row]);

        Assert.False(result.TryGetRows(out var rows, out var error));
        Assert.Equal(reason, error);
        Assert.Empty(rows);
    }
}
