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

        var source = builder.DataSource;
        var colon = source.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon <= 3)
        {
            source = source[(colon + 1)..]; // tcp:, np:, lpc:
        }

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            source = source[..comma];
        }

        var slash = source.IndexOf('\\', StringComparison.Ordinal);
        var host = slash >= 0 ? source[..slash] : source;
        var instanceName = slash >= 0 ? source[(slash + 1)..] : string.Empty;

        if (string.Equals(host, "(localdb)", StringComparison.OrdinalIgnoreCase) && instanceName.Length > 0)
        {
            return null;
        }

        if (LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase) && instanceName.Length > 0)
        {
            return null;
        }

        return $"{Variable} must name LocalDB or a named SQL Server instance on this machine (for example .\\SQLEXPRESS), "
            + $"not '{builder.DataSource}'. The command line never uses another machine's server or this machine's default instance.";
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
