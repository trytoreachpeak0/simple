using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Simple.Server.Listeners;

namespace Simple.Server.Tests;

/// <summary>
/// What each listener answers (ADR 0025, 0046), for endpoints the server does
/// not have yet: the vehicle overview's. The real listeners are tested over
/// sockets in ConsoleListenerTests and OnboardTerminalTests.
/// </summary>
public sealed class ListenerTests
{
    [Theory]
    [InlineData(ListenerKind.Overview, true, true)]
    [InlineData(ListenerKind.Overview, false, false)]
    [InlineData(ListenerKind.Console, true, true)]
    [InlineData(ListenerKind.Console, false, true)]
    [InlineData(ListenerKind.Api, true, true)]
    [InlineData(ListenerKind.Terminal, true, false)]
    public async Task TheOverviewAddressServesOverviewEndpointsAndNothingElse(ListenerKind listener, bool overviewEndpoint, bool served)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IConnectionItemsFeature>(new ConnectionItems(listener));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            overviewEndpoint ? new EndpointMetadataCollection(OverviewMetadata.Instance) : EndpointMetadataCollection.Empty,
            "test endpoint"));
        var reached = false;

        await Listener.KeepToOwnListener(context, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        Assert.Equal(served, reached);
        Assert.Equal(served ? StatusCodes.Status200OK : StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    private sealed class ConnectionItems(ListenerKind kind) : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?> { [Listener.ConnectionItem] = kind };
    }
}
