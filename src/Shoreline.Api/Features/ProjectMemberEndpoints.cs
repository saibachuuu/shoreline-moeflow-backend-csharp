using System.Text.Json;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Project identity endpoints, ported from Python app/apis/identity.py:
// member list/changes/bind/merge/hard-delete, owner transfer, and the
// complete/clear/reopen lifecycle transitions.
public static class ProjectMemberEndpoints
{
    private static string RequestId(HttpContext context) =>
        context.Request.Headers.TryGetValue("X-Request-ID", out var requestId)
            ? requestId.ToString()
            : context.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : "";

    private static async Task<JsonElement> BodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}").RootElement;
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            throw Infrastructure.IdentityErrors.InvalidRequest("invalid JSON body");
        }
    }

    private static JsonElementLite? Lite(JsonElement body) => new(body);

    public static void MapProjectMembers(this WebApplication app)
    {
        var members = app.MapGroup("/v1/projects/{projectId}/members");

        members.MapGet("", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectMemberService memberService, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var page = Pagination.From(context.Request);
            var rows = await memberService.ListMembersAsync(project, actor, context.Request.Query["status"],
                context.Request.Query["tag"], context.Request.Query["word"], ct);
            page.Apply(context.Response, rows.Length);
            return rows.Skip(page.Skip).Take(page.Limit).ToArray();
        });

        members.MapPost("/changes", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectMemberService memberService, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (result, httpStatus) = await memberService.ApplyChangesAsync(
                project, actor, body, RequestId(context), ct);
            if (httpStatus != 200) context.Response.StatusCode = httpStatus;
            return Results.Content(result.ToJson(), "application/json");
        });

        members.MapPost("/{memberId}/bind", async (string projectId, string memberId, HttpContext context,
            CurrentUser current, ProjectService projects, ProjectMemberService memberService, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (member, auditId) = await memberService.BindMemberAsync(project, actor, memberId, body, RequestId(context), ct);
            return new
            {
                member = await memberService.MemberToApiAsync(member, project, ct),
                audit_event_id = auditId.ToString()
            };
        });

        members.MapPost("/{memberId}/merge", async (string projectId, string memberId, HttpContext context,
            CurrentUser current, ProjectService projects, ProjectMemberService memberService, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (target, source, auditId) = await memberService.MergeAsync(project, actor, memberId, body, RequestId(context), ct);
            return new
            {
                member = await memberService.MemberToApiAsync(target, project, ct),
                source_member = await memberService.MemberToApiAsync(source, project, ct),
                audit_event_id = auditId.ToString()
            };
        });

        members.MapDelete("/{memberId}", async (string projectId, string memberId, HttpContext context,
            CurrentUser current, ProjectService projects, ProjectMemberService memberService, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var auditId = await memberService.HardDeleteExternalAsync(project, actor, memberId, RequestId(context), ct);
            return new { audit_event_id = auditId.ToString() };
        });

        var owner = app.MapGroup("/v1/projects/{projectId}/owner");
        owner.MapPost("/transfer", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectMemberService memberService, ProjectLifecycleService lifecycle,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (updated, member, changed) = await lifecycle.TransferOwnerAsync(actor, project, Lite(body), RequestId(context), ct);
            var summary = await projects.MemberSummariesAsync(updated, compact: false, ct);
            var view = await projects.ViewAsync(updated, actor, Chinese(context), ct);
            // app/apis/identity.py:43-51 nests member_summary inside project.
            view["member_summary"] = summary;
            return new
            {
                project = view,
                member = await memberService.MemberToApiAsync(member, updated, ct),
                changed
            };
        });

        var lifecycleGroup = app.MapGroup("/v1/projects/{projectId}");
        lifecycleGroup.MapPost("/complete", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectLifecycleService lifecycle, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (updated, changed) = await lifecycle.CompleteAsync(actor, project, Lite(body), RequestId(context), ct);
            var summary = await projects.MemberSummariesAsync(updated, compact: false, ct);
            var view = await projects.ViewAsync(updated, actor, Chinese(context), ct);
            // app/apis/identity.py:43-51 nests member_summary inside project.
            view["member_summary"] = summary;
            return new
            {
                project = view,
                status = ProjectLifecycleService.StatusName((int)updated.Integer("st")),
                status_version = (int)updated.Integer("stv"),
                changed
            };
        });

        lifecycleGroup.MapPost("/clear", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectLifecycleService lifecycle, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (updated, changed) = await lifecycle.ClearAsync(actor, project, Lite(body), RequestId(context), ct);
            var summary = await projects.MemberSummariesAsync(updated, compact: false, ct);
            var view = await projects.ViewAsync(updated, actor, Chinese(context), ct);
            // app/apis/identity.py:43-51 nests member_summary inside project.
            view["member_summary"] = summary;
            return new
            {
                project = view,
                status = ProjectLifecycleService.StatusName((int)updated.Integer("st")),
                status_version = (int)updated.Integer("stv"),
                changed
            };
        });

        lifecycleGroup.MapPost("/reopen", async (string projectId, HttpContext context, CurrentUser current,
            ProjectService projects, ProjectLifecycleService lifecycle, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var project = await projects.GetProjectAsync(projectId, ct);
            var body = await BodyAsync(context.Request);
            var (updated, changed) = await lifecycle.ReopenAsync(actor, project, Lite(body), RequestId(context), ct);
            var summary = await projects.MemberSummariesAsync(updated, compact: false, ct);
            var view = await projects.ViewAsync(updated, actor, Chinese(context), ct);
            // app/apis/identity.py:43-51 nests member_summary inside project.
            view["member_summary"] = summary;
            return new
            {
                project = view,
                status = ProjectLifecycleService.StatusName((int)updated.Integer("st")),
                status_version = (int)updated.Integer("stv"),
                changed
            };
        });
    }

    private static bool Chinese(HttpContext context) =>
        LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
}
