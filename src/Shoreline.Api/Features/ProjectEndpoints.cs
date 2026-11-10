using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class ProjectEndpoints
{
    public static void MapProjects(this WebApplication app)
    {
        var project = app.MapGroup("/v1/projects/{projectId}");
        project.MapGet("", async (string projectId, HttpContext context, CurrentUser current, ProjectService projects,
            IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            var snapshot = await permissions.ProjectSnapshotAsync(actor, document, ct);
            if (!snapshot.Has("project:ACCESS"))
            {
                var team = await projects.GetTeamAsync(document["t"].ToString()!, ct);
                var teamSnapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
                if (!teamSnapshot.Has("team:ACCESS")) throw ApiError.Forbidden();
            }
            var view = await projects.ViewAsync(document, actor, Chinese(context), ct);
            var summary = await projects.MemberSummariesAsync(document, compact: false, ct);
            return new { project = view, member_summary = summary };
        });
        project.MapPut("", async (string projectId, JsonElement data, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            var updated = await projects.UpdateProjectAsync(actor, document, data, ct);
            var view = await projects.ViewAsync(updated, actor, Chinese(context), ct);
            var summary = await projects.MemberSummariesAsync(updated, compact: false, ct);
            return new { message = "修改成功", project = view, member_summary = summary };
        });
        project.MapGet("/targets", async (string projectId, HttpContext context, CurrentUser current, ProjectService projects,
            MongoStore store, IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            var snapshot = await permissions.ProjectSnapshotAsync(actor, document, ct);
            if (!snapshot.Has("project:ACCESS")) throw ApiError.Forbidden();
            var rows = await store.Collection("target").Find(new BsonDocument("t", document["_id"]))
                .Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            var views = await Task.WhenAll(rows.Select(row => projects.TargetViewAsync(row, Chinese(context), ct)));
            return new { count = views.Length, targets = views };
        });
        project.MapPost("/targets", async (string projectId, JsonElement data, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            var code = data.OptionalText("language", 32);
            var target = await projects.CreateTargetAsync(actor, document, code, ct);
            return new { message = "添加目标语言成功", target = await projects.TargetViewAsync(target, Chinese(context), ct) };
        });

        var teamProjects = app.MapGroup("/v1/teams/{teamId}/projects");
        teamProjects.MapGet("", async (string teamId, HttpContext context, CurrentUser current, ProjectService projects,
            MongoStore store, IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            var teamSnapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
            if (!teamSnapshot.Has("team:ACCESS")) throw ApiError.Forbidden();
            var page = Pagination.From(context.Request);
            var (rows, count) = await ListProjectsAsync(projects, store, permissions, actor, team, context.Request.Query, page, Chinese(context), ct);
            page.Apply(context.Response, count);
            return new { projects = rows };
        });
        teamProjects.MapPost("", async (string teamId, JsonElement data, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            var document = await projects.CreateProjectAsync(actor, team, data, ct);
            var view = await projects.ViewAsync(document, actor, Chinese(context), ct);
            var summary = await projects.MemberSummariesAsync(document, compact: false, ct);
            return new { message = "创建成功", project = view, member_summary = summary };
        });

        var teamSets = app.MapGroup("/v1/teams/{teamId}/project-sets");
        teamSets.MapGet("", async (string teamId, HttpContext context, CurrentUser current, ProjectService projects,
            MongoStore store, IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            var teamSnapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
            if (!teamSnapshot.Has("team:ACCESS")) throw ApiError.Forbidden();
            var page = Pagination.From(context.Request);
            var filter = new BsonDocument("t", team["_id"]);
            var word = context.Request.Query["word"].ToString();
            if (word.Length > 0) filter["n"] = new BsonRegularExpression(Regex.Escape(word), "i");
            var count = await store.Collection("project_set").CountDocumentsAsync(filter, cancellationToken: ct);
            var rows = await store.Collection("project_set").Find(filter)
                .Sort(new BsonDocument("_id", 1)).Skip(page.Skip).Limit(page.Limit).ToListAsync(ct);
            page.Apply(context.Response, count);
            return rows.Select(ProjectService.ProjectSetListView).ToArray();
        });
        teamSets.MapPost("", async (string teamId, JsonElement data, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            var set = await projects.CreateProjectSetAsync(actor, team, data.RequiredText("name", 40), ct);
            return new { message = "创建成功", project_set = ProjectService.ProjectSetView(set) };
        });

        var projectSet = app.MapGroup("/v1/project-sets/{projectSetId}");
        projectSet.MapGet("", async (string projectSetId, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var set = await projects.GetProjectSetAsync(projectSetId, ct);
            var team = await projects.GetTeamAsync(set["t"].ToString()!, ct);
            var teamSnapshot = await TeamSnapshotAsync(context, actor, team, ct);
            if (!teamSnapshot.Has("team:ACCESS")) throw ApiError.Forbidden();
            return ProjectService.ProjectSetView(set);
        });
        projectSet.MapPut("", async (string projectSetId, JsonElement data, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var set = await projects.GetProjectSetAsync(projectSetId, ct);
            var updated = await projects.UpdateProjectSetAsync(actor, set, data.RequiredText("name", 40), ct);
            return new { message = "修改成功", project_set = ProjectService.ProjectSetView(updated) };
        });
        projectSet.MapDelete("", async (string projectSetId, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var set = await projects.GetProjectSetAsync(projectSetId, ct);
            await projects.DeleteProjectSetAsync(actor, set, ct);
            return new { message = "删除成功" };
        });

        var target = app.MapGroup("/v1/targets/{targetId}");
        target.MapDelete("", async (string targetId, HttpContext context, CurrentUser current, ProjectService projects, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var row = await projects.GetTargetAsync(targetId, ct);
            await projects.DeleteTargetAsync(actor, row, ct);
            return new { message = "删除成功" };
        });
    }

    private static async Task<(List<object>, long)> ListProjectsAsync(ProjectService projects, MongoStore store,
        IdentityPermissions permissions, BsonDocument actor, BsonDocument team, IQueryCollection query,
        Pagination page, bool chinese, CancellationToken ct)
    {
        var filter = new BsonDocument("t", team["_id"]);
        var word = query["word"].ToString();
        if (word.Length > 0) filter["ns"] = new BsonRegularExpression(Regex.Escape(word), "i");
        var statuses = ParseStatus(query["status"].ToString());
        if (statuses is not null) filter["st"] = new BsonDocument("$in", new BsonArray(statuses.Select(s => BsonInt32.Create(s))));
        var setIds = ParseTokens(query["project_sets"].ToString());
        if (setIds is not null)
        {
            var ids = new List<ObjectId>();
            foreach (var id in setIds)
            {
                var set = await projects.GetProjectSetAsync(id, ct);
                if (set["t"] != team["_id"]) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
                ids.Add(set["_id"].AsObjectId);
            }
            filter["ps"] = new BsonDocument("$in", new BsonArray(ids));
        }
        var count = await store.Collection("project").CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await store.Collection("project").Find(filter)
            .Sort(new BsonDocument("_id", 1)).Skip(page.Skip).Limit(page.Limit).ToListAsync(ct);
        var list = new List<object>();
        foreach (var row in rows)
        {
            var snapshot = await permissions.ProjectSnapshotAsync(actor, row, ct);
            var set = await projects.GetProjectSetAsync(row["ps"].ToString()!, ct);
            list.Add(projects.ListCardAsync(row, actor, chinese, snapshot, team, set));
        }
        return (list, count);
    }

    private static async Task<PermissionSnapshot> TeamSnapshotAsync(HttpContext context, BsonDocument actor, BsonDocument team, CancellationToken ct) =>
        await context.RequestServices.GetRequiredService<IdentityPermissions>().TeamSnapshotAsync(actor, team, ct);

    private static bool Chinese(HttpContext context) =>
        LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);

    private static int[]? ParseStatus(string raw)
    {
        var tokens = ParseTokens(raw);
        if (tokens is null) return null;
        var values = new List<int>();
        foreach (var token in tokens)
        {
            switch (token.ToUpperInvariant())
            {
                case "NORMAL": case "WORKING": values.Add(0); break;
                case "CLEARED": values.Add(1); break;
                case "COMPLETED": values.Add(5); values.Add(1); break;
                default: throw new ApiError("RequestDataWrongError", 111, "Invalid status");
            }
        }
        return values.ToArray();
    }

    private static string[]? ParseTokens(string raw)
    {
        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? null : tokens;
    }
}
