using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Simple.Logging;

namespace Simple.OnboardTerminal.Headless.Tests;

/// <summary>The headless terminal, run as a process, and what its test input listens on.</summary>
public sealed class TestInputTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTestInputListensOnItsLoopbackAddressOnlyWhateverTheEnvironmentSays()
    {
        var named = FreePort();
        await using var terminal = await RunningTerminal.StartAsync(start =>
        {
            // What the review found (#38): ASP.NET Core reads these and Kestrel
            // would open the endpoint they name, besides the test input. The
            // address is loopback here only so the test opens nothing itself.
            start.Environment["Kestrel__Endpoints__Wide__Url"] = $"http://127.0.0.1:{named}";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{named}";
        });

        Assert.False(Listens(named), $"Something listens on 127.0.0.1:{named}, the port the environment named.");
    }

    [Fact]
    public async Task TheTerminalWritesItsProgramLogWhereItsCommandLineSays()
    {
        await using var terminal = await RunningTerminal.StartAsync();

        var file = Assert.Single(ProgramLog.Files(terminal.LogDirectory));
        Assert.Equal("OnboardTerminal.log", Path.GetFileName(file));
        // The file is still open in the terminal, which shares it for reading.
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains(
            "[INF] Simple.OnboardTerminal.Core.TerminalConnection: Onboard terminal for vehicle vehicle-1, server https://127.0.0.1:1",
            await reader.ReadToEndAsync(Cancel),
            StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool Listens(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The terminal for vehicle-1, pointed at a server that is not there (it
    /// keeps retrying, which is enough here), started until its test input
    /// answers. Disposing kills it and deletes its directory.
    /// </summary>
    private sealed class RunningTerminal : IAsyncDisposable
    {
        private readonly string directory;
        private readonly Process process;
        private readonly Task<string> output;
        private readonly Task<string> errors;

        private RunningTerminal(string directory, Process process)
        {
            this.directory = directory;
            this.process = process;
            output = process.StandardOutput.ReadToEndAsync(Cancel);
            errors = process.StandardError.ReadToEndAsync(Cancel);
        }

        public string LogDirectory => Path.Combine(directory, "logs");

        public static async Task<RunningTerminal> StartAsync(Action<ProcessStartInfo>? configure = null)
        {
            var input = FreePort();
            var directory = Path.Combine(Path.GetTempPath(), $"simple-headless-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var config = Path.Combine(directory, "terminal.json");
            await File.WriteAllTextAsync(
                config, $$"""{"vehicleId": "vehicle-1", "server": "https://127.0.0.1:1", "serverCertificatePin": "{{new string('a', 64)}}"}""", Cancel);

            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    Path.Combine(AppContext.BaseDirectory, "Simple.OnboardTerminal.Headless.dll"),
                    $"--config={config}",
                    $"--test-input=http://127.0.0.1:{input}",
                    $"--Simple:Logs:Directory={Path.Combine(directory, "logs")}",
                },
            };
            configure?.Invoke(start);

            var terminal = new RunningTerminal(directory, Process.Start(start)!);
            try
            {
                await terminal.WaitForTestInputAsync(input);
                return terminal;
            }
            catch
            {
                await terminal.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Cancel);
            }

            await Task.WhenAll(output, errors);
            process.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file just closed can still be held for a moment
                // (a virus scan, or a process that has it loaded), and a leftover temp folder does no harm.
            }
        }

        private async Task WaitForTestInputAsync(int input)
        {
            // Waits up to 30 s for the test input to answer. On Windows a
            // connection to a loopback port nobody listens on yet is refused
            // only after about 2 s (the connection is tried again), so a
            // request timeout of 2 s ended the wait whenever the terminal took
            // longer than that to start (#60): each request now gets 10 s, and
            // one that times out counts as not answering yet.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                if (process.HasExited)
                {
                    Assert.Fail($"The terminal exited: {await errors}");
                }

                try
                {
                    using var status = await http.GetAsync(new Uri($"http://127.0.0.1:{input}/test/status"), Cancel);
                    if (status.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                catch (TaskCanceledException) when (!Cancel.IsCancellationRequested)
                {
                    // Not listening or not answering yet.
                }

                Assert.True(DateTime.UtcNow < deadline, "The test input did not answer within 30 s.");
                await Task.Delay(200, Cancel);
            }
        }
    }
}
