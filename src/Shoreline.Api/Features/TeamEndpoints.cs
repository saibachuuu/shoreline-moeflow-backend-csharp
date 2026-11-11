using System.Text.Json;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class TeamEndpoints
{
    public static void MapTeams(this WebApplication app)
    {
        app.MapPost("/v1/teams", async (JsonElement data, HttpContext context, CurrentUser current, TeamService teams, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var created = await teams.CreateAsync(actor, data, RequestId(context), ct);
            return new { message = LegacySuccessMessages.Get(context, "创建成功"), team = await teams.ViewAsync(created, actor, Chinese(context), ct) };
        });
        app.MapGet("/v1/teams/{teamId}", async (string teamId, HttpContext context, CurrentUser current,
            TeamMemberService members, TeamService teams, IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await members.GetTeamAsync(teamId, ct);
            var snapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
            if (!snapshot.Has("team:ACCESS")) throw ApiError.Forbidden();
            return await teams.ViewAsync(team, actor, Chinese(context), ct, snapshot: snapshot);
        });
        app.MapPut("/v1/teams/{teamId}", async (string teamId, JsonElement data, HttpContext context, CurrentUser current,
            TeamMemberService members, TeamService teams, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var updated = await teams.UpdateAsync(await members.GetTeamAsync(teamId, ct), actor, data, ct);
            return new { message = LegacySuccessMessages.Get(context, "修改成功"), team = await teams.ViewAsync(updated, actor, Chinese(context), ct) };
        });
        app.MapGet("/v1/user/teams", async (HttpContext context, CurrentUser current, TeamService teams,
            IdentityPermissions permissions, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var page = Pagination.From(context.Request);
            var (rows, count) = await teams.ListAsync(actor, context.Request.Query["word"], page, true, ct);
            var snapshots = await permissions.TeamSnapshotsAsync(actor, rows, ct);
            page.Apply(context.Response, count);
            return await Task.WhenAll(rows.Select(team => teams.ViewAsync(team, actor, Chinese(context), ct,
                compact: true, snapshot: snapshots[team["_id"].AsObjectId])));
        });
        app.MapGet("/v1/teams", async (HttpContext context, CurrentUser current, TeamService teams, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            // Legacy global discovery is test-only, not a production directory.
            if (!app.Environment.IsEnvironment("Testing")) return Results.NoContent();
            var word = context.Request.Query["word"].ToString();
            if (word.Length == 0) throw new ApiError("RequestDataEmptyError", 110, "请求参数不能为空或没有需要的值");
            var page = Pagination.From(context.Request);
            var (rows, count) = await teams.ListAsync(actor, word, page, false, ct);
            var data = await Task.WhenAll(rows.Select(async team =>
            {
                // The old test-only endpoint does not attach viewer roles.
                var item = await teams.ViewAsync(team, null, Chinese(context), ct);
                item["joined"] = team.Flag("_joined");
                return item;
            }));
            page.Apply(context.Response, count);
            return Results.Json(data);
        });
    }

    private static bool Chinese(HttpContext context) => LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
    private static string RequestId(HttpContext context) => context.Request.Headers["X-Request-ID"].FirstOrDefault()
        ?? context.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
}
