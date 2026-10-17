namespace Simple.Contracts.Tests;

/// <summary>An order's way from queued to done, and what the vehicle shows on the way.</summary>
public sealed class OrderLifecycleContracts : OrderContractTest
{
    /// <summary>
    /// QUEUEING → EXECUTING → SUCCESS on the appointed vehicle, which is then
    /// the executing vehicle; the vehicle reads station 0 while it moves and
    /// stands on the destination before the order turns SUCCESS.
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-002", "BC-STATE-002", "BC-STATE-003")]
    public async Task AMoveOrderRunsToSuccessAndTheVehicleArrivesBeforeTheOrderSucceeds()
    {
        Target.RequireWrites();
        var (upperId, executing) = await ExecutingAsync("lifecycle");
        Assert.Equal(Facts.VehicleKey, executing.ExecuteVehicleKey);

        var moving = await UntilAsync(_ => CardAsync(), c => c.ProcState == "PROCESSING_ORDER" && c.CurrentPosition == 0, "车离站、站号读成 0");
        var arrived = await UntilAsync(
            async _ => (Card: await CardAsync(), Order: await ReadAsync(upperId)),
            r => r.Card.CurrentPosition == Facts.ReachableStation && r.Card.ProcState != "IDLE",
            "车到站、订单还没结束");
        var done = await UntilStateAsync(upperId, OrderStates.Success);
        var idle = await UntilAsync(_ => CardAsync(), c => c.ProcState == "IDLE", "车回到 IDLE");

        Assert.Equal(Facts.MapId, (await Riot.Maps.ListMapsAsync(Cancel)).Single(m => m.Name == moving.CurrentMap).MapId);
        Assert.Equal(OrderStates.Executing, arrived.Order.OrderState);
        Assert.Equal(Facts.VehicleKey, done.ExecuteVehicleKey);
        Assert.Equal(Facts.ReachableStation, idle.CurrentPosition);
    }

    /// <summary>Every executing order shows CONTROL_STATE_ERR, with no emergency stop: it is not a fault signal.</summary>
    [Fact]
    [Contract("BC-VEH-005")]
    public async Task AnExecutingOrderShowsControlStateErrWithoutAnyEmergencyStop()
    {
        Target.RequireWrites();
        var (upperId, _) = await ExecutingAsync("control-err");
        var whileExecuting = new List<RIoT.Sdk.Core.VehicleExecutionFacts>();

        // The vehicle first, then the order: an order still executing after the
        // vehicle was read was executing when it was read (3 only goes on to 5).
        await UntilAsync(
            async _ =>
            {
                var facts = await VehicleAsync();
                var state = (await ReadAsync(upperId)).OrderState;
                if (state == OrderStates.Executing)
                {
                    whileExecuting.Add(facts);
                }

                return state;
            },
            state => state == OrderStates.Success,
            $"订单 {upperId} 成功");

        Assert.NotEmpty(whileExecuting);
        Assert.All(whileExecuting, facts => Assert.Equal(("CONTROL_STATE_ERR", "OK"), (facts.ControlState, facts.EmergencyState)));
    }
}
