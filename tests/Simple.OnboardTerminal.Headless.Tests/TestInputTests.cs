using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Simple.OnboardTerminal.Headless.Tests;

/// <summary>The headless terminal, run as a process, and what its test input listens on.</summary>
public sealed class TestInputTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTestInputListensOnItsLoopbackAddressOnlyWhateverTheEnvironmentSays()
    {
        var input = FreePort();
        var named = FreePort();
        var directory = Path.Combine(Path.GetTempPath(), $"simple-headless-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "terminal.json");
        // A server that is not there: the terminal keeps retrying, which is enough here.
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
            },
        };
        // What the review found (#38): ASP.NET Core reads these and Kestrel
        // would open the endpoint they name, besides the test input. The
        // address is loopback here only so the test opens nothing itself.
        start.Environment["Kestrel__Endpoints__Wide__Url"] = $"http://127.0.0.1:{named}";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{named}";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Cancel);
        var errors = process.StandardError.ReadToEndAsync(Cancel);
        try
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
                        break;
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

            Assert.False(Listens(named), $"Something listens on 127.0.0.1:{named}, the port the environment named.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Cancel);
            }

            await Task.WhenAll(output, errors);
            Directory.Delete(directory, recursive: true);
        }
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
}
