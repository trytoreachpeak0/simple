using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// The fleet manager a contract test runs against, through riot-sdk's
/// facade: the fake (default, and always on CI) or a real RIoT a person
/// selected for this run (<see cref="RealRiotSettings"/>).
/// </summary>
public abstract class RiotTarget : IAsyncDisposable
{
    /// <summary>The environment variable that names a settings file for a real RIoT.</summary>
    public const string RealSettingsVariable = "SIMPLE_CONTRACTS_RIOT_SETTINGS";

    public abstract string Description { get; }

    public abstract RiotTargetFacts Facts { get; }

    /// <summary>A session with the target's own call key.</summary>
    public abstract RiotSession Session { get; }

    /// <summary>The fake's control API; null on real RIoT, where a person does these things at the vehicle.</summary>
    public abstract FakeRiotControl? Control { get; }

    /// <summary>Whether contracts that create orders, send commands, stop vehicles or take them off line may run.</summary>
    public abstract bool AllowsWrites { get; }

    /// <summary>The longest a contract waits for a state the target is expected to reach.</summary>
    public abstract TimeSpan Patience { get; }

    /// <summary>How long a contract watches a state the target is expected to keep (for example, an order that stays queued).</summary>
    public abstract TimeSpan QuietWindow { get; }

    /// <summary>A session at the same endpoint with other credentials, for the authentication contracts.</summary>
    public abstract RiotSession SessionWith(RiotOptions options);

    /// <summary>
    /// A plain HTTP client at the target's endpoint, for what the facade will
    /// not send or does not show: without credentials, or with the call key.
    /// </summary>
    public HttpClient CreateRawClient(bool withCallKey = false)
    {
        var client = CreatePlainClient();
        if (withCallKey)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", CallApiKey);
        }

        return client;
    }

    protected abstract string CallApiKey { get; }

    protected abstract HttpClient CreatePlainClient();

    /// <summary>The target's endpoint, for <see cref="SessionWith"/>.</summary>
    public abstract string BaseUrl { get; }

    public static async Task<RiotTarget> CreateAsync()
    {
        var path = Environment.GetEnvironmentVariable(RealSettingsVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return await FakeRiotTarget.StartAsync();
        }

        var settings = RealRiotSettings.Load(path, Environment.GetEnvironmentVariable, DateTimeOffset.Now);
        return new RealRiotTarget(settings);
    }

    /// <summary>Skips a contract that writes, on a target that does not allow writes.</summary>
    public void RequireWrites()
    {
        if (!AllowsWrites)
        {
            Assert.Skip("写操作（建单、订单命令、急停、上下线）在真实 RIoT 上要产品负责人逐次授权、现场有人，这一套契约测试不在真实 RIoT 上做写操作。");
        }
    }

    /// <summary>Skips a contract that needs the fake's control API, on real RIoT.</summary>
    public FakeRiotControl RequireControl(string whatAPersonDoes)
    {
        RequireWrites();
        if (Control is null)
        {
            Assert.Skip($"在真实 RIoT 上这要人在车旁做：{whatAPersonDoes}。");
        }

        return Control;
    }

    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// What the contracts need to know about the target's maps and vehicle.
/// For the fake these come from its seed; for a real RIoT from the settings file.
/// </summary>
/// <param name="VehicleKey">The one vehicle the contracts use.</param>
/// <param name="MapId">The vehicle's map.</param>
/// <param name="VehicleStation">The station the vehicle stands on when the contracts start.</param>
/// <param name="ReachableStation">Another station on the map the vehicle can reach.</param>
/// <param name="SecondReachableStation">A third station on the map the vehicle can reach.</param>
/// <param name="UnreachableStation">A station on the map no route leads to; null when none is known.</param>
/// <param name="ChargingStation">A charging station the vehicle can reach; null when none is known.</param>
/// <param name="MapsSharingAStationId">Two maps that both have a station with the same id; null when not known.</param>
/// <param name="SharedStationId">That station id.</param>
/// <param name="Username">Admin login, when the person gave one.</param>
/// <param name="Password">Its password.</param>
public sealed record RiotTargetFacts(
    string VehicleKey,
    int MapId,
    int VehicleStation,
    int ReachableStation,
    int SecondReachableStation,
    int? UnreachableStation,
    int? ChargingStation,
    (int First, int Second)? MapsSharingAStationId,
    int? SharedStationId,
    string? Username,
    string? Password);
