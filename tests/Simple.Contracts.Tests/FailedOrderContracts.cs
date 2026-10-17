namespace Simple.Contracts.Tests;

/// <summary>
/// FAILED (4) was never seen in the RIoT behaviour lab (Q-024); in the field
/// it is rare and goes with system or map faults. The fake never fails an
/// order by itself; a test makes it fail.
/// </summary>
public sealed class FailedOrderContracts : OrderContractTest
{
    [Fact]
    [Contract("ADR 0010", "#11", "Q-024")]
    [NotVerifiedOnRealRiot("订单只会在刚创建、还没排队时变成失败，此时车在原地（产品负责人，ADR 0010；#7 评论第 5 条；实验室从没见过 FAILED，Q-024）。假 RIoT 没有'还没排队'这一态，用排队中的单代替")]
    public async Task AJustMadeOrderFailsWithTheVehicleWhereItIs()
    {
        var control = Target.RequireControl("让 RIoT 的订单失败（没人知道怎样能做到）");
        await Riot.Tasks.DispatchDisableAsync(Facts.VehicleKey, Cancel);
        var upperId = NewUpperId("failed");
        var created = await CreateMoveAsync(upperId, Facts.ReachableStation);

        await control.FailAsync(created.OrderId);
        var failed = await UntilStateAsync(upperId, OrderStates.Failed);

        Assert.Equal("--", failed.ExecuteVehicleKey);
        Assert.Equal(Facts.VehicleStation, (await CardAsync()).CurrentPosition);
    }
}
