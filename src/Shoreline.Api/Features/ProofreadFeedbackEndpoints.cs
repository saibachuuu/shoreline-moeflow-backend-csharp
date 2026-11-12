using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// G3 proofread draft endpoint. The legacy route is
/// POST /v1/projects/{project_id}/targets/{target_id}/send-proofread-draft.
/// </summary>
public static class ProofreadFeedbackEndpoints
{
    public static void MapProofreadFeedback(this WebApplication app)
    {
        app.MapPost("/v1/projects/{projectId}/targets/{targetId}/send-proofread-draft",
            async (string projectId, string targetId, JsonElement data, HttpContext context,
                CurrentUser current, ProofreadFeedbackService feedback, CancellationToken ct) =>
            {
                var actor = await current.RequireAsync(context, ct);
                var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
                var result = await feedback.SendAsync(actor, projectId, targetId, data, key, ct);
                if (result.Body is Dictionary<string, object?> body &&
                    body.TryGetValue("message", out var message) && message is string text)
                    body["message"] = LegacySuccessMessages.Get(context, text);
                return Results.Json(result.Body, statusCode: result.StatusCode);
            });
    }
}
