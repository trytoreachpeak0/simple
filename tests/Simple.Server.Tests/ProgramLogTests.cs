using Simple.Logging;

namespace Simple.Server.Tests;

/// <summary>The server writes its program log where Simple:Logs:Directory says (ADR 0034).</summary>
public sealed class ProgramLogTests
{
    [Fact]
    public async Task TheServerWritesItsProgramLogFile()
    {
        await using var server = new ServerInstance();
        using (var client = server.Start())
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
            live.EnsureSuccessStatusCode();
        }

        await server.StopAsync();

        var file = Assert.Single(ProgramLog.Files(server.LogDirectory));
        Assert.Equal("Server.log", Path.GetFileName(file));
        Assert.Contains(
            $"[INF] Simple.Server.Events.ServerLifecycleRecorder: Bringing database Simple_{server.Instance} to the current schema",
            await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }
}
