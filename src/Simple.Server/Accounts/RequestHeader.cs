using Grpc.AspNetCore.Server;

namespace Simple.Server.Accounts;

/// <summary>
/// Against cross-site request forgery (ADR 0045): besides the cookie's
/// <c>SameSite=Strict</c>, every request that changes something must carry
/// <see cref="Name"/>. A page on another site cannot send a custom header
/// without a CORS preflight, and the server allows no cross-origin calls.
/// </summary>
/// <remarks>
/// Hand-written because ASP.NET Core's antiforgery validates form posts only;
/// a JSON API has to guard this itself (ASP.NET Core 10 documentation,
/// "Prevent Cross-Site Request Forgery", minimal APIs).
/// </remarks>
public static class RequestHeader
{
    public const string Name = "X-Simple-Request";

    public static Task Require(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var method = context.Request.Method;
        var changes = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
        // The terminal protocol is not a browser's: it has its own listener and TLS (ADR 0025).
        var terminal = context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>() is not null;
        if (changes && !terminal && !context.Request.Headers.ContainsKey(Name))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"A request that changes something must carry the header {Name}.",
                detail: $"Send {Name}: 1 with every POST, PUT, PATCH and DELETE.").ExecuteAsync(context);
        }

        return next(context);
    }
}
