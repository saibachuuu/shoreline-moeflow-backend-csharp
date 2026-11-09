using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.ArchiveImport;

public static class ArchiveImportEndpoints
{
    public static void MapArchiveImports(this WebApplication app)
    {
        app.MapPost("/v1/projects/{projectId}/import-from-archive", ImportFromArchiveAsync);
        app.MapGet("/v1/projects/{projectId}/import-task", GetTaskAsync);
        app.MapPost("/v1/projects/{projectId}/import-task/dismiss", DismissAsync);
    }

    private static async Task<IResult> ImportFromArchiveAsync(string projectId, HttpContext context,
        CurrentUser current, IdentityPermissions permissions, ProjectService projects,
        ArchiveImportService imports, CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        if (project.Integer("st") != 0)
            throw new ApiError("ProjectFinishedError", 4002, "项目已完结");
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!IdentityPermissions.CanProject(snapshot, project, "project:ADD_FILE"))
            throw new ApiError("NoPermissionError", 103, "您没有此项目的上传文件权限");

        var request = await ReadJsonAsync(context, ct);
        var gid = RequiredText(request, "gid", "gid 不能为空");
        if (!gid.Trim().All(char.IsAsciiDigit))
            throw ApiError.Field("gid", "Invalid value.");
        var token = RequiredText(request, "token", "token 不能为空");
        var galleryUrl = OptionalText(request, "gallery_url");

        var task = await imports.CreateAsync(project, actor["_id"].AsObjectId.ToString(), gid, token,
            galleryUrl, ct);
        return Results.Json(new Dictionary<string, object?>
        {
            ["message"] = "归档导入任务已加入队列",
            ["task"] = ArchiveImportView.ToApi(task),
        });
    }

    private static async Task<IResult> GetTaskAsync(string projectId, HttpContext context, CurrentUser current,
        IdentityPermissions permissions, ProjectService projects, ArchiveImportService imports,
        CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, context, current, permissions, projects, ct);
        var task = await imports.LatestAsync(project["_id"].AsObjectId, ct);
        return Results.Json(new Dictionary<string, object?>
        {
            ["task"] = task is null ? null : ArchiveImportView.ToApi(task),
        });
    }

    private static async Task<IResult> DismissAsync(string projectId, HttpContext context, CurrentUser current,
        IdentityPermissions permissions, ProjectService projects, ArchiveImportService imports,
        CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, context, current, permissions, projects, ct);
        await imports.DismissAsync(project["_id"].AsObjectId, ct);
        return Results.Json(new Dictionary<string, object?>
        {
            ["message"] = "归档导入提示已关闭",
            ["dismissed"] = true,
        });
    }

    private static async Task<BsonDocument> RequireProjectAsync(string projectId, HttpContext context,
        CurrentUser current, IdentityPermissions permissions, ProjectService projects, CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!IdentityPermissions.CanProject(snapshot, project, "project:ACCESS"))
            throw new ApiError("NoPermissionError", 103, "您没有此项目的访问权限");
        return project;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpContext context, CancellationToken ct)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw ApiError.Invalid("Invalid request");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("Invalid request");
        }
    }

    private static string RequiredText(JsonElement request, string field, string emptyMessage)
    {
        if (!request.TryGetProperty(field, out var value))
            throw ApiError.Field(field, "Missing data for required field.");
        if (value.ValueKind != JsonValueKind.String)
            throw ApiError.Field(field, "Not a valid string.");
        var text = value.GetString() ?? "";
        if (text.Trim().Length == 0) throw ApiError.Field(field, emptyMessage);
        return text;
    }

    private static string OptionalText(JsonElement request, string field)
    {
        if (!request.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return "";
        if (value.ValueKind != JsonValueKind.String)
            throw ApiError.Field(field, "Not a valid string.");
        var text = value.GetString() ?? "";
        return text.Trim().Length == 0 ? "" : text;
    }
}
