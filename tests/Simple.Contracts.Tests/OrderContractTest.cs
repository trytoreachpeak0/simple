using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>
/// Contracts that create orders and send commands. They move a vehicle on a
/// real RIoT, so there they skip (<see cref="Targets.RiotTarget.RequireWrites"/>).
/// </summary>
public abstract class OrderContractTest : ContractTest
{
    /// <summary>A fresh upperId, at most <paramref name="length"/> characters.</summary>
    protected static string NewUpperId(string tag, int length = 40)
    {
        var id = $"sc-{tag}-{Guid.NewGuid():N}";
        return id.Length > length ? id[..length] : id;
    }

    protected Task<OrderRef> CreateMoveAsync(string upperId, int station, string? vehicleKey = null) =>
        Riot.Order.CreateMoveOrderAsync(upperId, vehicleKey ?? Facts.VehicleKey, Facts.MapId, station, cancellationToken: Cancel);

    protected async Task<OrderSnapshot> ReadAsync(string upperId)
    {
        var lookup = await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel);
        Assert.Equal(OrderLookupStatus.Found, lookup.Status);
        return lookup.Order!;
    }

    protected Task<OrderSnapshot> UntilStateAsync(string upperId, int state) =>
        UntilAsync(_ => ReadAsync(upperId), o => o.OrderState == state, $"订单 {upperId} 变成 {state}");

    protected Task<VehicleCard> CardAsync() => Riot.Tasks.GetVehicleCardAsync(Facts.VehicleKey, Cancel);

    protected Task<VehicleExecutionFacts> VehicleAsync() => Riot.Tasks.GetVehicleExecutionFactsAsync(Facts.VehicleKey, Cancel);

    /// <summary>Creates a move order to the reachable station and waits until it executes.</summary>
    protected async Task<(string UpperId, OrderSnapshot Order)> ExecutingAsync(string tag)
    {
        var upperId = NewUpperId(tag);
        await CreateMoveAsync(upperId, Facts.ReachableStation);
        return (upperId, await UntilStateAsync(upperId, OrderStates.Executing));
    }
}

/// <summary>RIoT's order states (research/riot-review.md R12).</summary>
public static class OrderStates
{
    public const int Queueing = 1;
    public const int Cancelled = 2;
    public const int Executing = 3;
    public const int Failed = 4;
    public const int Success = 5;
    public const int Held = 7;
    public const int Hang = 9;
}
