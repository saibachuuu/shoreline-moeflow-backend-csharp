using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// N3 routes only. N1 owns the inbox/preferences/policy surface and N2 owns
// publish/edit/revoke; these five GETs are the remaining management reads.
public static class NotificationManagementEndpoints
{
    public static void MapNotificationManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/notifications/sent", async (HttpContext context, CurrentUser current,
            NotificationManagementService management, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await management.SentAsync(actor,
                NotificationManagementQuery.FromQuery(context.Request.Query), ct));
        });

        app.MapGet("/v1/notifications/{notificationId}", async (string notificationId,
            HttpContext context, CurrentUser current, NotificationManagementService management,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await management.DetailAsync(actor, notificationId, false, ct));
        });

        app.MapGet("/v1/admin/notifications", async (HttpContext context, CurrentUser current,
            NotificationManagementService management, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await management.AdminListAsync(actor,
                NotificationManagementQuery.FromQuery(context.Request.Query), ct));
        });

        app.MapGet("/v1/admin/notifications/{notificationId}", async (string notificationId,
            HttpContext context, CurrentUser current, NotificationManagementService management,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await management.DetailAsync(actor, notificationId, true, ct));
        });

        app.MapGet("/v1/me/notification-sync", async (HttpContext context, CurrentUser current,
            NotificationManagementService management, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await current.RequireAsync(context, ct);
            return Results.Json(await management.SyncAsync(actor,
                NotificationSyncQuery.From(context.Request.Query), ct));
        });
    }
}
