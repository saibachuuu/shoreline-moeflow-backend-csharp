using System.Text.Json;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Application endpoints, ported from Python app/apis/application.py and the
// apply-page public-info endpoint in app/apis/group.py.
public static class ApplicationEndpoints
{
    private static string RequestId(HttpContext context) =>
        context.Request.Headers.TryGetValue("X-Request-ID", out var requestId)
            ? requestId.ToString()
            : context.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : "";

    private static async Task<JsonElement> BodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("invalid JSON body");
        }
    }

    private static IReadOnlyList<int>? ParseStatuses(StringValues values)
    {
        if (values.Count == 0) return null;
        var statuses = new List<int>();
        foreach (var value in values)
        {
            if (!int.TryParse(value, out var status))
                throw ApiError.Field("status", "expected an integer");
            statuses.Add(status);
        }
        return statuses;
    }

    private static async Task<BsonArray> RowsAsync(ApplicationService applications,
        IEnumerable<BsonDocument> rows, BsonDocument? user, HttpContext context, CancellationToken ct) =>
        new(await applications.ToApiListAsync(rows, user, context, ct));

    public static void MapApplications(this WebApplication app)
    {
        var group = app.MapGroup("/v1/{groupType}/{groupId}/applications");

        group.MapGet("", async (string groupType, string groupId, HttpContext context, CurrentUser current,
            ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var statuses = ParseStatuses(context.Request.Query["status"]);
            var page = Pagination.From(context.Request);
            var rows = await applications.ListAsync(actor, groupType, groupId, statuses, page.Skip, page.Limit, ct);
            page.Apply(context.Response,
                await applications.ListCountAsync(actor, groupType, groupId, statuses, ct));
            return Results.Content(
                (await RowsAsync(applications, rows, null, context, ct)).ToJson(), "application/json");
        });

        group.MapPost("", async (string groupType, string groupId, HttpContext context, CurrentUser current,
            ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = await BodyAsync(context.Request);
            if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("invalid JSON body");
            if (!body.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                throw ApiError.Field("message", "required");
            var result = await applications.ApplyAsync(actor, groupType, groupId,
                message.GetString() ?? "", context, ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapPatch("/v1/applications/{applicationId}", async (string applicationId, HttpContext context,
            CurrentUser current, ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = await BodyAsync(context.Request);
            if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("invalid JSON body");
            if (!body.TryGetProperty("allow", out var allow) ||
                allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw ApiError.Field("allow", "allow is required");
            var result = await applications.PatchAsync(actor, applicationId, allow.GetBoolean(), context,
                RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapDelete("/v1/applications/{applicationId}", async (string applicationId, HttpContext context,
            CurrentUser current, ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var result = await applications.DeleteAsync(actor, applicationId, RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapGet("/v1/user/related-applications", async (HttpContext context, CurrentUser current,
            ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var statuses = ParseStatuses(context.Request.Query["status"]);
            var page = Pagination.From(context.Request);
            var rows = await applications.RelatedAsync(actor, statuses, page.Skip, page.Limit, ct);
            page.Apply(context.Response, await applications.RelatedCountAsync(actor, statuses, ct));
            return Results.Content(
                (await RowsAsync(applications, rows, actor, context, ct)).ToJson(), "application/json");
        });

        app.MapGet("/v1/{groupType}/{groupId}/public-info", async (string groupType, string groupId,
            HttpContext context, CurrentUser current, ApplicationService applications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            BsonDocument result;
            try
            {
                result = await applications.PublicInfoAsync(actor, groupType, groupId, ct);
            }
            catch (ApiError error) when (error.Code == 3001)
            {
                var chinese = LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
                throw new ApiError(error.Error, error.Code, chinese ? "团队不存在" : "Team does not exist.");
            }
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });
    }
}
