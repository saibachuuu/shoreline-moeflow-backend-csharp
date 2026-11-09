using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class TeamInsightEndpoints
{
    public static void MapTeamInsights(this WebApplication app)
    {
        app.MapGet("/v1/teams/{teamId}/insight/users", async (string teamId, HttpContext context,
            CurrentUser current, TeamMemberService teamMembers, TeamInsightService insight, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await teamMembers.GetTeamAsync(teamId, ct);
            var page = Page(context, 10);
            var (rows, count) = await insight.ListUsersAsync(actor, team, Query(context, "word"), page.Skip,
                page.Limit, context, ct);
            page.Apply(context.Response, count);
            return Results.Json(rows);
        });

        app.MapGet("/v1/teams/{teamId}/insight/users/{userId}/projects", async (string teamId, string userId,
            HttpContext context, CurrentUser current, TeamMemberService teamMembers, TeamInsightService insight,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            // Legacy decorator order: fetch User first, then Team.
            var subject = await insight.GetUserAsync(userId, ct);
            var team = await teamMembers.GetTeamAsync(teamId, ct);
            var page = Page(context, 100);
            var (rows, count) = await insight.ListUserProjectsAsync(actor, team, subject, page.Skip, page.Limit, ct);
            page.Apply(context.Response, count);
            return Results.Json(rows);
        });

        app.MapGet("/v1/teams/{teamId}/insight/projects", async (string teamId, HttpContext context,
            CurrentUser current, TeamMemberService teamMembers, TeamInsightService insight, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var team = await teamMembers.GetTeamAsync(teamId, ct);
            var page = Page(context, 10);
            var (rows, count) = await insight.ListProjectsAsync(actor, team, Query(context, "word"), page.Skip,
                page.Limit, context, ct);
            page.Apply(context.Response, count);
            return Results.Json(rows);
        });

        app.MapGet("/v1/teams/{teamId}/insight/projects/{projectId}/users", async (string teamId, string projectId,
            HttpContext context, CurrentUser current, TeamMemberService teamMembers, ProjectService projects,
            TeamInsightService insight, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            // Legacy decorator order: fetch Project first, then Team.
            var project = await projects.GetProjectAsync(projectId, ct);
            var team = await teamMembers.GetTeamAsync(teamId, ct);
            var page = Page(context, 100);
            var (rows, count) = await insight.ListProjectUsersAsync(actor, team, project, page.Skip, page.Limit, ct);
            page.Apply(context.Response, count);
            return Results.Json(rows);
        });
    }

    private static string? Query(HttpContext context, string name)
    {
        var value = context.Request.Query[name];
        return value.Count == 0 ? null : value.ToString();
    }

    private static Pagination Page(HttpContext context, int maxLimit)
    {
        var page = Pagination.From(context.Request);
        return new Pagination(page.Page, Math.Min(page.Limit, maxLimit));
    }
}
