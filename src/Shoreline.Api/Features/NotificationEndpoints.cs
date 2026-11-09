using System.Text.Json;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// N1 routes only: inbox/detail/preferences/policy/cards. Publish, dispatch,
// preview, manage and sync are intentionally not registered until N2 can
// provide their full behavior instead of a half-working surface.
public static class NotificationEndpoints
{
    public static void MapNotifications(this WebApplication app)
    {
        app.MapGet("/v1/me/notification-capabilities", async (HttpContext context, CurrentUser current,
            NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.CapabilitiesAsync(actor,
                context.Request.Query["category"].FirstOrDefault(),
                context.Request.Query["scope_id"].FirstOrDefault(), ct));
        });

        app.MapGet("/v1/me/notifications/unread-counts", async (HttpContext context, CurrentUser current,
            NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.UnreadCountsAsync(actor, ct));
        });

        app.MapPost("/v1/me/notifications/mark-read", async (JsonElement data, HttpContext context,
            CurrentUser current, NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var query = context.Request.Query;
            return Results.Json(await notifications.MarkReadAsync(actor, data,
                query["q"].FirstOrDefault(), query["category"].FirstOrDefault(),
                query["cursor"].FirstOrDefault(), ct));
        });

        app.MapGet("/v1/me/notifications", async (HttpContext context, CurrentUser current,
            NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var query = context.Request.Query;
            return Results.Json(await notifications.InboxAsync(actor, query["q"].FirstOrDefault(),
                query["category"].FirstOrDefault(), query["cursor"].FirstOrDefault(),
                query["unread"].FirstOrDefault(), query["archived"].FirstOrDefault(),
                query["limit"].FirstOrDefault(), ct));
        });

        app.MapGet("/v1/me/notifications/{notificationId}", async (string notificationId, HttpContext context,
            CurrentUser current, NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            notifications.EnsureEnabled();
            var (note, receipt) = await notifications.GetReceiptAsync(actor, notificationId, ct);
            return Results.Json(await notifications.DetailAsync(note, actor, receipt: receipt, ct: ct));
        });

        app.MapPatch("/v1/me/notifications/{notificationId}", async (string notificationId, JsonElement data,
            HttpContext context, CurrentUser current, NotificationReadService notifications,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.UpdateReceiptAsync(actor, notificationId, data, ct));
        });

        app.MapGet("/v1/me/notification-preferences", async (HttpContext context, CurrentUser current,
            NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.PreferencesAsync(actor, ct));
        });

        app.MapPut("/v1/me/notification-preferences", async (JsonElement data, HttpContext context,
            CurrentUser current, NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.SavePreferencesAsync(actor, data, ct));
        });

        app.MapGet("/v1/teams/{teamId}/notification-policy", async (string teamId, HttpContext context,
            CurrentUser current, NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.PolicyAsync(actor, teamId, ct));
        });

        app.MapPut("/v1/teams/{teamId}/notification-policy", async (string teamId, JsonElement data,
            HttpContext context, CurrentUser current, NotificationReadService notifications,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.SavePolicyAsync(actor, teamId, data, ct));
        });

        app.MapGet("/v1/notification-project-cards", async (HttpContext context, CurrentUser current,
            NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var query = context.Request.Query;
            return Results.Json(await notifications.CardSearchAsync(actor,
                query["q"].FirstOrDefault(), query["cursor"].FirstOrDefault(), ct));
        });

        app.MapPost("/v1/notification-project-cards/resolve", async (JsonElement data, HttpContext context,
            CurrentUser current, NotificationReadService notifications, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await notifications.CardResolveAsync(actor, data, ct));
        });
    }
}
