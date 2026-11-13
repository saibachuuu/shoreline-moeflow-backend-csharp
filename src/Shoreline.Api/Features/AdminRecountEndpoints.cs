using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Rebuilds the cached label counters for one project.
///
/// <c>file.tsc</c>/<c>target.tsc</c>/<c>project.tsc</c> are maintained by
/// incremental +1/-1 updates, so any single skipped update leaves a permanent
/// drift that nothing corrects. This endpoint runs the same reconciler the
/// idempotent label path uses, which recomputes everything from the
/// translation collection.
/// </summary>
public static class AdminRecountEndpoints
{
    public static void MapAdminRecount(this WebApplication app)
    {
        app.MapPost("/v1/admin/projects/{projectId}/recount", Recount);
    }

    private static async Task<IResult> Recount(
        HttpContext context,
        string projectId,
        CurrentUser currentUser,
        MongoStore store,
        CancellationToken ct)
    {
        await currentUser.RequireAdminAsync(context, ct);
        if (!ObjectId.TryParse(projectId, out var id))
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");

        var project = await store.Collection("project")
            .Find(new BsonDocument("_id", id))
            .FirstOrDefaultAsync(ct);
        if (project is null)
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");

        var beforeTranslated = project.Integer("tsc");
        var beforeChecked = project.Integer("csc");
        var revision = project.Integer("label_revision", 0);

        var reconciler = new LabelCounterReconciler(store);
        await reconciler.ReconcileAsync(id, revision, DateTime.UtcNow, ct);

        var after = await store.Collection("project")
            .Find(new BsonDocument("_id", id))
            .FirstOrDefaultAsync(ct);
        var afterTranslated = after?.Integer("tsc") ?? 0;
        var afterChecked = after?.Integer("csc") ?? 0;

        return Results.Json(new
        {
            project_id = id.ToString(),
            revision,
            translated_source_count = afterTranslated,
            checked_source_count = afterChecked,
            translated_delta = afterTranslated - beforeTranslated,
            checked_delta = afterChecked - beforeChecked,
        });
    }
}
