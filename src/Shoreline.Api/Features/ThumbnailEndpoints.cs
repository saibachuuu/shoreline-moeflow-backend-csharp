using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Legacy-compatible thumbnail rebuild endpoints. These are POST commands, not
/// list endpoints: a successful response only means the durable job was
/// queued; the image bytes are produced by ThumbnailJobHandler.
/// </summary>
public static class ThumbnailEndpoints
{
    public static void MapThumbnails(this WebApplication app)
    {
        app.MapPost("/v1/files/{fileId}/thumbnail", FileThumbnailAsync);
        app.MapPost("/v1/projects/{projectId}/thumbnails", ProjectThumbnailsAsync);
    }

    private static async Task<IResult> FileThumbnailAsync(
        HttpContext context,
        string fileId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        ThumbnailService thumbnails,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var file = await thumbnails.GetFileAsync(fileId, ct);
        var projectId = ThumbnailService.ProjectIdOf(file);
        var project = await projects.GetProjectAsync(projectId.ToString(), ct);

        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!IdentityPermissions.CanProject(snapshot, project, "project:ACCESS"))
        {
            throw ThumbnailService.NoProjectAccess();
        }

        if (file.Integer("t") != 2) throw ThumbnailService.FileTypeNotSupport();
        if (!thumbnails.LocalGenerationRequired)
        {
            return Results.Json(new { message = "当前存储模式无需重建缩略图", count = 0 });
        }

        await thumbnails.ScheduleAsync(file["_id"].AsObjectId, projectId, expectedLabelRevision: null, ct);
        return Results.Json(new { message = "缩略图生成任务已触发" });
    }

    private static async Task<IResult> ProjectThumbnailsAsync(
        HttpContext context,
        string projectId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        ThumbnailService thumbnails,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);

        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!IdentityPermissions.CanProject(snapshot, project, "project:CHANGE"))
        {
            throw ThumbnailService.NoProjectAccess();
        }

        if (!thumbnails.LocalGenerationRequired)
        {
            return Results.Json(new { message = "当前存储模式无需重建缩略图", count = 0 });
        }

        var count = await thumbnails.ScheduleProjectAsync(project["_id"].AsObjectId, ct);
        return Results.Json(new
        {
            message = $"已为 {count} 张图片触发缩略图生成任务",
            count
        });
    }
}
