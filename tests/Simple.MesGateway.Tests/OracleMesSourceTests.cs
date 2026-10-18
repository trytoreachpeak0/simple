using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Oracle;
using Simple.MesGateway.Rounds;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Tests;

/// <summary>
/// The real MES source against a scripted driver (<see cref="FakeDbProvider"/>):
/// what it sends, how it reads the result, and how each way of failing ends.
/// The real MES is never reached.
/// </summary>
public sealed class OracleMesSourceTests
{
    internal const string Secret = "Oracle-test-secret";

    private readonly FakeDbProvider provider = new();
    private readonly FakeTimeProvider time = new();
    private readonly GatewaySettings settings = Settings();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    internal static GatewaySettings Settings() => new()
    {
        Source = GatewaySettings.OracleSource,
        Oracle =
        {
            DataSource = "127.0.0.1:1521/FAKEMES",
            User = "simple_reader",
            Password = Secret,
        },
    };

    private OracleMesSource Source() => new(Options.Create(settings), provider, time);

    [Fact]
    public async Task ReadsEveryRowOfTheApprovedQueryOnceAndClosesTheConnection()
    {
        var dates = new DateTime(2026, 10, 18, 8, 30, 0);
        provider.Result = FakeDbProvider.Table(
            [.. MesColumns.All],
            ["STAGING_TO_WIRE", "LOT123.01", "N3-7", "WB-07", "焊线", dates, "SOT-23"],
            ["WIRE_TO_GATE", "LOT124.01", null, null, "焊线关卡", null, null]);

        var result = await Source().ReadAsync(Cancel);

        Assert.Equal(MesColumns.All, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["STAGING_TO_WIRE", "LOT123.01", "N3-7", "WB-07", "焊线", dates, "SOT-23"], result.Rows[0]);
        Assert.Equal(["WIRE_TO_GATE", "LOT124.01", null, null, "焊线关卡", null, null], result.Rows[1]);
        Assert.True(result.TryGetRows(out var rows, out var error), error);
        Assert.Equal(new MesRow("STAGING_TO_WIRE", "LOT123.01", "N3-7", "WB-07", "焊线", "2026-10-18T08:30:00", "SOT-23"), rows[0]);

        // One connection, one command: the approved query as it is, with the query timeout.
        var connection = Assert.Single(provider.Connections);
        var command = Assert.Single(provider.Commands);
        Assert.Equal(MesTaskUnionQuery.Load(), command.CommandText);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Equal(30, command.CommandTimeout);
        Assert.Equal(1, command.Executions);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task TheConnectionStringCarriesTheSettingsAndAtMostTwoConnections()
    {
        settings.Oracle.ConnectTimeout = TimeSpan.FromSeconds(12);

        await Source().ReadAsync(Cancel);

        var connection = new DbConnectionStringBuilder { ConnectionString = Assert.Single(provider.Connections).ConnectionString };
        Assert.Equal("simple_reader", connection["User Id"]);
        Assert.Equal(Secret, connection["Password"]);
        Assert.Equal("127.0.0.1:1521/FAKEMES", connection["Data Source"]);
        Assert.Equal("12", connection["Connection Timeout"]);
        Assert.Equal("2", connection["Max Pool Size"]);
        Assert.Equal("True", connection["Pooling"].ToString(), ignoreCase: true);
        // Never part of a transaction someone else started: the source only reads.
        Assert.Equal("False", connection["Enlist"].ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task TheQueryTimeoutIsRoundedUpToWholeSeconds()
    {
        settings.Oracle.QueryTimeout = TimeSpan.FromSeconds(4.2);

        await Source().ReadAsync(Cancel);

        Assert.Equal(5, Assert.Single(provider.Commands).CommandTimeout);
    }

    [Fact]
    public async Task AConnectionTheMesRefusesFailsTheRead()
    {
        provider.Open = _ => throw new FakeDbException("ORA-12541: Cannot connect. No listener at host 127.0.0.1 port 1521.");

        var failure = await Assert.ThrowsAsync<FakeDbException>(() => Source().ReadAsync(Cancel));

        Assert.StartsWith("ORA-12541", failure.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Commands);
        Assert.True(Assert.Single(provider.Connections).WasDisposed);
    }

    [Fact]
    public async Task ADriverThatCannotGiveAConnectionFailsTheRead()
    {
        provider.CreateConnectionFails = new TypeInitializationException("OracleInternal.Common.ProviderConfig", new IOException("driver files missing"));

        await Assert.ThrowsAsync<TypeInitializationException>(() => Source().ReadAsync(Cancel));
    }

    /// <summary>A read stuck anywhere (connecting, querying, fetching) ends as a failed read when its time is up, so the round fails and is retried (ADR 0027).</summary>
    [Fact]
    public async Task AReadThatHangsFailsWhenItsTimeIsUp()
    {
        var abandoned = new TaskCompletionSource();
        provider.Open = async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                abandoned.TrySetResult();
            }
        };

        var read = Source().ReadAsync(Cancel);
        Assert.Equal(TimeSpan.FromSeconds(50), settings.Oracle.ReadTimeout);
        time.Advance(settings.Oracle.ReadTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => read);
        Assert.Contains("50 s", failure.Message, StringComparison.Ordinal);
        // The driver is told to stop as well, so the stuck call can let go of its connection.
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    /// <summary>Oracle cancels a query that runs past its command timeout with ORA-01013; that is a timeout, not a person cancelling.</summary>
    [Fact]
    public async Task OracleCancellingTheQueryAtItsTimeoutIsATimeout()
    {
        provider.Execute = (_, _) => throw OracleErrors.Create(1013, "user requested cancel of current operation");

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => Source().ReadAsync(Cancel));

        Assert.Contains("30 s", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ORA-01013", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Only Oracle's error number 1013 means its command timeout ran out; another error that mentions it is passed on as it is.</summary>
    [Fact]
    public async Task OnlyOraclesErrorNumberMakesATimeout()
    {
        provider.Execute = (_, _) => throw OracleErrors.Create(12541, "Cannot connect. ORA-01013 mentioned in passing");

        var failure = await Assert.ThrowsAsync<global::Oracle.ManagedDataAccess.Client.OracleException>(() => Source().ReadAsync(Cancel));

        Assert.Equal(12541, failure.Number);
    }

    [Fact]
    public async Task StoppingTheGatewayCancelsTheRead()
    {
        provider.Open = token => Task.Delay(Timeout.Infinite, token);
        using var stopping = new CancellationTokenSource();

        var read = Source().ReadAsync(stopping.Token);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    /// <summary>The source's failures land on the existing rounds: read failures and format failures, the credential never shown.</summary>
    [Theory]
    [InlineData("refused", MesReadFailure.Read, "ORA-12541")]
    [InlineData("driver", MesReadFailure.Read, "driver files missing")]
    [InlineData("credential in the message", MesReadFailure.Read, "Password=***")]
    [InlineData("timeout", MesReadFailure.Read, "did not finish within 30 s")]
    [InlineData("six columns", MesReadFailure.Format, "The result has no column PACKAGE.")]
    [InlineData("a number", MesReadFailure.Format, "Row 1 has a value of type Decimal in SUBLOT")]
    public async Task EachFailureIsAFailedRoundAndTheGatewayGoesOn(string failure, string kind, string message)
    {
        switch (failure)
        {
            case "refused":
                provider.Open = _ => throw new FakeDbException("ORA-12541: Cannot connect. No listener at host 127.0.0.1 port 1521.");
                break;
            case "driver":
                provider.CreateConnectionFails = new InvalidOperationException("driver files missing");
                break;
            case "credential in the message":
                provider.Open = _ => throw new FakeDbException($"Connection failed: User Id=simple_reader;Password={Secret};Data Source=127.0.0.1:1521/FAKEMES");
                break;
            case "timeout":
                provider.Execute = (_, _) => throw OracleErrors.Create(1013, "user requested cancel of current operation");
                break;
            case "six columns":
                provider.Result = FakeDbProvider.Table([.. MesColumns.All.SkipLast(1)], ["T", "S", "A", "E", "ST", null]);
                break;
            default:
                provider.Result = FakeDbProvider.Table([.. MesColumns.All], ["T", 123.01m, "A", "E", "ST", null, "P"]);
                break;
        }

        var snapshots = new SnapshotStore();
        settings.RetryDelay = TimeSpan.FromSeconds(1);
        using var reader = new RoundReader(Source(), snapshots, Options.Create(settings), time, NullLogger<RoundReader>.Instance);
        await reader.StartAsync(Cancel);
        for (var i = 0; i < 200 && snapshots.Latest.Round < 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, Cancel);
        }

        await reader.StopAsync(Cancel);

        var latest = snapshots.Latest;
        Assert.True(latest.Round >= 2, $"The gateway stopped reading after round {latest.Round}.");
        Assert.False(latest.Succeeded);
        Assert.Null(latest.Rows);
        Assert.Equal(kind, latest.Failure!.Kind);
        Assert.Contains(message, latest.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, latest.Failure.Message, StringComparison.Ordinal);
    }
}
