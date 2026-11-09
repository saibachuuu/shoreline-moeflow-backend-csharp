using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class AdminStorageOverviewEndpoints
{
    public static void MapAdminStorageOverview(this WebApplication app)
    {
        app.MapGet("/v1/admin/storage/default", GetDefault);
    }

    private static async Task<IResult> GetDefault(
        HttpContext context,
        CurrentUser currentUser,
        AdminStorageOverviewService overview,
        CancellationToken ct)
    {
        await currentUser.RequireAdminAsync(context, ct);
        return Results.Json(overview.View());
    }
}
