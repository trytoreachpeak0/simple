namespace Simple.Server.Riot.Tests;

/// <summary>
/// Which areas a RIoT station serves, read from its name (ADR 0002): the
/// convention of the D8 maps, carried over from the previous line.
/// </summary>
public sealed class StationAreasTests
{
    [Theory]
    [InlineData("N1-1", new[] { "N1-1" })]
    [InlineData("N2-2_N3-1", new[] { "N2-2", "N3-1" })]
    [InlineData("N2-16_N3-15_N3-16", new[] { "N2-16", "N3-15", "N3-16" })]
    [InlineData("T2-1_T3-1_T3-2", new[] { "T2-1", "T3-1", "T3-2" })]
    [InlineData("S1-1", new[] { "S1-1" })]
    public void AMachineStationNamesItsAreas(string name, string[] areas)
    {
        Assert.Equal(areas, StationAreas.Of(name));
    }

    [Theory]
    [InlineData("关卡")]
    [InlineData("充电点1")]
    [InlineData("等待点1")]
    [InlineData("派工待送取货")]
    [InlineData("F1-CHARGER")] // the part after the hyphen is not a number
    [InlineData("n1-1")] // an area starts with a capital letter
    [InlineData("N1-1_关卡")] // every part has to be an area
    [InlineData("N1-1_N1-1")] // twice the same area
    [InlineData("N1-1__N1-2")]
    [InlineData("")]
    public void AnyOtherNameServesNoArea(string name)
    {
        Assert.Empty(StationAreas.Of(name));
    }

    [Fact]
    public void TheNameIsTrimmedButNotOtherwiseChanged()
    {
        // ADR 0002: 5 and 05 are two areas; only surrounding white space goes.
        Assert.Equal(["T4-01"], StationAreas.Of(" T4-01 "));
    }
}
