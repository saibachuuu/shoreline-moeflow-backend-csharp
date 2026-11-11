using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Legacy multipart import endpoint:
/// POST /v1/teams/{teamId}/project-sets/{projectSetId}/project-zips
/// The legacy route name mentions zips, but the API receives two files:
/// "project" (exported JSON) and "labelplus" (UTF-8 text). No archive is read.
/// </summary>
public static class ProjectImportEndpoints
{
    private const long ProjectJsonLimit = 1024 * 1024;
    private const long LabelPlusLimit = 16 * 1024 * 1024;
    private const long MultipartLimit = 1024L * 1024 * 1024;

    public static IEndpointRouteBuilder MapProjectImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/teams/{teamId}/project-sets/{projectSetId}/project-zips", ImportAsync);
        return app;
    }

    private static async Task<IResult> ImportAsync(
        HttpContext context,
        string teamId,
        string projectSetId,
        CurrentUser currentUser,
        ProjectService projects,
        IdentityPermissions permissions,
        ProjectImportService imports,
        CancellationToken ct)
    {
        var actor = await currentUser.RequireAsync(context, ct);
        var team = await projects.GetTeamAsync(teamId, ct);
        var projectSet = await projects.GetProjectSetAsync(projectSetId, ct);

        if (!(await permissions.TeamSnapshotAsync(actor, team, ct)).Has("team:CREATE_PROJECT"))
            throw new ApiError("NoPermissionError", 103, "您没有权限在这个团队创建项目");

        // Project.create in Python rejects a project set from another team with
        // ProjectSetNotExistError (4022); the legacy import endpoint delegates to
        // that same check. (4014 in the old tree is a different move-folder error.)
        if (projectSet["t"] != team["_id"])
            throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MultipartLimit;
        var form = await context.Request.ReadFormAsync(new FormOptions
        {
            MultipartBodyLengthLimit = MultipartLimit
        }, ct);

        var projectUpload = form.Files.GetFile("project")
                            ?? throw ApiError.Field("project", "required");
        var labelPlusUpload = form.Files.GetFile("labelplus")
                              ?? throw ApiError.Field("labelplus", "required");
        if (projectUpload.Length > ProjectJsonLimit)
            throw ApiError.Field("project", "file is too large");
        if (labelPlusUpload.Length > LabelPlusLimit)
            throw ApiError.Field("labelplus", "file is too large");

        JsonDocument document;
        try
        {
            await using var projectStream = projectUpload.OpenReadStream();
            document = await JsonDocument.ParseAsync(projectStream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("invalid JSON body");
        }

        string labelPlusText;
        try
        {
            using var reader = new StreamReader(labelPlusUpload.OpenReadStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            labelPlusText = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw ApiError.Invalid("labelplus is not valid UTF-8");
        }

        using (document)
        {
            var prepared = await imports.PrepareProjectJsonAsync(document.RootElement, team, projectSet, ct);
            // Legacy Project.create parses the text before writing any row.
            imports.ValidateBeforeCreate(labelPlusText);

            var project = await projects.CreateProjectAsync(actor, team, prepared, ct);
            var requestId = RequestId(context);
            await imports.InitializeAfterCreateAsync(project, actor, labelPlusText, requestId, ct);

            var view = await projects.ViewAsync(project, actor, Chinese(context), ct);
            view["member_summary"] = await projects.MemberSummariesAsync(project, compact: false, ct);
            var response = new BsonDocument
            {
                ["message"] = LegacySuccessMessages.Get(context, "创建成功"),
                ["project"] = BsonDocument.Parse(JsonSerializer.Serialize(view))
            };
            return Results.Content(response.ToJson(), "application/json");
        }
    }

    private static string RequestId(HttpContext context)
    {
        var requestId = context.Request.Headers["X-Request-ID"].ToString();
        return string.IsNullOrEmpty(requestId) ? context.Request.Headers["Idempotency-Key"].ToString() : requestId;
    }

    private static bool Chinese(HttpContext context) =>
        context.Request.Headers["Accept-Language"].ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
