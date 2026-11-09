using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// The legacy OCR pipeline is explicitly disabled: Python
/// app/tasks/ocr.py:421 raises NotImplementedError("OCR under reconstruction").
/// These routes therefore fail closed with HTTP 501 instead of queueing a job
/// or returning a fake success. They still require a token and still resolve
/// the addressed file/project, so callers get the legacy not-found errors for
/// invalid ids.
/// </summary>
public static class OcrEndpoints
{
    public static ApiError Unavailable() =>
        new("OcrUnavailableError", 5010, "OCR 功能暂不可用（后端重建中）", 501);

    public static void MapOcr(this WebApplication app)
    {
        app.MapPost("/v1/files/{fileId}/ocr", async (
            string fileId,
            HttpContext context,
            CurrentUser current,
            FileService files,
            CancellationToken ct) =>
        {
            await current.RequireAsync(context, ct);
            await files.GetFileAsync(fileId, ct);
            throw Unavailable();
        });

        app.MapPost("/v1/projects/{projectId}/ocr", async (
            string projectId,
            HttpContext context,
            CurrentUser current,
            ProjectService projects,
            CancellationToken ct) =>
        {
            await current.RequireAsync(context, ct);
            await projects.GetProjectAsync(projectId, ct);
            throw Unavailable();
        });
    }
}
