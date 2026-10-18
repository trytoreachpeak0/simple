using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Simple.Server.Accounts;

namespace Simple.Server.Sites;

public static class SiteConfigurationEndpoints
{
    /// <summary>
    /// How the configuration reads as JSON: camelCase names, enumerations in
    /// kebab-case as a site file writes them (<c>nearest-first</c>,
    /// <c>fixed-station</c>), IO points as <c>DI1</c> and <c>DO1</c>,
    /// durations as <c>hh:mm:ss</c>.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower), new IoPointConverter() },
    };

    public static void MapSiteConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/site-configuration", GetCurrent)
            .RequireAccess(Access.View)
            .WithName("GetSiteConfiguration")
            .WithSummary("The whole site configuration in effect, with its version; every account may read it (ADR 0017). 404 before the first import.");
    }

    private static async Task<Results<JsonHttpResult<SiteConfigurationResponse>, ProblemHttpResult>> GetCurrent(
        SiteConfigurationStore store, CancellationToken cancellationToken)
    {
        if (await store.CurrentAsync(cancellationToken) is not { } current)
        {
            return TypedResults.Problem(
                "服务端还没有厂区配置：首次安装时用 import-site 导入 sites/<厂区>/site.yaml。",
                statusCode: StatusCodes.Status404NotFound,
                title: "No site configuration yet");
        }

        return TypedResults.Json(new SiteConfigurationResponse(current.Version, current.CreatedAt, current.Source, current.Configuration), JsonOptions);
    }

    private sealed class IoPointConverter : JsonConverter<IoPoint>
    {
        public override IoPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            IoPoint.Parse(reader.GetString()) ?? throw new JsonException($"'{reader.GetString()}' is not an IO point such as DI1 or DO1.");

        public override void Write(Utf8JsonWriter writer, IoPoint value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

/// <param name="Version">The version in effect: 1 for the first import.</param>
/// <param name="CreatedAt">When it was stored.</param>
/// <param name="Source">How it came, for example <c>import</c>.</param>
public sealed record SiteConfigurationResponse(int Version, DateTimeOffset CreatedAt, string Source, SiteConfiguration Configuration);
