using System.Text.Json;

namespace Simple.Cli.Tests;

/// <summary>The MES line of <c>simple status</c>, from whatever the server's status says.</summary>
public sealed class InstanceReportTests
{
    private static string MesLine(string mesGateway)
    {
        using var server = JsonDocument.Parse($$"""{ "mesGateway": {{mesGateway}} }""");
        var report = new InstanceReport("i", false, "repo", "sql", "db", DateTimeOffset.UnixEpoch, [], server.RootElement.Clone());
        return Assert.Single(report.Lines(), l => l.Contains("MES gateway", StringComparison.Ordinal)).Trim();
    }

    [Fact]
    public void AGoodRoundShowsItsNumberTimeAndRows() =>
        Assert.Matches(
            @"^MES gateway reachable; round 12 at \d\d:\d\d:\d\d, 3 rows$",
            MesLine("""{ "reachable": true, "available": true, "lastRound": { "round": 12, "readAt": "2026-10-17T08:00:00+00:00", "succeeded": true }, "rowCount": 3 }"""));

    [Fact]
    public void AnUnreachableGatewayShowsWhyAndTheLastRound() =>
        Assert.Equal(
            "MES gateway unreachable (refused), MES unavailable; round 4 failed: ORA-03113",
            MesLine("""{ "reachable": false, "gatewayError": "refused", "available": false, "lastRound": { "round": 4, "succeeded": false, "failure": { "kind": "read", "message": "ORA-03113" } } }"""));

    [Theory]
    [InlineData("""{ }""", "MES gateway not asked yet; no round yet")]
    [InlineData("""{ "reachable": null, "lastRound": null }""", "MES gateway not asked yet; no round yet")]
    [InlineData("""{ "reachable": false, "lastRound": { "succeeded": false } }""", "MES gateway unreachable (no reason given); a round failed: no reason given")]
    [InlineData("""{ "reachable": true, "lastRound": { "round": 5, "succeeded": true, "readAt": null }, "rowCount": null }""", "MES gateway reachable; round 5 at an unknown time, rows unknown")]
    [InlineData("""{ "reachable": true, "lastRound": { "round": 5, "succeeded": false, "failure": null } }""", "MES gateway reachable; round 5 failed: no reason given")]
    public void MissingOrNullFieldsStillGiveALine(string mesGateway, string line) => Assert.Equal(line, MesLine(mesGateway));
}
