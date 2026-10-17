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

    /// <summary>The same shape for an orderId: 200 with no result, which the facade turns into order-ref-missing.</summary>
    [Fact]
    [Contract("R11", "Q-043")]
    public async Task AnUnknownOrderIdAnswersSuccessWithoutAResult()
    {
        var refused = await Assert.ThrowsAsync<RiotApiException>(
            () => Riot.Order.GetOrderByOrderIdAsync("order-1000000000000000000", Cancel));

        Assert.Equal("order-ref-missing", refused.BusinessCode);
    }
}
