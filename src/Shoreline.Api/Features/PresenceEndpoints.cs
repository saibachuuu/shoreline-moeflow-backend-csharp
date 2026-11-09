using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Port of app/apis/presence.py. These routes intentionally keep the legacy
// contract: a valid token and an existing project/team are required, but the
// Python module has no membership/ACCESS permission guard. Do not read that as
// an authorization guarantee; the security review must revisit it separately.
public static class PresenceEndpoints
{
    public static void MapPresence(this WebApplication app)
    {
        var project = app.MapGroup("/v1/projects/{projectId}/presence");

        project.MapPost("/heartbeat", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, PresenceService presence, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            var action = await ActionAsync(context.Request, ct);
            await presence.HeartbeatAsync(document, actor, action, ct);
            return new
            {
                message = "ok",
                project_id = document["_id"].ToString(),
                active_users = await presence.ActiveUsersForProjectAsync(document, ct)
            };
        });

        project.MapPost("/leave", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, PresenceService presence, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            await presence.LeaveAsync(document, actor, ct);
            return new { message = "ok", project_id = document["_id"].ToString() };
        });

        project.MapGet("", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, PresenceService presence, CancellationToken ct) =>
        {
            await current.RequireAsync(context, ct);
            var document = await projects.GetProjectAsync(projectId, ct);
            return new
            {
                project_id = document["_id"].ToString(),
                active_users = await presence.ActiveUsersForProjectAsync(document, ct)
            };
        });

        app.MapGet("/v1/teams/{teamId}/projects/active-presence", async (string teamId, HttpContext context,
            CurrentUser current, ProjectService projects, PresenceService presence, CancellationToken ct) =>
        {
            await current.RequireAsync(context, ct);
            var team = await projects.GetTeamAsync(teamId, ct);
            return new { active_projects = await presence.ActivePresenceForTeamAsync(team, ct) };
        });
    }

    // Python uses request.get_json(silent=True) or {}: absent/malformed JSON,
    // a non-object root, a non-string action, or an empty action all mean
    // "working". Only a JSON string reaches the 64-code-point truncation.
    private static async Task<string?> ActionAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) return null;
        using var reader = new StreamReader(request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("action", out var action)) return null;
            return action.ValueKind == JsonValueKind.String ? action.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
