using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Server.Riot;

/// <summary>Whether the server reaches the fleet manager, for the status API.</summary>
public sealed class RiotConnectionMonitor(IOptions<RiotSettings> settings, TimeProvider time)
{
    public const string NotConfigured = "not-configured";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Unreachable = "unreachable";

    private Observation current = new(null, null, null, null);

    public RiotStatus Status()
    {
        var o = Volatile.Read(ref current);
        var url = settings.Value.Url;
        var state = url is null ? NotConfigured
            : o.Reachable is null ? Connecting
            : o.Reachable.Value ? Connected
            : Unreachable;
        return new RiotStatus(state, url, o.CheckedAt, o.ConnectedAt, o.Error, o.MapCount);
    }

    public void Answered(int mapCount)
    {
        var now = time.GetUtcNow();
        Volatile.Write(ref current, new Observation(true, now, now, null) { MapCount = mapCount });
    }

    public void DidNotAnswer(string error)
    {
        var o = Volatile.Read(ref current);
        Volatile.Write(ref current, o with { Reachable = false, CheckedAt = time.GetUtcNow(), Error = error });
    }

    private sealed record Observation(bool? Reachable, DateTimeOffset? CheckedAt, DateTimeOffset? ConnectedAt, string? Error)
    {
        public int? MapCount { get; init; }
    }
}

/// <summary>The fleet manager column of the status API.</summary>
/// <param name="State"><c>not-configured</c>, <c>connecting</c> (not asked yet), <c>connected</c> or <c>unreachable</c>.</param>
/// <param name="Url">Where the server reaches RIoT; null when none is configured.</param>
/// <param name="CheckedAt">When the server last asked, by its own clock.</param>
/// <param name="ConnectedAt">When RIoT last answered; kept while it is unreachable.</param>
/// <param name="Error">Why the last call failed; null when it succeeded.</param>
/// <param name="MapCount">Maps in RIoT's last answer.</param>
public sealed record RiotStatus(string State, Uri? Url, DateTimeOffset? CheckedAt, DateTimeOffset? ConnectedAt, string? Error, int? MapCount);

/// <summary>
/// Reads RIoT's map list every <see cref="RiotSettings.PollInterval"/> to
/// know whether the server reaches it. Read only, so while RIoT does not
/// answer it tries again with growing, jittered waits up to
/// <see cref="RiotSettings.RetryMaxDelay"/> and never gives up (ADR 0008).
/// </summary>
public sealed class RiotConnectionProbe(
    IHttpMessageHandlerFactory handlers,
    RiotConnectionMonitor monitor,
    IOptions<RiotSettings> settings,
    TimeProvider time) : BackgroundService
{
    public const string HttpClientName = "riot";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Configured)
        {
            return;
        }

        var pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RiotUnreachableException>(),
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = settings.Value.PollInterval,
                MaxDelay = settings.Value.RetryMaxDelay,
            })
            .Build();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipeline.ExecuteAsync(ProbeAsync, stoppingToken);
                await Task.Delay(settings.Value.PollInterval, time, stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. A stop that lands while a call is failing comes back
            // from the retry as that failure, not as a cancellation (simple#55);
            // either way the probe just ends.
        }
    }

    private async ValueTask ProbeAsync(CancellationToken cancellationToken)
    {
        var options = new RiotOptions
        {
            BaseUrl = settings.Value.Url!.ToString(),
            CallApiKey = settings.Value.CallApiKey,
            Timeout = settings.Value.RequestTimeout,
        };
        try
        {
            // The factory's handler has automatic redirects off (ADR 0028),
            // like the client riot-sdk builds for itself.
            await using var session = new RiotSession(options, handlers.CreateHandler(HttpClientName));
            var maps = await session.Maps.ListMapsAsync(cancellationToken);
            monitor.Answered(maps.Count);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the read throws, RIoT did not answer with a map list:
            // a refused connection, a timeout, a body cut off midway
            // (HttpIOException, an IOException, simple#37), HTTP 401, a
            // business failure. Letting one through would end the probe and,
            // by default, the whole host.
            monitor.DidNotAnswer(e is TaskCanceledException ? $"No answer within {settings.Value.RequestTimeout}." : e.Message);
            throw new RiotUnreachableException();
        }
    }

    /// <summary>Tells the retry that RIoT did not answer; the monitor has already been told why.</summary>
    private sealed class RiotUnreachableException : Exception;
}

public static class RiotServiceCollectionExtensions
{
    /// <summary>The fleet manager connection: settings from <c>Simple:Riot</c>, its monitor, its probe and the map reader.</summary>
    public static IServiceCollection AddRiotConnection(this IServiceCollection services)
    {
        services.AddOptions<RiotSettings>()
            .BindConfiguration(RiotSettings.Section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RiotSettings>, RiotSettingsValidator>();
        services.AddHttpClient(RiotConnectionProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<RiotConnectionMonitor>();
        services.AddHostedService<RiotConnectionProbe>();
        services.AddSingleton<RiotMapReader>();
        return services;
    }
}
