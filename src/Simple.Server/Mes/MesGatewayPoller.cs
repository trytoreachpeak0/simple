using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Simple.Server.Mes;

/// <summary>
/// Fetches the MES gateway's whole snapshot every <see cref="MesGatewaySettings.PollInterval"/>
/// (the server pulls; the gateway never needs to know where the server is).
/// While the gateway does not answer, it tries again with growing, jittered
/// waits up to <see cref="MesGatewaySettings.RetryMaxDelay"/> and never gives
/// up (ADR 0008).
/// </summary>
public sealed class MesGatewayPoller(
    IHttpClientFactory clients,
    MesGatewayMonitor monitor,
    IOptions<MesGatewaySettings> settings,
    TimeProvider time) : BackgroundService
{
    public const string HttpClientName = "mes-gateway";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<GatewayUnreachableException>(),
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = settings.Value.PollInterval,
                MaxDelay = settings.Value.RetryMaxDelay,
            })
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            await pipeline.ExecuteAsync(PollAsync, stoppingToken);
            await Task.Delay(settings.Value.PollInterval, time, stoppingToken);
        }
    }

    private async ValueTask PollAsync(CancellationToken cancellationToken)
    {
        MesSnapshot? snapshot;
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            snapshot = await client.GetFromJsonAsync<MesSnapshot>(new Uri(settings.Value.Url!, "/api/v1/snapshot"), cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the fetch throws, the gateway did not answer with a
            // snapshot: a refused connection, a timeout, a body cut off
            // midway (HttpIOException, an IOException), bad JSON. Letting one
            // through would end the poller and, by default, the whole host.
            await monitor.GatewayUnreachableAsync(e is TaskCanceledException ? $"No answer within {settings.Value.RequestTimeout}." : e.Message, cancellationToken);
            throw new GatewayUnreachableException();
        }

        if (snapshot is null)
        {
            await monitor.GatewayUnreachableAsync("The gateway answered null instead of a snapshot.", cancellationToken);
            throw new GatewayUnreachableException();
        }

        await monitor.SnapshotReceivedAsync(snapshot, cancellationToken);
    }

    /// <summary>Tells the retry that the gateway did not answer; the monitor has already been told why.</summary>
    private sealed class GatewayUnreachableException : Exception;
}
