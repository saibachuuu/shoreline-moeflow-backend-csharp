using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class NotificationPublishEndpoints
{
    public static void MapNotificationPublish(this WebApplication app)
    {
        app.MapPost("/v1/notifications", async (
            JsonElement data, HttpContext context, CurrentUser current,
            NotificationReadService read, NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var note = await publish.PublishAsync(actor, data, IdempotencyKey(context), ct);
            return Results.Json(await read.DetailAsync(note, actor, management: true, ct: ct), statusCode: 202);
        });

        app.MapPost("/v1/notifications/preview", async (
            JsonElement data, HttpContext context, CurrentUser current,
            NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await publish.PreviewAsync(actor, data, Query(context, "q"), ct));
        });

        app.MapGet("/v1/notification-recipients", async (
            HttpContext context, CurrentUser current,
            NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await publish.CandidatesAsync(
                actor, Query(context, "category"), Query(context, "scope_id"),
                Query(context, "q"), Query(context, "cursor"), Query(context, "limit"), false, ct));
        });

        app.MapPatch("/v1/notifications/{notificationId}", async (
            string notificationId, JsonElement data, HttpContext context, CurrentUser current,
            NotificationReadService read, NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var note = await read.GetNoteAsync(notificationId, ct);
            var updated = await publish.EditDraftAsync(actor, note, data, ct);
            return Results.Json(await read.DetailAsync(updated, actor, management: true, ct: ct));
        });

        app.MapPost("/v1/notifications/{notificationId}/revoke", async (
            string notificationId, JsonElement data, HttpContext context, CurrentUser current,
            NotificationReadService read, NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var note = await read.GetNoteAsync(notificationId, ct);
            var updated = await publish.RevokeAsync(actor, note, data, IdempotencyKey(context), false, ct);
            return Results.Json(await read.DetailAsync(updated, actor, management: true, ct: ct));
        });

        app.MapPost("/v1/admin/notifications", async (
            JsonElement data, HttpContext context, CurrentUser current,
            NotificationReadService read, NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var parsed = NotificationPublishService.Object(data);
            if (NotificationPublishService.Text(parsed, "category") != "system" || HasScope(parsed))
                throw NotificationErrors.Forbidden("全站管理入口只能新建站点通知");
            var note = await publish.PublishAsync(actor, data, IdempotencyKey(context), ct);
            return Results.Json(await read.DetailAsync(note, actor, management: true, ct: ct), statusCode: 202);
        });

        app.MapPost("/v1/admin/notifications/preview", async (
            JsonElement data, HttpContext context, CurrentUser current,
            NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var parsed = NotificationPublishService.Object(data);
            if (NotificationPublishService.Text(parsed, "category") != "system" || HasScope(parsed))
                throw NotificationErrors.Forbidden("管理入口不能代发团队或项目消息");
            return Results.Json(await publish.PreviewAsync(actor, data, Query(context, "q"), ct));
        });

        app.MapGet("/v1/admin/notification-recipients", async (
            HttpContext context, CurrentUser current,
            NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var category = Query(context, "category") ?? "system";
            if (category != "system")
                throw NotificationErrors.Forbidden("管理入口只选择站点接收人");
            return Results.Json(await publish.CandidatesAsync(
                actor, category, Query(context, "scope_id"),
                Query(context, "q"), Query(context, "cursor"), Query(context, "limit"), true, ct));
        });

        app.MapPost("/v1/admin/notifications/{notificationId}/revoke", async (
            string notificationId, JsonElement data, HttpContext context, CurrentUser current,
            NotificationReadService read, NotificationPublishService publish, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var note = await read.GetNoteAsync(notificationId, ct);
            var updated = await publish.RevokeAsync(actor, note, data, IdempotencyKey(context), true, ct);
            return Results.Json(await read.DetailAsync(updated, actor, management: true, ct: ct));
        });
    }

    private static string? IdempotencyKey(HttpContext context) =>
        context.Request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;

    private static string? Query(HttpContext context, string name)
    {
        var value = context.Request.Query[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool HasScope(IReadOnlyDictionary<string, JsonElement> data) =>
        data.TryGetValue("scope_id", out var value) &&
        value.ValueKind != JsonValueKind.Null &&
        (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 0);
}
