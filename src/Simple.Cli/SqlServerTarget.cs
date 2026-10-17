using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Simple.Cli;

/// <summary>
/// The SQL Server instance an instance's database lives on: LocalDB by
/// default, or another instance on this machine named in
/// <c>SIMPLE_SQLSERVER</c> (CI uses <c>.\SQLEXPRESS</c>). The command line
/// refuses anything else, so it can never create or drop a database on a
/// shared server or on this machine's default instance.
/// </summary>
public static partial class SqlServerTarget
{
    public const string Variable = "SIMPLE_SQLSERVER";
    public const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";

    private const string DefaultInstanceName = "MSSQLSERVER";

    private static readonly string[] LoopbackHosts = [".", "(local)", "localhost", "127.0.0.1", "::1", "[::1]"];

    public static string DatabaseName(string instance) => $"Simple_{instance}";

    public static bool IsValidInstanceName(string? instance) => instance is not null && InstanceName().IsMatch(instance);

    /// <summary>Turns any folder name into a valid instance name.</summary>
    public static string InstanceNameFrom(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var name = InvalidInstanceCharacters().Replace(folder, "-").Trim('-');
        name = name.Length > 50 ? name[..50] : name;
        return name.Length == 0 ? "dev" : name;
    }

    public static string Configured() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured ? configured : LocalDb;

    /// <summary>Why this connection string may not be used, or null when it may.</summary>
    public static string? Refusal(string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException e)
        {
            return $"{Variable} cannot be parsed: {e.Message}";
        }

        if (!string.IsNullOrEmpty(builder.InitialCatalog) || !string.IsNullOrEmpty(builder.AttachDBFilename))
        {
            return $"{Variable} names a database. Leave it out: the database is Simple_<instance>.";
        }

        // The connection string is kept in the instance's state files, so it
        // must not carry a password: locally only Windows authentication.
        if (!string.IsNullOrEmpty(builder.UserID) || !string.IsNullOrEmpty(builder.Password))
        {
            return $"{Variable} carries a SQL login (User ID or Password). Leave them out and use Integrated Security=true: the command line keeps the connection string in its state files.";
        }

        if (!string.IsNullOrEmpty(builder.FailoverPartner))
        {
            return $"{Variable} names a failover partner ('{builder.FailoverPartner}'). Leave it out: the command line uses this machine's server only.";
        }

        var source = builder.DataSource;
        var colon = source.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon <= 3)
        {
            source = source[(colon + 1)..]; // tcp:, np:, lpc:
        }

        // With a port, SqlClient ignores the instance name and connects to
        // whatever listens on that port; on a development machine 1433 is
        // usually the default instance.
        if (source.Contains(',', StringComparison.Ordinal))
        {
            return $"{Variable} gives a port ('{builder.DataSource}'). Leave it out: name the instance instead, for example .\\SQLEXPRESS.";
        }

        var slash = source.IndexOf('\\', StringComparison.Ordinal);
        var host = slash >= 0 ? source[..slash] : source;
        var instanceName = slash >= 0 ? source[(slash + 1)..] : string.Empty;

        if (string.Equals(host, "(localdb)", StringComparison.OrdinalIgnoreCase) && instanceName.Length > 0)
        {
            return null;
        }

        if (LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            && instanceName.Length > 0
            && !string.Equals(instanceName, DefaultInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{Variable} must name LocalDB or a named SQL Server instance on this machine (for example .\\SQLEXPRESS), "
            + $"not '{builder.DataSource}'. The command line never uses another machine's server or this machine's default instance.";
    }

    /// <summary>
    /// Asks the server itself which instance answered, and returns why it may
    /// not be used, or null when it may. Run before a server is started on it
    /// and before a database is dropped on it: the connection string alone
    /// cannot tell an alias or a forwarded port from the instance it names.
    /// </summary>
    /// <exception cref="SqlException">The server could not be reached.</exception>
    public static async Task<string?> VerifyAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(SERVERPROPERTY('IsLocalDB') AS int), CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), CAST(SERVERPROPERTY('MachineName') AS nvarchar(128))";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Judge(
            isLocalDb: !reader.IsDBNull(0) && reader.GetInt32(0) == 1,
            instanceName: reader.IsDBNull(1) ? null : reader.GetString(1),
            machineName: reader.IsDBNull(2) ? null : reader.GetString(2),
            localMachine: Environment.MachineName,
            dataSource: builder.DataSource);
    }

    /// <summary>The rule <see cref="VerifyAsync"/> applies to what the server says about itself.</summary>
    public static string? Judge(bool isLocalDb, string? instanceName, string? machineName, string localMachine, string dataSource)
    {
        if (isLocalDb)
        {
            return null;
        }

        if (string.IsNullOrEmpty(instanceName) || string.Equals(instanceName, DefaultInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{dataSource}' answered as the default instance of {machineName}. The command line uses LocalDB or a named instance only.";
        }

        if (!string.Equals(machineName, localMachine, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{dataSource}' answered as {machineName}\\{instanceName}, not as an instance on this machine ({localMachine}).";
        }

        return null;
    }

    /// <summary>The server part of the connection string, safe to print.</summary>
    public static string DataSource(string connectionString) => new SqlConnectionStringBuilder(connectionString).DataSource;

    /// <summary>Drops the instance's database if it exists. Returns whether there was one.</summary>
    public static async Task<bool> DropDatabaseAsync(string connectionString, string instance, CancellationToken cancellationToken)
    {
        if (!IsValidInstanceName(instance))
        {
            throw new ArgumentException($"'{instance}' is not a valid instance name.", nameof(instance));
        }

        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF DB_ID(@name) IS NULL
                SELECT 0;
            ELSE
            BEGIN
                DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
                SELECT 1;
            END
            """;
        command.Parameters.AddWithValue("@name", DatabaseName(instance));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The same rule the server applies to Simple:Instance.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,50}$")]
    private static partial Regex InstanceName();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex InvalidInstanceCharacters();
}
