using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>What creating an order answers, and when it is refused.</summary>
public sealed class OrderCreationContracts : OrderContractTest
{
    /// <summary>
    /// Three identifiers: the string orderId (<c>order-…</c>) for details and
    /// commands, the numeric id, and our upperId echoed back. A new order is
    /// queued, with no executing vehicle yet (<c>"--"</c>).
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-001", "BC-ORDER-005", "BC-ORDER-013")]
    public async Task ACreatedOrderIsQueuedWithItsThreeIdentifiers()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("create");

        var created = await CreateMoveAsync(upperId, Facts.ReachableStation);
        var byOrderId = await Riot.Order.GetOrderByOrderIdAsync(created.OrderId, Cancel);
        var lookup = await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel);

        Assert.Matches("^order-[0-9]+$", created.OrderId);
        Assert.True(created.Id > 0);
        Assert.Equal(upperId, created.UpperId);
        Assert.Equal(OrderStates.Queueing, created.OrderState);
        Assert.Equal(created, byOrderId with { OrderState = created.OrderState });
        Assert.Equal(OrderLookupStatus.Found, lookup.Status);
        Assert.Equal(Facts.VehicleKey, lookup.Order!.AppointVehicleKey);
        Assert.Equal(Facts.ReachableStation, lookup.Order.EndStationNo);
    }

    [Fact]
    [Contract("BC-ORDER-013")]
    public async Task AQueuedOrderShowsNoExecutingVehicle()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("queued");

        await CreateMoveAsync(upperId, Facts.ReachableStation, vehicleKey: "BROKERX-00000000000000000000000000000000");
        var order = await ReadAsync(upperId);

        Assert.Equal(OrderStates.Queueing, order.OrderState);
        Assert.Equal("--", order.ExecuteVehicleKey);
    }

    /// <summary>A repeated upperId is refused with 0610008; no second order is made and the first is not returned.</summary>
    [Fact]
    [Contract("BC-ORDER-004")]
    public async Task ARepeatedUpperIdIsRefusedAndMakesNoSecondOrder()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("repeat");
        await CreateMoveAsync(upperId, Facts.ReachableStation);

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(upperId, Facts.SecondReachableStation));
        var page = await Riot.Order.ListOrdersByStatesAsync([1, 2, 3, 4, 5, 7, 9], 1, 1000, Cancel);

        Assert.Equal("0610008", refused.BusinessCode);
        Assert.Single(page.Records, r => r.UpperId == upperId);
    }

    [Fact]
    [Contract("BC-ORDER-007")]
    public async Task AStationThatIsNotOnTheMapIsRefusedWith0660003()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("no-station");

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(upperId, 99999));

        Assert.Equal("0660003", refused.BusinessCode);
        Assert.Equal(OrderLookupStatus.AbsentAtObservation, (await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel)).Status);
    }

    /// <summary>
    /// 69 and 71 characters were refused with HTTP 200 and 00002, and the
    /// order was never made; 52 is the longest known to be accepted. The
    /// exact limit (52 to 68) is unknown, so nothing may rely on more than 52.
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-022", "cs#616")]
    public async Task AnUpperIdOf69CharactersIsRefusedWith00002AndNoOrderIsMade()
    {
        Target.RequireWrites();
        var tooLong = NewUpperId("long", 69).PadRight(69, '0');

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(tooLong, Facts.ReachableStation));

        Assert.Equal(69, tooLong.Length);
        Assert.Equal("00002", refused.BusinessCode);
        Assert.Equal(OrderLookupStatus.AbsentAtObservation, (await Riot.Order.FindOrderByUpperIdAsync(tooLong, Cancel)).Status);
    }

    [Fact]
    [Contract("BC-ORDER-022")]
    public async Task AnUpperIdOf52CharactersIsAccepted()
    {
        Target.RequireWrites();
        var longest = NewUpperId("long", 52).PadRight(52, '0');

        var created = await CreateMoveAsync(longest, Facts.ReachableStation);

        Assert.Equal(52, longest.Length);
        Assert.Equal(longest, created.UpperId);
    }

    /// <summary>An appointed vehicle RIoT does not know does not stop the order: it is made and stays queued.</summary>
    [Fact]
    [Contract("BC-ORDER-007")]
    public async Task AnOrderForAnUnknownVehicleIsMadeAndStaysQueued()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("ghost");

        await CreateMoveAsync(upperId, Facts.ReachableStation, vehicleKey: "BROKERX-00000000000000000000000000000000");

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队");
    }

    /// <summary>An unreachable destination does not fail the order either: it stays queued and never turns FAILED.</summary>
    [Fact]
    [Contract("BC-ORDER-012")]
    public async Task AnOrderToAnUnreachableStationIsMadeAndStaysQueued()
    {
        Target.RequireWrites();
        if (Facts.UnreachableStation is not { } unreachable)
        {
            Assert.Skip("没有已知的不可达站点。");
            return;
        }

        var upperId = NewUpperId("unreachable");
        await CreateMoveAsync(upperId, unreachable);

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队、不变失败");
    }
}
