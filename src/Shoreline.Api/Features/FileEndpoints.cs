using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class FileEndpoints
{
    public static void MapFiles(WebApplication app)
    {
        app.MapGet("/v1/projects/{projectId}/files", ListFiles);
        app.MapPost("/v1/projects/{projectId}/files", UploadFile);
        app.MapGet("/v1/files/{fileId}", GetFile);
        app.MapPut("/v1/files/{fileId}", UpdateFile);
        app.MapDelete("/v1/files/{fileId}", DeleteFile);
        app.MapGet("/v1/projects/{projectId}/files/order", GetOrder);
        app.MapPut("/v1/projects/{projectId}/files/order", SaveOrder);
        app.MapGet("/v1/admin/files", AdminListFiles);
    }

    private static async Task<IResult> ListFiles(
        HttpContext context,
        string projectId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        if (project.Integer("st") != 0)
        {
            throw new ApiError("ProjectFinishedError", 4002, "项目已完结");
        }

        await RequireProjectAccessAsync(actor, project, permissions, projects, ct);

        var query = context.Request.Query;
        var orderBy = query["order_by"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        var listQuery = new FileListQuery(
            First(query["parent_id"]),
            IsTrue(First(query["only_folder"])),
            IsTrue(First(query["only_file"])),
            orderBy,
            First(query["word"]),
            First(query["target"]));

        var page = Pagination.From(context.Request);
        var (items, count) = await files.ListAsync(project, listQuery, page, ct);
        page.Apply(context.Response, count);
        return Results.Json(items);
    }

    private static async Task<IResult> UploadFile(
        HttpContext context,
        string projectId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        if (project.Integer("st") != 0)
        {
            throw new ApiError("ProjectFinishedError", 4002, "项目已完结");
        }

        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!IdentityPermissions.CanProject(snapshot, project, "project:ADD_FILE"))
        {
            throw new ApiError("NoPermissionError", 103, "您没有此项目的上传文件权限");
        }

        FormOptions options = new() { MultipartBodyLengthLimit = 1024L * 1024 * 1024 };
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = 1024L * 1024 * 1024;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(options, ct);
        }
        catch (InvalidDataException)
        {
            throw new ApiError("RequestEntityTooLarge", 2, "上传文件超过大小限制", 413);
        }

        var upload = form.Files["file"];
        if (upload is null || string.IsNullOrEmpty(upload.FileName))
        {
            throw new ApiError("UploadFileNotFoundError", 109, "上传未包含文件");
        }

        var parent = form["parent_id"].ToString();
        var data = await files.UploadAsync(
            project,
            upload.FileName,
            upload,
            string.IsNullOrWhiteSpace(parent) ? null : parent,
            ct);
        return Results.Json(data);
    }

    private static async Task<IResult> GetFile(
        HttpContext context,
        string fileId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var file = await files.GetFileAsync(fileId, ct);
        var project = await projects.GetProjectAsync(file["p"].AsObjectId.ToString(), ct);
        await RequireProjectAccessAsync(actor, project, permissions, projects, ct);
        if (!file.Flag("ac"))
        {
            throw new ApiError("FileNotActivatedError", 4016, "此修订版未激活");
        }

        var target = First(context.Request.Query["target"]);
        var data = await files.GetAsync(file, project, target, ct);
        return Results.Json(data);
    }

    private static async Task<IResult> UpdateFile(
        HttpContext context,
        string fileId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var file = await files.GetFileAsync(fileId, ct);
        var project = await projects.GetProjectAsync(file["p"].AsObjectId.ToString(), ct);
        var snapshot = await RequireProjectAccessAsync(actor, project, permissions, projects, ct);

        var data = await ReadJsonAsync(context, ct);
        var parent = JsonText(data, "parent_id");
        var name = JsonText(data, "name");

        if (!string.IsNullOrEmpty(parent))
        {
            if (!IdentityPermissions.CanProject(snapshot, project, "project:MOVE_FILE"))
            {
                throw new ApiError("NoPermissionError", 103, "您没有权限移动文件");
            }

            await files.MoveAsync(file, project, parent == "root" ? null : parent, ct);
            return Results.Json(new { message = LegacySuccessMessages.Get(context, "移动成功") });
        }

        if (!string.IsNullOrEmpty(name))
        {
            if (!IdentityPermissions.CanProject(snapshot, project, "project:RENAME_FILE"))
            {
                throw new ApiError("NoPermissionError", 103, "您没有权限修改文件名");
            }

            await files.RenameAsync(file, project, name, ct);
            return Results.Json(new { message = LegacySuccessMessages.Get(context, "改名成功") });
        }

        throw ApiError.Validate("需要提供parent_id或name参数");
    }

    private static async Task<IResult> DeleteFile(
        HttpContext context,
        string fileId,
        CurrentUser current,
        ProjectService projects,
        IdentityPermissions permissions,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var file = await files.GetFileAsync(fileId, ct);
        var project = await projects.GetProjectAsync(file["p"].AsObjectId.ToString(), ct);
        var snapshot = await RequireProjectAccessAsync(actor, project, permissions, projects, ct);

        if (!IdentityPermissions.CanProject(snapshot, project, "project:DELETE_FILE"))
        {
            throw new ApiError("NoPermissionError", 103, "您没有权限删除文件");
        }

        await files.DeleteAsync(file, project, ct);
        return Results.Json(new { message = LegacySuccessMessages.Get(context, "删除成功") });
    }

    private static async Task<IResult> GetOrder(
        HttpContext context,
        string projectId,
        CurrentUser current,
        ProjectService projects,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        var view = await files.OrderAsync(actor, project, ct);
        return Results.Json(new
        {
            files = view.Files,
            version = view.Version,
            default_file_ids = view.DefaultFileIds,
        });
    }

    private static async Task<IResult> SaveOrder(
        HttpContext context,
        string projectId,
        CurrentUser current,
        ProjectService projects,
        FileService files,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        var data = await ReadJsonAsync(context, ct);

        if (!data.TryGetProperty("file_ids", out var idsElement) || idsElement.ValueKind != JsonValueKind.Array)
        {
            throw ApiError.Invalid("file_ids 参数缺失");
        }

        var ids = new List<string>();
        foreach (var item in idsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !ObjectId.TryParse(item.GetString(), out _))
            {
                throw new ApiError("InvalidObjectIdError", 101, "错误的ObjectId");
            }

            ids.Add(item.GetString()!);
        }

        var version = JsonText(data, "version");
        if (string.IsNullOrEmpty(version))
        {
            throw ApiError.Invalid("version 参数缺失");
        }

        var reset = data.TryGetProperty("reset_to_default", out var resetElement)
                    && resetElement.ValueKind == JsonValueKind.True;
        await files.SaveOrderAsync(actor, project, ids, version, reset, ct);
        return Results.Json(new { message = LegacySuccessMessages.Get(context, "文件顺序已保存") });
    }

    private static async Task<IResult> AdminListFiles(
        HttpContext context,
        CurrentUser current,
        FileService files,
        CancellationToken ct)
    {
        await current.RequireAdminAsync(context, ct);
        var page = Pagination.From(context.Request);
        var (items, count) = await files.AdminListAsync(page, ct);
        page.Apply(context.Response, count);
        return Results.Json(items);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<PermissionSnapshot> RequireProjectAccessAsync(
        BsonDocument actor,
        BsonDocument project,
        IdentityPermissions permissions,
        ProjectService projects,
        CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (snapshot.Has("project:ACCESS"))
        {
            return snapshot;
        }

        if (project.Contains("t") && !project["t"].IsBsonNull)
        {
            var team = await projects.GetTeamAsync(project["t"].AsObjectId.ToString(), ct);
            var teamSnapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
            if (teamSnapshot.Has("team:ACCESS"))
            {
                return snapshot;
            }
        }

        throw new ApiError("NoPermissionError", 103, "您没有此项目的访问权限");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("请求体格式错误");
        }
    }

    private static string? JsonText(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? First(Microsoft.Extensions.Primitives.StringValues values)
        => values.Count == 0 ? null : values[0];

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
