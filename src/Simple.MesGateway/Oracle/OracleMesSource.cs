using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Oracle;

/// <summary>
/// Reads the customer's Oracle MES: one connection, the approved query
/// (<see cref="MesTaskUnionQuery"/>) and nothing else, every row of its
/// result as the driver gives it. The driver is Oracle's managed one in Thin
/// mode (<see cref="OracleClientFactory"/>); tests put a scripted one in its
/// place. Every failure, a read that runs out of time included, is thrown, so
/// the round fails and is retried (ADR 0027).
/// </summary>
/// <remarks>
/// Read-only by construction: the only text the source ever sends is the
/// approved query, as a query that returns rows; it starts no transaction and
/// binds no parameters. Whether the account the MES gives could write is
/// for the customer's IT to settle (the gateway README).
/// </remarks>
public sealed class OracleMesSource(IOptions<GatewaySettings> settings, DbProviderFactory provider, TimeProvider time) : IMesSource
{
    /// <summary>
    /// The most connections the gateway holds to the MES. Rounds run one at a
    /// time; the second is for a round that starts while a read that ran out
    /// of time still holds the first, until the driver's own timeouts let go.
    /// </summary>
    public const int MaxConnections = 2;

    /// <summary>ORA-01013, "user requested cancel of current operation": what Oracle reports when a command timeout runs out.</summary>
    private const int UserRequestedCancel = 1013;

    public async Task<MesReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var oracle = settings.Value.Oracle;
        var sql = MesTaskUnionQuery.Load();

        // The driver's own timeouts may not stop every stuck call (a server that
        // takes the connection and says nothing), so the read as a whole has one too.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = ReadOnceAsync(oracle, sql, stop.Token);
        try
        {
            return await read.WaitAsync(oracle.ReadTimeout, time, cancellationToken);
        }
        catch (TimeoutException) when (!read.IsCompleted)
        {
            await stop.CancelAsync();
            throw new TimeoutException(
                $"The MES read did not finish within {Seconds(oracle.ReadTimeout)} s (connect timeout {Seconds(oracle.ConnectTimeout)} s, query timeout {Seconds(oracle.QueryTimeout)} s); it was abandoned.");
        }
        finally
        {
            // The abandoned read still holds the token until it lets go; its own failure is not this round's.
            _ = read.ContinueWith(
                finished =>
                {
                    _ = finished.Exception;
                    stop.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<MesReadResult> ReadOnceAsync(OracleSettings oracle, string sql, CancellationToken cancellationToken)
    {
        await using var connection = provider.CreateConnection()
            ?? throw new InvalidOperationException("The Oracle driver gave no connection.");
        connection.ConnectionString = new OracleConnectionStringBuilder
        {
            DataSource = oracle.DataSource,
            UserID = oracle.User,
            Password = oracle.Password,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = MaxConnections,
            ConnectionTimeout = WholeSeconds(oracle.ConnectTimeout),
            // Never part of a transaction something else started: the source only reads.
            Enlist = "false",
        }.ConnectionString;
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = WholeSeconds(oracle.QueryTimeout);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = reader.GetName(i);
            }

            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object?[columns.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
                }

                rows.Add(values);
            }

            return new MesReadResult(columns, rows);
        }
        catch (OracleException e) when (e.Number == UserRequestedCancel && !cancellationToken.IsCancellationRequested)
        {
            // Oracle stops a query that runs past the command timeout and reports it as a cancel.
            throw new TimeoutException($"The MES query did not finish within {command.CommandTimeout} s; Oracle stopped it (ORA-01013).", e);
        }
    }

    /// <summary>The driver takes whole seconds; a part of one counts as a whole one, so a timeout is never shortened.</summary>
    private static int WholeSeconds(TimeSpan span) => (int)Math.Ceiling(span.TotalSeconds);

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
