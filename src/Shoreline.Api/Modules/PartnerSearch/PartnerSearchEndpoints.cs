using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.PartnerSearch;

public static class PartnerSearchEndpoints
{
    public static void MapPartnerSearch(this WebApplication app)
    {
        // Partner lookup is intentionally public, like the Python view. OPTIONS
        // stays usable without an Origin header; the global CORS middleware
        // still answers real preflight requests before this handler.
        app.MapMethods("/v1/partner-search-query-entry", ["OPTIONS"], () => Results.Ok());
        app.MapPost("/v1/partner-search-query-entry", async (HttpContext context,
            PartnerSearchService search, CancellationToken ct) =>
        {
            var body = await OptionalJsonAsync(context.Request, ct);
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
            var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
            var result = await search.QueryAsync(body, forwardedFor, remoteAddress, ct);
            return PartnerSearchApiJson.ToObject(result);
        });

        app.MapMethods("/v1/partner-search-query-entry/settings", ["OPTIONS"], () => Results.Ok());
        app.MapGet("/v1/partner-search-query-entry/settings", async (HttpContext context,
            CurrentUser current, PartnerSearchService search, CancellationToken ct) =>
        {
            await RequireAdminAsync(context, current, ct);
            return PartnerSearchApiJson.ToObject(await search.GetSettingsAsync(ct));
        });
        app.MapPut("/v1/partner-search-query-entry/settings", async (HttpContext context,
            CurrentUser current, PartnerSearchService search, CancellationToken ct) =>
        {
            await RequireAdminAsync(context, current, ct);
            var body = await RequiredJsonAsync(context.Request, ct);
            return PartnerSearchApiJson.ToObject(await search.UpdateSettingsAsync(body, ct));
        });
    }

    /// <summary>
    /// Python's admin_required answers 403. The shared C# helper currently
    /// constructs the same NoPermissionError with its default 400 status, so the
    /// module keeps the protocol-compatible status without changing core code.
    /// </summary>
    private static async Task RequireAdminAsync(HttpContext context, CurrentUser current,
        CancellationToken ct)
    {
        try
        {
            await current.RequireAdminAsync(context, ct);
        }
        catch (ApiError error) when (error.Code == 103 && error.Status != 403)
        {
            throw new ApiError(error.Error, error.Code, error.Message, 403);
        }
    }

    /// <summary>
    /// Mirrors Flask's request.get_json(silent=True): non-JSON content types,
    /// empty bodies and malformed JSON all become an empty object.
    /// </summary>
    private static async Task<JsonElement> OptionalJsonAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0) return default;
        var contentType = request.ContentType;
        if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return default;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<JsonElement> RequiredJsonAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0) throw ApiError.Invalid("Invalid request body");
        var contentType = request.ContentType;
        if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw ApiError.Invalid("Invalid request body");
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("Invalid request body");
        }
    }
}

/// <summary>
/// Converts the module's BSON views to plain CLR values before System.Text.Json
/// serializes them, keeping snake_case keys and ObjectId strings stable.
/// </summary>
internal static class PartnerSearchApiJson
{
    public static object? ToObject(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => ((BsonDocument)value).Elements.ToDictionary(
            element => element.Name, element => ToObject(element.Value), StringComparer.Ordinal),
        BsonType.Array => ((BsonArray)value).Select(ToObject).ToList(),
        BsonType.Null => null,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => ((BsonDateTime)value).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => value.ToDouble(),
        BsonType.String => value.AsString,
        _ => value.ToString(),
    };
}
