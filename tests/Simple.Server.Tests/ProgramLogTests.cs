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
        // In process, the host is released by both the factory and Program; when
        // the factory returns, Program's side may not have released the program
        // log yet (seen once on simple01), so it is read the way simple logs reads.
        Assert.Contains(
            $"[INF] Simple.Server.Events.ServerLifecycleRecorder: Bringing database Simple_{server.Instance} to the current schema",
            ProgramLog.ReadText(server.LogDirectory),
            StringComparison.Ordinal);
    }
}
