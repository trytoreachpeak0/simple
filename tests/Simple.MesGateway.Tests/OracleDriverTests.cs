using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Oracle;
using Simple.MesGateway.Rounds;

namespace Simple.MesGateway.Tests;

/// <summary>
/// The real Oracle driver, pointed only at loopback ports with no Oracle
/// behind them: nothing listening, or a listener that never answers. It
/// proves the driver loads, fails the way the rounds expect and never puts
/// the password in what the gateway shows or logs. No MES is reached.
/// </summary>
public sealed class OracleDriverTests
{
    private const string Secret = OracleMesSourceTests.Secret;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static GatewaySettings Settings(int port)
    {
        var settings = OracleMesSourceTests.Settings();
        settings.Oracle.DataSource = $"127.0.0.1:{port}/NOSUCHMES";
        settings.Oracle.ConnectTimeout = TimeSpan.FromSeconds(2);
        settings.Oracle.QueryTimeout = TimeSpan.FromSeconds(2);
        return settings;
    }

    [Fact]
    public async Task NothingListeningIsAFailedReadWithoutThePassword()
    {
        var source = new OracleMesSource(Options.Create(Settings(ClosedLoopbackPort())), OracleClientFactory.Instance, TimeProvider.System);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => source.ReadAsync(Cancel));

        Assert.IsNotType<OperationCanceledException>(failure, exactMatch: false);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A server that takes the connection and then says nothing: the read ends within its time, it does not hang the rounds.</summary>
    [Fact]
    public async Task AServerThatNeverAnswersEndsTheReadWithinItsTime()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = new List<Socket>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await silent.AcceptSocketAsync(Cancel));
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The listener stopped at the end of the test.
            }
        }, Cancel);
        var settings = Settings(((IPEndPoint)silent.LocalEndpoint).Port);
        var source = new OracleMesSource(Options.Create(settings), OracleClientFactory.Instance, TimeProvider.System);

        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => source.ReadAsync(Cancel));
        clock.Stop();

        Assert.IsNotType<OperationCanceledException>(failure, exactMatch: false);
        Assert.True(clock.Elapsed < settings.Oracle.ReadTimeout + TimeSpan.FromSeconds(5), $"The read took {clock.Elapsed}.");
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        silent.Stop();
        await accepting;
        accepted.ForEach(s => s.Dispose());
    }

    /// <summary>
    /// The whole gateway on the Oracle source, set the way a person sets it
    /// for one run: the rounds fail and go on, the fake's control API is not
    /// there, and neither the snapshot nor the program log holds the password.
    /// </summary>
    [Fact]
    public async Task TheGatewayOnTheOracleSourceShowsFailedRoundsAndNeverThePassword()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "simple-gateway-tests", Guid.NewGuid().ToString("N"));
        await using (var factory = new OracleFactory(logDirectory, ClosedLoopbackPort()))
        {
            using var client = factory.CreateClient();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            MesSnapshot snapshot;
            do
            {
                Assert.True(DateTime.UtcNow < deadline, "No second round within 60 s.");
                await Task.Delay(100, Cancel);
                snapshot = (await client.GetFromJsonAsync<MesSnapshot>(new Uri("/api/v1/snapshot", UriKind.Relative), Cancel))!;
            }
            while (snapshot.Round < 2);

            Assert.False(snapshot.Succeeded);
            Assert.Null(snapshot.Rows);
            Assert.Equal(MesReadFailure.Read, snapshot.Failure!.Kind);
            Assert.DoesNotContain(Secret, await client.GetStringAsync(new Uri("/api/v1/snapshot", UriKind.Relative), Cancel), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/fake-mes/state", UriKind.Relative), Cancel)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancel)).StatusCode);
        }

        var log = Simple.Logging.ProgramLog.ReadText(logDirectory);
        Assert.Contains("failed (read)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        try
        {
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder does no harm.
        }
    }

    /// <summary>A loopback port nothing listens on: taken from the system, then let go.</summary>
    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class OracleFactory(string logDirectory, int port) : WebApplicationFactory<Program>
    {
        // UseSetting, not ConfigureAppConfiguration: the gateway picks its source
        // from the configuration before the host is built.
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder
                .UseSetting("Simple:Logs:Directory", logDirectory)
                .UseSetting("Simple:Mes:Source", "Oracle")
                .UseSetting("Simple:Mes:Oracle:DataSource", $"127.0.0.1:{port}/NOSUCHMES")
                .UseSetting("Simple:Mes:Oracle:User", "simple_reader")
                .UseSetting("Simple:Mes:Oracle:Password", Secret)
                .UseSetting("Simple:Mes:Oracle:ConnectTimeout", "00:00:02")
                .UseSetting("Simple:Mes:Oracle:QueryTimeout", "00:00:02")
                .UseSetting("Simple:Mes:RetryDelay", "00:00:00.05")
                .UseSetting("Simple:Mes:RetryMaxDelay", "00:00:00.2");
    }
}
