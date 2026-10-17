using System.Net;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Simple.Server.Listeners;

/// <summary>
/// The addresses the server process listens on (ADR 0025, 0046), and what
/// each answers. A request is kept to its own listener by the connection it
/// arrives on, not by host name or port, which a caller chooses:
/// <list type="bullet">
/// <item>the API on loopback and the console's HTTPS address: everything but the terminal protocol;</item>
/// <item>the onboard terminal endpoint: the terminal protocol only;</item>
/// <item>the vehicle overview's plain address: the overview only (<see cref="OverviewMetadata"/>).</item>
/// </list>
/// </summary>
public static class Listener
{
    /// <summary>The connection item that says which listener a connection came in on.</summary>
    public const string ConnectionItem = "Simple.Listener";

    /// <summary>Marks every connection a listener accepts with what it is for.</summary>
    public static void Mark(ListenOptions listen, ListenerKind kind)
    {
        ArgumentNullException.ThrowIfNull(listen);
        listen.Use(next => connection =>
        {
            connection.Items[ConnectionItem] = kind;
            return next(connection);
        });
    }

    /// <summary>Middleware, after routing: answers 404 to what a listener does not serve.</summary>
    public static Task KeepToOwnListener(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var kind = context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ConnectionItem, out var value) == true
            ? (ListenerKind)value!
            : ListenerKind.Api;
        var endpoint = context.GetEndpoint();
        var isTerminalProtocol = endpoint?.Metadata.GetMetadata<GrpcMethodMetadata>() is not null;
        var served = kind switch
        {
            ListenerKind.Terminal => isTerminalProtocol,
            ListenerKind.Overview => endpoint?.Metadata.GetMetadata<OverviewMetadata>() is not null,
            _ => !isTerminalProtocol,
        };
        if (!served)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }

    /// <summary>Listens on one address: an IP address, or localhost (both loopbacks).</summary>
    public static void Listen(KestrelServerOptions kestrel, BindingAddress address, Action<ListenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(address);
        var host = address.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(address.Port, configure);
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(host), address.Port, configure);
        }
    }

    /// <summary>The address as configured, with the port the listener was given when 0 was configured.</summary>
    public static string Describe(string url, ListenOptions? listener)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (listener?.IPEndPoint is { Port: > 0 } bound)
        {
            var address = BindingAddress.Parse(url);
            return $"{address.Scheme}://{address.Host}:{bound.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    /// <summary>Marks an endpoint as part of the vehicle overview, served on its plain address too (ADR 0016, 0046).</summary>
    public static TBuilder ServedOnOverview<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(OverviewMetadata.Instance);
    }
}

public enum ListenerKind
{
    /// <summary>The API on loopback (urls).</summary>
    Api,

    /// <summary>The onboard terminal endpoint (ADR 0025).</summary>
    Terminal,

    /// <summary>The console's HTTPS address (ADR 0046).</summary>
    Console,

    /// <summary>The vehicle overview's plain HTTP address (ADR 0046).</summary>
    Overview,
}

/// <summary>An endpoint of the vehicle overview: the page and its data, without login.</summary>
public sealed class OverviewMetadata
{
    public static readonly OverviewMetadata Instance = new();

    private OverviewMetadata()
    {
    }
}
