using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>
/// <c>00002</c> is RIoT's general internal error. On creating an order it
/// has been seen with no order made (BC-ORDER-022); elsewhere it has been seen
/// with a side effect (BC-VEH-003). So a caller treats it as "result
/// unknown" and looks the order up (research/riot-review.md R6). The fake
/// answers either way when told to, which a person cannot bring about on
/// real RIoT.
/// </summary>
public sealed class InternalErrorContracts : OrderContractTest
{
    [Fact]
    [Contract("BC-ORDER-022", "R6")]
    public async Task A00002ThatMadeNoOrderLeavesNoOrderToFind()
    {
        var control = Target.RequireControl("让 RIoT 建单时回 00002");
        await control.InjectCreate00002Async(orderMade: false);
        var upperId = NewUpperId("00002-none");

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(upperId, Facts.ReachableStation));

        Assert.Equal("00002", refused.BusinessCode);
        Assert.Equal(OrderLookupStatus.AbsentAtObservation, (await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel)).Status);
    }

    [Fact]
    [Contract("R6", "BC-VEH-003", "BC-ORDER-022")]
    [NotVerifiedOnRealRiot("建单回 00002 而单其实建成了：建单上没有观测过；00002 是通用内部错误，上下线接口上见过带副作用（BC-VEH-003），所以按结果未知对待（research/riot-review.md R6，BC-ORDER-022 消费影响第 2 条）")]
    public async Task A00002CanComeBackForAnOrderThatWasMade()
    {
        var control = Target.RequireControl("让 RIoT 建单时回 00002");
        await control.InjectCreate00002Async(orderMade: true);
        var upperId = NewUpperId("00002-made");

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(upperId, Facts.ReachableStation));
        var lookup = await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel);

        Assert.Equal("00002", refused.BusinessCode);
        Assert.Equal(OrderLookupStatus.Found, lookup.Status);
        await UntilStateAsync(upperId, OrderStates.Success);
    }

    /// <summary>The injection answers one create; the next one is answered as usual.</summary>
    [Fact]
    [Contract("R6")]
    public async Task OnlyTheNextCreateAnswers00002()
    {
        var control = Target.RequireControl("让 RIoT 建单时回 00002");
        await control.InjectCreate00002Async(orderMade: false);
        await Assert.ThrowsAsync<RiotApiException>(() => CreateMoveAsync(NewUpperId("00002-first"), Facts.ReachableStation));
        var upperId = NewUpperId("00002-next");

        var created = await CreateMoveAsync(upperId, Facts.ReachableStation);

        Assert.Equal(upperId, created.UpperId);
    }
}
