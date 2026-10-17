using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Simple.Cli.Instances;
using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

/// <summary>
/// Runs the built command line as a process, the way a person or CI does, with
/// a home directory of its own. Disposing kills whatever its instances left
/// running (by the process ids in their state files, with their process
/// trees), drops their databases and deletes the home directory.
/// </summary>
/// <param name="cancellationToken">Ends every command and query when the test is cancelled or times out.</param>
public sealed class CliRunner(CancellationToken cancellationToken) : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(4);

    private readonly List<string> instances = [];

    /// <summary>
    /// The SQL Server instance the databases are created on: LocalDB unless
    /// SIMPLE_TEST_SQLSERVER names another, as for every test that needs a
    /// database (CI uses .\SQLEXPRESS). Handed to the command line as
    /// SIMPLE_SQLSERVER.
    /// </summary>
    public static string SqlServer { get; } =
        Environment.GetEnvironmentVariable("SIMPLE_TEST_SQLSERVER") is { Length: > 0 } configured
            ? configured
            : SqlServerTarget.LocalDb;

    public string Home { get; } = Path.Combine(Path.GetTempPath(), $"simple-cli-test-{Guid.NewGuid():N}");

    public string Repository { get; } = CliEnvironment.FindRepository(AppContext.BaseDirectory);

    /// <summary>A random instance name whose database, Simple_test_…, the CI leftover check knows.</summary>
    public string NewInstance()
    {
        var instance = $"test_{Guid.NewGuid():N}"[..17];
        instances.Add(instance);
        return instance;
    }

    public async Task<(int ExitCode, JsonElement Output, string Error)> RunJsonAsync(params string[] args)
    {
        var (exitCode, stdout, stderr) = await RunAsync([.. args, "--json"]);
        using var document = JsonDocument.Parse(stdout);
        return (exitCode, document.RootElement.Clone(), stderr);
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Repository,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "simple.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[InstanceStore.HomeVariable] = Home;
        start.Environment[SqlServerTarget.Variable] = SqlServer;

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            // Reading to the end also proves the components did not inherit
            // the command line's stdout: if one had, the pipe would stay open
            // after the command line exits and this would not return.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"simple {string.Join(' ', args)} did not finish within {CommandTimeout}.");
        }
    }

    public async Task<bool> DatabaseExistsAsync(string instance)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", SqlServerTarget.DatabaseName(instance));
        return await command.ExecuteScalarAsync(cancellationToken) is not DBNull;
    }

    public async Task<int> CountEventsAsync(string instance, string type, int processId)
    {
        var builder = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = SqlServerTarget.DatabaseName(instance), Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Events WHERE Type = @type AND JSON_VALUE(Data, '$.processId') = @pid";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        var store = new InstanceStore(Home);
        foreach (var instance in instances)
        {
            if (store.Read(instance) is { } state)
            {
                foreach (var record in state.Processes)
                {
                    using var process = ChildProcess.Find(record.Pid, record.ProcessStartedAt);
                    if (process is not null)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }

            await SqlServerTarget.DropDatabaseAsync(SqlServer, instance, CancellationToken.None);
            var run = InstanceStore.RunDirectory(Repository, instance);
            if (Directory.Exists(run))
            {
                Directory.Delete(run, recursive: true);
            }
        }

        if (Directory.Exists(Home))
        {
            Directory.Delete(Home, recursive: true);
        }
    }
}
