using Microsoft.Extensions.Options;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Fake;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

public sealed class FakeMesSourceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A fake source on the seed file the gateway ships with.</summary>
    public static FakeMesSource Shipped() =>
        new(Options.Create(new GatewaySettings { Source = GatewaySettings.FakeSource, FakeSeedFile = "Fake/seed-rows.json" }));

    private static async Task<IReadOnlyList<MesRow>> RowsAsync(FakeMesSource fake)
    {
        var result = await fake.ReadAsync(Cancel);
        Assert.True(result.TryGetRows(out var rows, out var error), error);
        return rows;
    }

    [Fact]
    public async Task TheShippedSeedIsAFictionalSiteInTheAgreedShape()
    {
        var rows = await RowsAsync(Shipped());

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            // Option 11: tests and the local system default to a fictional
            // site, never to D8's real values.
            Assert.StartsWith("SAMPLE_", row.TaskType, StringComparison.Ordinal);
            Assert.StartsWith("SMP", row.Sublot, StringComparison.Ordinal);
            Assert.Matches("^S[1-9]-[1-9]$", row.Area);
            Assert.All(new[] { row.Eqp, row.Step, row.Dates, row.Package }, Assert.NotNull);
        });
    }

    [Fact]
    public async Task RowsAreAddedAndRemovedByBusinessKey()
    {
        var fake = Shipped();
        var seed = await RowsAsync(fake);
        var added = new MesRow("SAMPLE_TO_OVEN", "SMP9999.01", "S1-3", "SMP-DA-03", "SMP-CURE", "2026-01-06T09:00:00", "SMP-PKG-A");

        fake.Add(added);
        fake.Add(added with { Area = "S1-4" });
        Assert.Equal([.. seed, added, added with { Area = "S1-4" }], await RowsAsync(fake));

        // The key is exact: another case or a trailing space is another key.
        Assert.Equal(0, fake.Remove("sample_to_oven", "SMP9999.01"));
        Assert.Equal(0, fake.Remove("SAMPLE_TO_OVEN", "SMP9999.01 "));
        Assert.Equal(2, fake.Remove("SAMPLE_TO_OVEN", "SMP9999.01"));
        Assert.Equal(seed, await RowsAsync(fake));
    }

    [Fact]
    public async Task FailuresAreUsedUpOneReadAtATime()
    {
        var fake = Shipped();

        fake.FailNext(2, "ORA-03113: end-of-file on communication channel");

        for (var i = 0; i < 2; i++)
        {
            var e = await Assert.ThrowsAsync<IOException>(() => fake.ReadAsync(Cancel));
            Assert.Equal("ORA-03113: end-of-file on communication channel", e.Message);
        }

        Assert.NotEmpty(await RowsAsync(fake));
        Assert.Equal(0, fake.State().FailuresPending);
    }

    [Fact]
    public async Task AMalformedReadCarriesARowOneValueShort()
    {
        var fake = Shipped();
        var seed = await RowsAsync(fake);

        fake.MalformedNext(1);
        var malformed = await fake.ReadAsync(Cancel);

        Assert.False(malformed.TryGetRows(out _, out var error));
        Assert.Equal($"Row {seed.Count + 1} has 6 values for 7 columns.", error);
        Assert.Equal(seed, await RowsAsync(fake));
    }

    [Fact]
    public async Task ResetGoesBackToTheSeedWithNothingPending()
    {
        var fake = Shipped();
        var seed = await RowsAsync(fake);
        fake.Add(new MesRow("X", "Y", null, null, null, null, null));
        fake.FailNext(3, null);
        fake.MalformedNext(3);

        fake.Reset();

        var state = fake.State();
        Assert.Equal(seed, state.Rows);
        Assert.Equal(0, state.FailuresPending);
        Assert.Equal(0, state.MalformedPending);
        Assert.Equal(seed, await RowsAsync(fake));
    }
}
