using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Simple.MesGateway.Tests;

/// <summary>
/// A database driver that tests script, in place of Oracle's: it records the
/// connections and commands the source makes, and answers each with what the
/// test set up. Only a query that returns rows is answered; anything else the
/// source might send fails the test.
/// </summary>
internal sealed class FakeDbProvider : DbProviderFactory
{
    /// <summary>Thrown by <see cref="CreateConnection"/> instead of giving a connection, as a driver that cannot load would.</summary>
    public Exception? CreateConnectionFails { get; set; }

    /// <summary>Runs when a connection opens; throw to refuse it, wait on the token to hang.</summary>
    public Func<CancellationToken, Task> Open { get; set; } = _ => Task.CompletedTask;

    /// <summary>Answers a query; by default with <see cref="Result"/>.</summary>
    public Func<FakeCommand, CancellationToken, Task<DbDataReader>> Execute { get; set; }

    public DataTable Result { get; set; } = Table(["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"]);

    public List<FakeConnection> Connections { get; } = [];

    public List<FakeCommand> Commands { get; } = [];

    public FakeDbProvider()
    {
        Execute = (_, _) => Task.FromResult<DbDataReader>(Result.CreateDataReader());
    }

    /// <summary>A result with these column names and rows; a null value is the database's NULL.</summary>
    public static DataTable Table(string[] columns, params object?[][] rows)
    {
        var table = new DataTable();
        foreach (var column in columns)
        {
            table.Columns.Add(column, typeof(object));
        }

        foreach (var row in rows)
        {
            table.Rows.Add([.. row.Select(value => value ?? DBNull.Value)]);
        }

        return table;
    }

    public override DbConnection? CreateConnection()
    {
        if (CreateConnectionFails is { } failure)
        {
            throw failure;
        }

        var connection = new FakeConnection(this);
        Connections.Add(connection);
        return connection;
    }
}

/// <summary>An error as a database driver reports it (OracleException is a DbException too).</summary>
internal sealed class FakeDbException(string message) : DbException(message);

[SuppressMessage("Security", "CA2100", Justification = "A fake; it runs nothing.")]
internal sealed class FakeConnection(FakeDbProvider provider) : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    [AllowNull]
    public override string ConnectionString { get; set; } = "";

    public override string Database => "";

    public override string DataSource => "";

    public override string ServerVersion => "fake";

    public override ConnectionState State => state;

    public bool WasDisposed { get; private set; }

    public override void Open() => throw new NotSupportedException("The source opens connections asynchronously.");

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await provider.Open(cancellationToken);
        state = ConnectionState.Open;
    }

    public override void Close() => state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("The source has no business starting a transaction on the MES.");

    protected override DbCommand CreateDbCommand()
    {
        var command = new FakeCommand(provider) { Connection = this };
        provider.Commands.Add(command);
        return command;
    }

    protected override void Dispose(bool disposing)
    {
        state = ConnectionState.Closed;
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class FakeCommand(FakeDbProvider provider) : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = "";

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public int Executions { get; private set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameters();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => throw new NotSupportedException("The source only reads.");

    public override object? ExecuteScalar() => throw new NotSupportedException("The source only reads.");

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => throw new NotSupportedException("The approved query has no parameters.");

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        throw new NotSupportedException("The source reads asynchronously.");

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (DbConnection?.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

        Executions++;
        return provider.Execute(this, cancellationToken);
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot { get; } = new();

        public override int Add(object value) => throw new NotSupportedException();

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear()
        {
        }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index)
        {
        }

        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => throw new NotSupportedException();

        public override void Remove(object value)
        {
        }

        public override void RemoveAt(int index)
        {
        }

        public override void RemoveAt(string parameterName)
        {
        }

        protected override DbParameter GetParameter(int index) => throw new ArgumentOutOfRangeException(nameof(index));

        protected override DbParameter GetParameter(string parameterName) => throw new ArgumentOutOfRangeException(nameof(parameterName));

        protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();

        protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
    }
}
