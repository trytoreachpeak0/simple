using System.Reflection;

namespace Simple.Server.Status;

/// <summary>Facts about this server process that do not change while it runs.</summary>
public sealed class ServerInfo(TimeProvider time)
{
    public string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();
}
