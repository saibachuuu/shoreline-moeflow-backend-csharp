using System.Text.Json;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class TeamIdentityEndpoints
{
    public static void MapTeamIdentity(this WebApplication app)
    {
        var team = app.MapGroup("/v1/teams/{teamId}");
        team.MapGet("/members", async (string teamId, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var page = Pagination.From(context.Request);
            var (rows, count) = await members.ListAsync(document, actor, context.Request.Query["status"], context.Request.Query["word"], page, ct);
            page.Apply(context.Response, count);
            return await Task.WhenAll(rows.Select(row => members.ToApiAsync(row, users, context, ct)));
        });
        team.MapPost("/members", async (string teamId, JsonElement data, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var member = await members.AddAsync(document, actor, data, RequestId(context), ct);
            return new { member = await members.ToApiAsync(member, users, context, ct) };
        });
        team.MapPatch("/members/{memberId}", async (string teamId, string memberId, JsonElement data, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var member = await members.UpdateAsync(document, memberId, actor, data, RequestId(context), ct);
            return new { member = await members.ToApiAsync(member, users, context, ct) };
        });
        team.MapDelete("/members/{memberId}", async (string teamId, string memberId, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var member = await members.RemoveAsync(document, memberId, actor, RequestId(context), ct);
            return new { member = await members.ToApiAsync(member, users, context, ct) };
        });
        team.MapPatch("/members/{memberId}/aliases", async (string teamId, string memberId, JsonElement data, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var member = await members.UpdateAliasesAsync(document, memberId, actor, data, RequestId(context), ct);
            return new { member = await members.ToApiAsync(member, users, context, ct) };
        });
        team.MapPatch("/members/{memberId}/default-display-name", async (string teamId, string memberId, JsonElement data, HttpContext context, CurrentUser current, TeamMemberService members, UserView users, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await members.GetTeamAsync(teamId, ct);
            var member = await members.UpdateDisplayNameAsync(document, memberId, actor, data, RequestId(context), ct);
            return new { member = await members.ToApiAsync(member, users, context, ct) };
        });
        team.MapGet("/identity-tag-policy", async (string teamId, HttpContext context, CurrentUser current, TeamMemberService members, IdentityTagPolicyService policies, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return await policies.GetAsync(await members.GetTeamAsync(teamId, ct), actor, ct);
        });
        team.MapPatch("/identity-tag-policy", async (string teamId, JsonElement data, HttpContext context, CurrentUser current, TeamMemberService members, IdentityTagPolicyService policies, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            return await policies.UpdateAsync(await members.GetTeamAsync(teamId, ct), actor, data, RequestId(context), ct);
        });
    }

    private static string RequestId(HttpContext context) => context.Request.Headers["X-Request-ID"].FirstOrDefault()
        ?? context.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
}
