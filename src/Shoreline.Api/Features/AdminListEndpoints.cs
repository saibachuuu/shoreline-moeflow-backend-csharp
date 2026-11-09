using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class AdminListEndpoints
{
    public static void MapAdminLists(this WebApplication app)
    {
        // GET /v1/admin/users already exists in AdminUserEndpoints.MapAdminUsers
        // (wired by Program.cs). Re-mapping it here would create an ambiguous
        // route, so only the missing v-code list is added.
        app.MapGet("/v1/admin/v-codes", async (HttpContext ctx, CurrentUser current,
            AdminListService lists, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            return await lists.VCodesAsync(ct);
        });
    }
}
