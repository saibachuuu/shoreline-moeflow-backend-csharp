using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class OutputEndpoints
{
    public static void MapOutputs(this WebApplication app)
    {
        var project = app.MapGroup("/v1/projects/{projectId}");

        project.MapPost("/outputs", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            ProjectService projects,
            IdentityPermissions permissions,
            OutputService outputs,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            await RequireOutputPermission(permissions, actor, document, ct);
            var created = await outputs.CreateAllAsync(document, actor, ct);
            var rows = new List<(BsonDocument Output, BsonDocument Target)>();
            foreach (var output in created)
            {
                var target = await projects.GetTargetAsync(output["t"].AsObjectId.ToString(), ct);
                rows.Add((output, target));
            }
            return await outputs.ViewsAsync(document, rows, context, ct);
        });

        var target = project.MapGroup("/targets/{targetId}");

        target.MapGet("/outputs", async (
            string projectId,
            string targetId,
            HttpContext context,
            CurrentUser current,
            ProjectService projects,
            IdentityPermissions permissions,
            OutputService outputs,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            await RequireOutputPermission(permissions, actor, document, ct);
            var targetRow = await projects.GetTargetAsync(targetId, ct);
            OutputService.EnsureTargetBelongsToProject(document, targetRow);
            var rows = await outputs.ListAsync(document["_id"].AsObjectId, targetRow["_id"].AsObjectId, ct);
            return await outputs.ViewsAsync(
                document, rows.Select(output => (output, targetRow)).ToList(), context, ct);
        });

        target.MapPost("/outputs", async (
            string projectId,
            string targetId,
            JsonElement data,
            HttpContext context,
            CurrentUser current,
            ProjectService projects,
            IdentityPermissions permissions,
            OutputService outputs,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            await RequireOutputPermission(permissions, actor, document, ct);
            var targetRow = await projects.GetTargetAsync(targetId, ct);
            OutputService.EnsureTargetBelongsToProject(document, targetRow);

            var type = RequiredType(data);
            var include = OptionalIdList(data, "file_ids_include");
            var exclude = OptionalIdList(data, "file_ids_exclude");
            var output = await outputs.CreateAsync(document, targetRow, actor, type, include, exclude, ct);
            return await outputs.ViewAsync(document, targetRow, output, context, ct);
        });

        // Python TeamProjectOutputListAPI.post (app/apis/team.py:498-506):
        // team-level export of every WORKING project/target.
        app.MapPost("/v1/teams/{teamId}/outputs", async (
            string teamId,
            HttpContext context,
            CurrentUser current,
            ProjectService projects,
            IdentityPermissions permissions,
            OutputService outputs,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            var snapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
            if (!snapshot.Has("team:AUTO_BECOME_PROJECT_ADMIN")) throw ApiError.Forbidden();
            // Python output_team_projects() enqueues the task whenever a worker is
            // alive and only runs it inline on an empty pool. A team with a thousand
            // projects cannot create every output row inside the request: the
            // synchronous version blew past the edge timeout and the caller saw a
            // failed export even though rows were being written.
            await outputs.ScheduleTeamAsync(team["_id"].AsObjectId, actor["_id"].AsObjectId, ct);
            return new { message = LegacySuccessMessages.Get(context, "创建导出任务成功") };
        });
    }

    private static async Task RequireOutputPermission(
        IdentityPermissions permissions,
        BsonDocument actor,
        BsonDocument project,
        CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!snapshot.Has("project:OUTPUT_TRA")) throw ApiError.Forbidden();
    }

    private static int RequiredType(JsonElement data)
    {
        if (!data.TryGetProperty("type", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var type)
            || type is not (OutputTypes.All or OutputTypes.OnlyText))
            throw ApiError.Field("type", "导出类型不正确");
        return type;
    }

    private static IReadOnlyList<ObjectId>? OptionalIdList(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw ApiError.Field(field, "文件列表格式不正确");

        var ids = new List<ObjectId>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !ObjectId.TryParse(item.GetString(), out var id))
                throw ApiError.Field(field, "文件 ID 格式不正确");
            ids.Add(id);
        }
        return ids;
    }
}
