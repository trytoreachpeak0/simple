using System.Text.Json;

namespace Simple.Contracts.Tests;

/// <summary>
/// Orders SIMPLE did not make: dispatched by hand on RIoT's web page. The
/// server reads them back by orderId to see whether they have ended (ADR 0011).
/// </summary>
public sealed class ForeignOrderContracts : OrderContractTest
{
    /// <summary>
    /// An order dispatched by hand on the web page has an empty upperId: the
    /// empty string, not null and not a missing member. The previous line's
    /// SDK refused such an answer and never saw the order end, so the vehicle
    /// was held for good (cs#617). One sample on real RIoT (Q-043).
    /// </summary>
    [Fact]
    [Contract("Q-043", "cs#617")]
    public async Task AnOrderDispatchedOnTheWebPageReadsByOrderIdWithAnEmptyUpperId()
    {
        var control = Target.RequireControl("在 RIoT 网页上给车手动派一张单");

        var orderId = await control.CreateWebOrderAsync(Facts.VehicleKey, Facts.MapId, Facts.ReachableStation);
        var order = await Riot.Order.GetOrderByOrderIdAsync(orderId, Cancel);
        using var http = Target.CreateRawClient(withCallKey: true);
        using var raw = JsonDocument.Parse(await http.GetStringAsync(new Uri($"api/order/v1/orderRecord/detailByOrderId/{orderId}", UriKind.Relative), Cancel));

        Assert.Equal("", order.UpperId);
        Assert.Equal(JsonValueKind.String, raw.RootElement.GetProperty("result").GetProperty("upperId").ValueKind);
        Assert.Equal("", raw.RootElement.GetProperty("result").GetProperty("upperId").GetString());
    }

    /// <summary>It runs on the vehicle like any other order, and its end is read by orderId.</summary>
    [Fact]
    [Contract("Q-043", "cs#617")]
    public async Task AnOrderDispatchedOnTheWebPageRunsAndItsEndIsReadByOrderId()
    {
        var control = Target.RequireControl("在 RIoT 网页上给车手动派一张单");

        var orderId = await control.CreateWebOrderAsync(Facts.VehicleKey, Facts.MapId, Facts.ReachableStation);

        await UntilAsync(c => Riot.Order.GetOrderByOrderIdAsync(orderId, c), o => o.OrderState == OrderStates.Success, $"网页手派单 {orderId} 成功");
    }
}
