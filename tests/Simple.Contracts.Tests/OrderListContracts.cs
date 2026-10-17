namespace Simple.Contracts.Tests;

/// <summary>Paging through the order list while the rest of the plant makes orders.</summary>
public sealed class OrderListContracts : OrderContractTest
{
    /// <summary>
    /// Real RIoT is shared by the whole plant; its order list holds hundreds of
    /// other lines' orders, and the total can change between one page and the
    /// next. A caller that needs every order checks the total and the page
    /// sizes and reads again when they disagree.
    /// </summary>
    [Fact]
    [Contract("cs#525", "cs#573")]
    [NotVerifiedOnRealRiot("翻页途中订单总数会变：上一代 cs#573 由服务端分页读不全推断（全厂订单变动时），没有留下原始分页读数")]
    public async Task TheTotalCanChangeBetweenOnePageAndTheNext()
    {
        var control = Target.RequireControl("让别的产线在翻页期间建单");
        foreach (var tag in new[] { "page-a", "page-b", "page-c" })
        {
            await CreateMoveAsync(NewUpperId(tag), Facts.ReachableStation, vehicleKey: "BROKERX-00000000000000000000000000000000");
        }

        await control.SetOrderListChurnAsync(ordersPerRead: 1);
        var first = await Riot.Order.ListOrdersByStatesAsync([OrderStates.Queueing], 1, 2, Cancel);
        var second = await Riot.Order.ListOrdersByStatesAsync([OrderStates.Queueing], 2, 2, Cancel);

        Assert.NotEqual(first.Total, second.Total);
        Assert.False(first.CoversAllRecords);
    }
}
