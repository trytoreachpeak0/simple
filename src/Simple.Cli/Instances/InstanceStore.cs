using System.Text.Json;

namespace Simple.Cli.Instances;

/// <summary>
/// The command line's own files, outside every clone: one directory per
/// instance under <c>&lt;home&gt;/instances/</c>, holding its state file and
/// its logs. Home is <c>SIMPLE_HOME</c>, or <c>%LOCALAPPDATA%\simple</c>.
/// The copy of the build output an instance runs from is large and stays
/// inside the clone, under <c>artifacts/</c>.
/// </summary>
public sealed class InstanceStore(string home)
{
    public const string HomeVariable = "SIMPLE_HOME";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { WriteIndented = true };

    public string Home { get; } = Path.GetFullPath(home);

    /// <summary>How long deleting an instance's build output waits for files that are still held.</summary>
    public TimeSpan DeleteWait { get; init; } = TimeSpan.FromSeconds(10);

    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "simple");

    public string InstanceDirectory(string instance) => Path.Combine(Home, "instances", instance);

    public string StatePath(string instance) => Path.Combine(InstanceDirectory(instance), "state.json");

    private string SqlServerPath(string instance) => Path.Combine(InstanceDirectory(instance), "sqlserver.txt");

    /// <summary>The SQL Server the instance's database was created on, written at up and kept until down --reset drops it.</summary>
    public string? ReadSqlServer(string instance)
    {
        var path = SqlServerPath(instance);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteSqlServer(string instance, string sqlServer)
    {
        Directory.CreateDirectory(InstanceDirectory(instance));
        File.WriteAllText(SqlServerPath(instance), sqlServer);
    }

    public void ForgetSqlServer(string instance) => File.Delete(SqlServerPath(instance));

    private string ServerKeyStorePath(string instance) => Path.Combine(InstanceDirectory(instance), "serverkey.txt");

    /// <summary>
    /// The key store the instance's server key was made in, written at up and
    /// kept until down --reset deletes the key; null when there is no record.
    /// </summary>
    /// <exception cref="InvalidDataException">The record holds something else.</exception>
    public ServerKeyStore? ReadServerKeyStore(string instance)
    {
        var path = ServerKeyStorePath(instance);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return ServerKey.TryParse(text, out var store)
            ? store
            : throw new InvalidDataException($"{path} holds '{text}', neither User nor Machine.");
    }

    public void WriteServerKeyStore(string instance, ServerKeyStore store)
    {
        Directory.CreateDirectory(InstanceDirectory(instance));
        File.WriteAllText(ServerKeyStorePath(instance), store.ToString());
    }

    public void ForgetServerKeyStore(string instance) => File.Delete(ServerKeyStorePath(instance));

    /// <summary>
    /// The instance's administrator password (ADR 0045): made at random by
    /// the first up, set in the server by every up, kept outside the clone
    /// until down --reset drops the database it belongs to.
    /// </summary>
    public string AdminPasswordPath(string instance) => Path.Combine(InstanceDirectory(instance), "admin-password.txt");

    public string? ReadAdminPassword(string instance)
    {
        var path = AdminPasswordPath(instance);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public string ReadOrCreateAdminPassword(string instance)
    {
        if (ReadAdminPassword(instance) is { Length: > 0 } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(InstanceDirectory(instance));
        var password = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(AdminPasswordPath(instance), password);
        return password;
    }

    public void ForgetAdminPassword(string instance) => File.Delete(AdminPasswordPath(instance));

    public string LogDirectory(string instance) => Path.Combine(InstanceDirectory(instance), "logs");

    /// <summary>What a process wrote to its console (stdout and stderr), or the last build's output; written anew at each up.</summary>
    public string LogPath(string instance, string processName) => Path.Combine(LogDirectory(instance), $"{processName}.log");

    /// <summary>
    /// A process's program log directory (ADR 0034), given to it as
    /// <c>--Simple:Logs:Directory</c>. The program limits its size itself and
    /// keeps it from one up to the next.
    /// </summary>
    public string ProgramLogDirectory(string instance, string processName) => Path.Combine(LogDirectory(instance), processName);

    /// <summary>A process's own files, for example an onboard terminal's settings. Kept after down; up writes them anew.</summary>
    public string ProcessDirectory(string instance, string processName) => Path.Combine(InstanceDirectory(instance), "processes", processName);

    /// <summary>Where a component's build output is copied to, so a rebuild in the clone never meets a locked file.</summary>
    public static string BinDirectory(string repository, string instance, string component) =>
        Path.Combine(RunDirectory(repository, instance), component);

    public static string RunDirectory(string repository, string instance) =>
        Path.Combine(repository, "artifacts", "simple", instance);

    public InstanceState? Read(string instance)
    {
        var path = StatePath(instance);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<InstanceState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"The state file {path} is empty.");
    }

    public void Write(InstanceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = StatePath(state.Instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Forgets a stopped instance: the build output it ran from, then its
    /// state file. Its logs stay. The state file goes last, so an instance
    /// whose files are still held stays recorded and the next down tries again.
    /// </summary>
    /// <exception cref="FilesHeldException">Files in the build output were still held after <see cref="DeleteWait"/>; the state file is kept.</exception>
    public async Task DeleteAsync(InstanceState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await DirectoryTree.DeleteAsync(RunDirectory(state.Repository, state.Instance), DeleteWait, cancellationToken);
        File.Delete(StatePath(state.Instance));
    }

    /// <summary>Ports recorded by every other instance with a state file, running or not.</summary>
    public IReadOnlySet<int> PortsClaimedByOthers(string instance)
    {
        var claimed = new HashSet<int>();
        var instances = Path.Combine(Home, "instances");
        if (!Directory.Exists(instances))
        {
            return claimed;
        }

        foreach (var directory in Directory.EnumerateDirectories(instances))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, instance, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Read(name) is { } other)
                {
                    claimed.UnionWith(other.Ports);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
            {
                // A state file being rewritten or broken claims nothing; the
                // port probe still keeps ports in use from being handed out.
            }
        }

        return claimed;
    }

    /// <summary>
    /// Holds this instance for one up or down, so two commands never start or
    /// stop the same instance at once. Waits for the holder to finish.
    /// </summary>
    public Task<IDisposable?> LockInstanceAsync(string instance, TimeSpan wait, CancellationToken cancellationToken) =>
        LockAsync(Path.Combine(InstanceDirectory(instance), "lock"), wait, cancellationToken);

    /// <summary>Held while ports are chosen and recorded, so two instances never choose the same ones.</summary>
    public Task<IDisposable?> LockPortsAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        LockAsync(Path.Combine(Home, "ports.lock"), wait, cancellationToken);

    /// <summary>
    /// Held while one clone's components are built and copied: two instances
    /// started from one clone at once would otherwise build the same project
    /// into the same output at the same time.
    /// </summary>
    public static Task<IDisposable?> LockBuildAsync(string repository, TimeSpan wait, CancellationToken cancellationToken) =>
        LockAsync(Path.Combine(repository, "artifacts", "simple", "build.lock"), wait, cancellationToken);

    private static async Task<IDisposable?> LockAsync(string path, TimeSpan wait, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            try
            {
                // The operating system releases the lock when the process
                // ends, so a crashed command never leaves the instance locked.
                // The file stays behind: deleting it on close would make a
                // waiting opener fail with access denied while it is pending
                // deletion.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
