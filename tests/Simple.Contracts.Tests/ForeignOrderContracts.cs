using System.Text.Json;

namespace Simple.Contracts.Tests;

/// <summary>
/// Orders SIMPLE did not make: dispatched by hand on RIoT's web page. The
/// server reads them back by orderId to see whether they have ended (ADR 0011).
/// </summary>
public sealed class ForeignOrderContracts : OrderContractTest
{
    /// <summary>
    /// A cancelled order that was dispatched by hand on the web page reads by
    /// orderId with an empty upperId: the empty string, not null and not a
    /// missing member. The previous line's SDK refused such an answer and never
    /// saw the order end, so the vehicle was held for good (cs#617). The one
    /// sample on real RIoT is exactly this: a cancelled web-page order (Q-043).
    /// </summary>
    [Fact]
    [Contract("Q-043", "cs#617")]
    public async Task ACancelledWebPageOrderReadsByOrderIdWithAnEmptyUpperId()
    {
        var control = Target.RequireControl("在 RIoT 网页上给车手动派一张单");
        var orderId = await control.CreateWebOrderAsync(Facts.VehicleKey, Facts.MapId, Facts.ReachableStation);
        await Riot.Tasks.CancelOrderAsync(orderId, cancellationToken: Cancel);

        var order = await UntilAsync(c => Riot.Order.GetOrderByOrderIdAsync(orderId, c), o => o.OrderState == OrderStates.Cancelled, $"网页手派单 {orderId} 已取消");
        var upperId = await RawUpperIdAsync(orderId);

        Assert.Equal("", order.UpperId);
        Assert.Equal((JsonValueKind.String, ""), upperId);
    }

    /// <summary>
    /// While it runs, too, it reads with an empty upperId, and its end is read
    /// by orderId. Whether an order that has not ended answers the same is
    /// Q-043's first open question; the fake answers the same.
    /// </summary>
    [Fact]
    [Contract("Q-043", "cs#617")]
    [NotVerifiedOnRealRiot("没结束的网页手派单按 orderId 读，upperId 也是空串（Q-043 第 1 问；唯一的样本是已取消的单）")]
    public async Task AWebPageOrderReadsWithAnEmptyUpperIdWhileItRunsAndItsEndIsReadByOrderId()
    {
        var control = Target.RequireControl("在 RIoT 网页上给车手动派一张单");

        var orderId = await control.CreateWebOrderAsync(Facts.VehicleKey, Facts.MapId, Facts.ReachableStation);
        var running = await UntilAsync(c => Riot.Order.GetOrderByOrderIdAsync(orderId, c), o => o.OrderState == OrderStates.Executing, $"网页手派单 {orderId} 执行");

        Assert.Equal("", running.UpperId);
        Assert.Equal((JsonValueKind.String, ""), await RawUpperIdAsync(orderId));
        await UntilAsync(c => Riot.Order.GetOrderByOrderIdAsync(orderId, c), o => o.OrderState == OrderStates.Success, $"网页手派单 {orderId} 成功");
    }

    private async Task<(JsonValueKind Kind, string? Value)> RawUpperIdAsync(string orderId)
    {
        using var http = Target.CreateRawClient(withCallKey: true);
        using var raw = JsonDocument.Parse(await http.GetStringAsync(new Uri($"api/order/v1/orderRecord/detailByOrderId/{orderId}", UriKind.Relative), Cancel));
        var upperId = raw.RootElement.GetProperty("result").GetProperty("upperId");
        return (upperId.ValueKind, upperId.ValueKind == JsonValueKind.String ? upperId.GetString() : null);
    }
}
