using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// HTTP surface for the label snapshot APIs. Registration is provided by
/// <see cref="LabelSnapshotRegistration"/> so the main Program can wire it when
/// the shared label middleware and services are enabled.
/// </summary>
public static class LabelSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapLabelSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/label-state", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var state = await service.GetLabelStateAsync(actor, ProjectId(projectId), ct);
            return Results.Json(state.ToApi());
        });

        app.MapGet("/v1/projects/{projectId}/snapshots", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var pagination = Pagination.From(context.Request);
            var page = await service.ListSnapshotsAsync(
                actor, ProjectId(projectId), pagination.Page, pagination.Limit, ct);
            pagination.Apply(context.Response, page.Total);
            return Results.Json(page.Items.Select(item => item.ToApi()).ToList());
        });

        app.MapPost("/v1/projects/{projectId}/snapshots", async (
            string projectId,
            JsonElement data,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var id = ProjectId(projectId);
            var actor = await current.RequireAsync(context, ct);
            // Python checks the create capability before reading the body.
            await service.RequireForProjectAsync(actor, id, "create", ct);
            var body = ReadObject(data);
            var name = SnapshotName(body);
            if (RuneLength(name) > 120)
                throw LabelSnapshotService.OperationError("快照名称最多 120 个字符");
            var snapshotId = await service.CreateSnapshotAsync(actor, id, name, "manual", null, ct);
            return Results.Json(new Dictionary<string, object?> { ["snapshot_id"] = snapshotId.ToString() });
        });

        app.MapPost("/v1/projects/{projectId}/snapshots/{snapshotId}/restore", async (
            string projectId,
            string snapshotId,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var id = ProjectId(projectId);
            var actor = await current.RequireAsync(context, ct);
            await service.RequireForProjectAsync(actor, id, "restore", ct);
            var result = await service.RestoreSnapshotAsync(actor, id, snapshotId, ct);
            return Results.Json(new Dictionary<string, object?> { ["snapshot_id"] = result.ToString() });
        });

        app.MapPost("/v1/projects/{projectId}/label-clear", async (
            string projectId,
            JsonElement data,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var body = ReadObject(data);
            var id = ProjectId(projectId);
            var field = OptionalString(body, "field");
            await service.RequireForProjectAsync(actor, id,
                field == "proofread" ? "clear_proofread" : "restore", ct);
            var deleteLabels = OptionalBool(body, "delete_labels", "清空选项必须为布尔值");
            var clearTips = OptionalBool(body, "clear_tips", "清空选项必须为布尔值");
            var snapshotId = await service.ClearLabelsAsync(
                actor, id, field, deleteLabels, clearTips, ct);
            return Results.Json(new Dictionary<string, object?> { ["snapshot_id"] = snapshotId.ToString() });
        });

        app.MapGet("/v1/projects/{projectId}/label-merge", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var id = ProjectId(projectId);
            // Python requires the merge capability before resolving the target.
            await service.RequireForProjectAsync(actor, id, "merge", ct);
            // Flask uses request.args.get("target_id"), not the C# camelCase name.
            var target = TargetId(context.Request.Query["target_id"].FirstOrDefault());
            var preview = await service.PreviewMergeAsync(actor, id, target, ct);
            return Results.Json(preview.ToApi());
        });

        app.MapPost("/v1/projects/{projectId}/label-merge", async (
            string projectId,
            JsonElement data,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var id = ProjectId(projectId);
            var actor = await current.RequireAsync(context, ct);
            await service.RequireForProjectAsync(actor, id, "merge", ct);
            var body = ReadObject(data);
            var target = TargetId(OptionalString(body, "target_id"));
            var versions = OptionalStringMap(body, "versions");
            var selections = OptionalStringMap(body, "selections");
            var snapshotId = await service.MergeLabelsAsync(
                actor, id, target, OptionalString(body, "token"), versions, selections, ct);
            return Results.Json(new Dictionary<string, object?> { ["snapshot_id"] = snapshotId.ToString() });
        });

        app.MapPost("/v1/projects/{projectId}/label-recover", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            LabelSnapshotService service,
            CancellationToken ct) =>
        {
            var id = ProjectId(projectId);
            var actor = await current.RequireAsync(context, ct);
            await service.RequireForProjectAsync(actor, id, "restore", ct);
            await service.RecoverAsync(actor, id, ct);
            return Results.Json(new Dictionary<string, object?> { ["recovered"] = true });
        });

        return app;
    }

    private static ObjectId ProjectId(string value) =>
        ObjectId.TryParse(value, out var id)
            ? id
            : throw new ApiError("ProjectNotExistError", 4001, "项目不存在");

    private static ObjectId TargetId(string? value) =>
        ObjectId.TryParse(value, out var id)
            ? id
            : throw new ApiError("TargetNotExistError", 4024, "项目目标语言不存在");

    private static JsonElement ReadObject(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw LabelSnapshotService.OperationError("请求数据无效");
        return data;
    }

    private static string? OptionalString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string SnapshotName(JsonElement body)
    {
        if (!body.TryGetProperty("name", out var value)) return "";
        if (value.ValueKind != JsonValueKind.String)
            throw LabelSnapshotService.OperationError("快照名称最多 120 个字符");
        return value.GetString() ?? "";
    }

    private static bool OptionalBool(JsonElement body, string name, string message)
    {
        if (!body.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        throw LabelSnapshotService.OperationError(message);
    }

    private static Dictionary<string, string?>? OptionalStringMap(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        // Python's data.get(name, {}) only defaults when the key is absent.
        // An explicit null (or any non-dict) must fail schema validation.
        if (value.ValueKind != JsonValueKind.Object)
            throw LabelSnapshotService.OperationError("冲突选择数据无效");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        return result;
    }

    private static int RuneLength(string value) =>
        value.EnumerateRunes().Count();
}
