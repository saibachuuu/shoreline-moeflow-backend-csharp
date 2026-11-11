using System.Text.Json;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Invitation endpoints, ported from Python app/apis/invitation.py:
// GET/POST /v1/{group_type}/{group_id}/invitations,
// PUT/PATCH/DELETE /v1/invitations/{id}, GET /v1/user/invitations.
public static class InvitationEndpoints
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

    public static void MapInvitations(this WebApplication app)
    {
        var group = app.MapGroup("/v1/{groupType}/{groupId}/invitations");

        group.MapGet("", async (string groupType, string groupId, HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var (scope, groupDoc) = await invitations.FetchGroupAsync(groupType, groupId, ct);
            if (!await invitations.CanInviteAsync(actor, scope, groupDoc, ct))
                throw ApiError.Forbidden();
            var statuses = ParseStatuses(context.Request.Query["status"]);
            var page = Pagination.From(context.Request);
            var rows = await invitations.ListAsync(scope, groupDoc, statuses, page.Skip, page.Limit, ct);
            page.Apply(context.Response, await invitations.ListCountAsync(scope, groupDoc, statuses, ct));
            return Results.Content(new BsonArray(await AttachViewsAsync(invitations, rows, context, ct)).ToJson(), "application/json");
        });

        group.MapPost("", async (string groupType, string groupId, HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = await BodyAsync(context.Request);
            var result = await invitations.PostInviteAsync(actor, groupType, groupId, body, context, RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapGet("/v1/user/invitations", async (HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var statuses = ParseStatuses(context.Request.Query["status"]);
            var page = Pagination.From(context.Request);
            var rows = await invitations.MeListAsync(actor, statuses, page.Skip, page.Limit, ct);
            page.Apply(context.Response, await invitations.MeListCountAsync(actor, statuses, ct));
            return Results.Content(new BsonArray(await AttachViewsAsync(invitations, rows, context, ct)).ToJson(), "application/json");
        });

        app.MapPut("/v1/invitations/{invitationId}", async (string invitationId, HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = await BodyAsync(context.Request);
            var result = await invitations.PutInvitationAsync(actor, invitationId, body, context, RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapPatch("/v1/invitations/{invitationId}", async (string invitationId, HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = await BodyAsync(context.Request);
            var allow = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("allow", out var property)
                && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False);
            if (!allow) throw ApiError.Field("allow", "allow is required");
            var result = await invitations.PatchInvitationAsync(actor, invitationId,
                body.GetProperty("allow").GetBoolean(), context, RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });

        app.MapDelete("/v1/invitations/{invitationId}", async (string invitationId, HttpContext context, CurrentUser current,
            InvitationService invitations, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var result = await invitations.DeleteInvitationAsync(actor, invitationId, RequestId(context), ct);
            if (result.Contains("message")) result["message"] = LegacySuccessMessages.Get(context, result["message"].AsString);
            return Results.Content(result.ToJson(), "application/json");
        });
    }

    private static int[] ParseStatuses(StringValues values)
    {
        return values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => int.TryParse(text, out var status) ? status : throw ApiError.Field("status", "expected an integer"))
            .Distinct().ToArray();
    }

    private static async Task<BsonDocument[]> AttachViewsAsync(InvitationService invitations,
        List<BsonDocument> rows, HttpContext context, CancellationToken ct)
    {
        var views = new BsonDocument[rows.Count];
        for (var index = 0; index < rows.Count; index++)
            views[index] = await invitations.ToApiAsync(rows[index], context, ct);
        return views;
    }
}
