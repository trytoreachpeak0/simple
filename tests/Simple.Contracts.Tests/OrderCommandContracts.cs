using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>Cancel, hold, continue and priority: what each command does to the order.</summary>
public sealed class OrderCommandContracts : OrderContractTest
{
    [Fact]
    [Contract("BC-ORDER-003")]
    public async Task CancellingAnExecutingOrderCancelsItAndFreesTheVehicle()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("cancel-executing");

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
        await UntilAsync(_ => CardAsync(), c => c.ProcState == "IDLE", "车回到 IDLE");
    }

    [Fact]
    [Contract("BC-ORDER-013")]
    public async Task CancellingAQueuedOrderCancelsIt()
    {
        Target.RequireWrites();
        var upperId = NewUpperId("cancel-queued");
        var created = await CreateMoveAsync(upperId, Facts.ReachableStation, vehicleKey: "BROKERX-00000000000000000000000000000000");

        await Riot.Tasks.CancelOrderAsync(created.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>HELD is 7; CONTINUE_FROM_HELD takes it back to 3 and on to SUCCESS.</summary>
    [Fact]
    [Contract("BC-ORDER-006")]
    public async Task AHeldOrderContinuesFromHeldToSuccess()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("hold");

        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        var held = await CardAsync();
        await Riot.Tasks.OrderContinueAsync(order.OrderId, cancellationToken: Cancel);

        Assert.Equal("USER_FORCE_IDLE", held.ProcState);
        await UntilStateAsync(upperId, OrderStates.Success);
    }

    [Fact]
    [Contract("BC-ORDER-006")]
    public async Task AHeldOrderCanBeCancelled()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("hold-cancel");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>The two continue commands are not interchangeable: the wrong one is refused and the order is unchanged.</summary>
    [Fact]
    [Contract("BC-ORDER-006", "BC-ORDER-010")]
    public async Task TheWrongContinueCommandIsRefused()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("wrong-continue");

        var fromHeldWhileExecuting = await Assert.ThrowsAsync<RiotApiException>(() => Riot.Tasks.OrderContinueAsync(order.OrderId, cancellationToken: Cancel));
        var fromHangWhileExecuting = await Assert.ThrowsAsync<RiotApiException>(() => Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel));
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        var fromHangWhileHeld = await Assert.ThrowsAsync<RiotApiException>(() => Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel));

        Assert.Equal("100020", fromHeldWhileExecuting.BusinessCode);
        Assert.Equal("100021", fromHangWhileExecuting.BusinessCode);
        Assert.Equal("100021", fromHangWhileHeld.BusinessCode);
        Assert.Equal(OrderStates.Held, (await ReadAsync(upperId)).OrderState);
    }

    /// <summary>While an order of the vehicle is held, a new order for it stays queued.</summary>
    [Fact]
    [Contract("BC-ORDER-006")]
    public async Task ANewOrderStaysQueuedWhileTheVehiclesOrderIsHeld()
    {
        Target.RequireWrites();
        var (upperId, order) = await ExecutingAsync("hold-occupies");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        var next = NewUpperId("behind-held");

        await CreateMoveAsync(next, Facts.SecondReachableStation);

        await ThroughoutAsync(_ => ReadAsync(next), o => o.OrderState == OrderStates.Queueing, "排队");
    }

    /// <summary>
    /// A queued order put first with priority execution starts before the
    /// vehicle's earlier queued orders. The order of execution is read from
    /// where it ends, not from a moment in between: once both have succeeded,
    /// the vehicle stands at the destination of the order that ran last, the
    /// earlier one. Reading "the earlier one is still queued" while the
    /// promoted one executes failed on a busy CI machine (PR #74, run
    /// 39050505846), as the two may both be done by then.
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-014")]
    public async Task PriorityExecutionPutsAQueuedOrderFirst()
    {
        Target.RequireWrites();
        var (current, order) = await ExecutingAsync("priority-current");
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(current, OrderStates.Held);
        var first = NewUpperId("priority-first");
        var second = NewUpperId("priority-second");
        await CreateMoveAsync(first, Facts.SecondReachableStation);
        // A busy machine between the two creates: the earlier order may start
        // as soon as the vehicle is free, before the promoted one is.
        await Task.Delay(200, Cancel);
        var promoted = await CreateMoveAsync(second, Facts.VehicleStation);

        await Riot.Order.PriorityExecAsync(promoted.OrderId, Cancel);
        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(second, OrderStates.Success);
        await UntilStateAsync(first, OrderStates.Success);
        Assert.Equal(Facts.SecondReachableStation, (await CardAsync()).CurrentPosition);
    }

    /// <summary>
    /// Cancelling a hung order cancels it. The product owner's statement
    /// (ADR 0010); the lab has no record of it (research/riot-review.md U3).
    /// </summary>
    [Fact]
    [Contract("ADR 0010", "#11")]
    [NotVerifiedOnRealRiot("对挂起的单发取消，单子变成已取消（产品负责人，ADR 0010）")]
    public async Task CancellingAHungOrderCancelsIt()
    {
        var control = Target.RequireControl("在车载屏上取消或松抱闸，让订单挂起");
        var (upperId, order) = await ExecutingAsync("hang-cancel");
        await control.HangAsync(order.OrderId);
        await UntilStateAsync(upperId, OrderStates.Hang);

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>A hung order continues from HANG back to EXECUTING (when nothing on the vehicle stops it).</summary>
    [Fact]
    [Contract("BC-ORDER-015")]
    public async Task AHungOrderContinuesFromHang()
    {
        var control = Target.RequireControl("在车载屏上取消或松抱闸，让订单挂起");
        var (upperId, order) = await ExecutingAsync("hang-continue");
        await control.HangAsync(order.OrderId);
        await UntilStateAsync(upperId, OrderStates.Hang);
        var hung = await CardAsync();

        await Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel);

        Assert.Equal("INNER_FORCE_IDLE", hung.ProcState);
        await UntilStateAsync(upperId, OrderStates.Success);
    }
}
