namespace Simple.Cli.Instances;

/// <summary>
/// What <c>simple up</c> started for one instance, written to the instance's
/// state file. <c>simple down</c> stops exactly these processes and nothing
/// else.
/// </summary>
public sealed record InstanceState(
    string Instance,
    string Repository,
    string SqlServer,
    DateTimeOffset StartedAt,
    IReadOnlyList<int> Ports,
    IReadOnlyList<ProcessRecord> Processes)
{
    public string Database => SqlServerTarget.DatabaseName(Instance);
}

/// <summary>
/// One process this instance started. The start time tells a recorded process
/// apart from a later process that reuses its id.
/// </summary>
/// <param name="Name">The process's name within the instance, for example <c>server</c>.</param>
/// <param name="Component">The component it runs (see <see cref="Components"/>).</param>
/// <param name="Index">1-based among the processes of its component.</param>
public sealed record ProcessRecord(
    string Name,
    string Component,
    int Index,
    int Pid,
    DateTimeOffset ProcessStartedAt,
    string Executable,
    IReadOnlyList<int> Ports,
    string Log);
