using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Module routes. They are always mapped, even when no API key is configured:
/// the config endpoint reports <c>enabled: false</c> and a check request records
/// an explicit failed verdict instead of disappearing or reporting clear.
/// </summary>
public static class ZitengPartnerEndpoints
{
    public static void MapZitengPartner(this WebApplication app)
    {
        var checks = app.MapGroup("/v1/projects/{projectId}/ziteng-check");
        checks.MapGet("", GetCheckAsync);
        checks.MapPost("", PostCheckAsync);

        // The Python module's canonical path is /v1/ziteng-partner/config.
        // The short alias is kept for the route named in the migration request;
        // both answer with the same payload.
        app.MapGet("/v1/ziteng-partner/config", GetConfigAsync);
        app.MapGet("/v1/config/ziteng-partner", GetConfigAsync);
    }

    private static async Task<object> GetCheckAsync(string projectId, HttpContext context, CurrentUser current,
        ProjectService projects, IdentityPermissions permissions, ZitengPartnerStore store, CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        await RequireProjectAccessAsync(actor, project, permissions, ct);
        var check = await store.GetCheckAsync(projectId, ct);
        Dictionary<string, object?>? view = check?.ToApi();
        return new { check = view };
    }

    private static async Task<object> PostCheckAsync(string projectId, HttpContext context, CurrentUser current,
        ProjectService projects, IdentityPermissions permissions, ZitengPartnerService service, CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var project = await projects.GetProjectAsync(projectId, ct);
        await RequireProjectAccessAsync(actor, project, permissions, ct);
        var check = await service.RequestCheckAsync(projectId, project.Text("n"), ct);
        Dictionary<string, object?>? view = check?.ToApi();
        return new { check = view };
    }

    /// <summary>
    /// Every module route maps a missing project permission to 403 (the Python
    /// message is kept). The shared <c>ApiError.Forbidden()</c> carries the same
    /// code but its legacy default status is 400, so state the status here.
    /// </summary>
    private static async Task RequireProjectAccessAsync(BsonDocument actor, BsonDocument project,
        IdentityPermissions permissions, CancellationToken ct)
    {
        if (!IdentityPermissions.CanProject(await permissions.ProjectSnapshotAsync(actor, project, ct), project,
                "project:ACCESS"))
            throw new ApiError("NoPermissionError", 103, "您没有此项目的访问权限", 403);
    }

    private static async Task<object> GetConfigAsync(HttpContext context, CurrentUser current,
        ZitengPartnerSettings settings, CancellationToken ct)
    {
        await current.RequireAsync(context, ct);
        return new { enabled = settings.Enabled, max_results = settings.MaxResults };
    }
}
