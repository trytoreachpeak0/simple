using System.Net;
using System.Text.Json;
using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>Looking up orders RIoT does not know. Read only.</summary>
public sealed class OrderLookupContracts : ContractTest
{
    /// <summary>
    /// Real RIoT answers HTTP 200, <c>code=0</c> and no <c>result</c>, never
    /// 404. The previous line's fake answered 404, so "RIoT says there is no
    /// such order" went down a branch that never runs in the field.
    /// </summary>
    [Fact]
    [Contract("R10", "cs#404", "cs#432")]
    public async Task AnUnknownUpperIdIsAbsentAtObservationNotNotFound()
    {
        var upperId = $"simple-contract-absent-{Guid.NewGuid():N}"[..48];

        var lookup = await Riot.Order.FindOrderByUpperIdAsync(upperId, Cancel);

        Assert.Equal(OrderLookupStatus.AbsentAtObservation, lookup.Status);
        Assert.Null(lookup.Order);
    }

    /// <summary>
    /// The same shape for an orderId: 200 with no result, which the facade
    /// turns into order-ref-missing. No field record has it: Q-043 and cs#617
    /// read a known order dispatched by hand on RIoT's web page; "an unknown
    /// orderId answers 200 with no result" is only riot-sdk's comment
    /// (OrderClient.cs, GetOrderByOrderIdAsync). Read only, so a run against
    /// real RIoT checks it.
    /// </summary>
    [Fact]
    [Contract("riot-sdk OrderClient.GetOrderByOrderIdAsync")]
    [NotVerifiedOnRealRiot("按 orderId 查不认识的单，回 HTTP 200、code 0、不带 result（只有 riot-sdk 的注释这样写，没有现场记录）")]
    public async Task AnUnknownOrderIdAnswersSuccessWithoutAResult()
    {
        var refused = await Assert.ThrowsAsync<RiotApiException>(
            () => Riot.Order.GetOrderByOrderIdAsync("order-1000000000000000000", Cancel));

        Assert.Equal("order-ref-missing", refused.BusinessCode);
    }

    /// <summary>
    /// The facade reads "no result", "result null" and "missing ids" alike,
    /// so this one looks at the answer itself: HTTP 200, code 0, and no
    /// <c>result</c> member at all.
    /// </summary>
    [Fact]
    [Contract("R10", "cs#404", "cs#432")]
    public async Task AnUnknownUpperIdIsHttp200WithCode0AndNoResultMember() =>
        await AssertNoResultMemberAsync("detailByUpperId/simple-contract-absent-0000000000000000000000");

    [Fact]
    [Contract("riot-sdk OrderClient.GetOrderByOrderIdAsync")]
    [NotVerifiedOnRealRiot("按 orderId 查不认识的单，回 HTTP 200、code 0、不带 result（只有 riot-sdk 的注释这样写，没有现场记录）")]
    public async Task AnUnknownOrderIdIsHttp200WithCode0AndNoResultMember() =>
        await AssertNoResultMemberAsync("detailByOrderId/order-1000000000000000000");

    private async Task AssertNoResultMemberAsync(string path)
    {
        using var http = Target.CreateRawClient(withCallKey: true);

        using var response = await http.GetAsync(new Uri($"api/order/v1/orderRecord/{path}", UriKind.Relative), Cancel);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", body.RootElement.GetProperty("code").ToString());
        Assert.False(body.RootElement.TryGetProperty("result", out _), $"answer carries a result member: {body.RootElement}");
    }
}
