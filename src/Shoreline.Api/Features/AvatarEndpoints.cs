using Microsoft.AspNetCore.Http.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class AvatarEndpoints
{
    public static void MapAvatar(this WebApplication app)
    {
        app.MapPut("/v1/avatar", UploadAsync);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        CurrentUser currentUser,
        AvatarService avatars,
        CancellationToken ct)
    {
        var actor = await currentUser.RequireAsync(context, ct);

        // Match the existing upload route's pragmatic multipart limits. The
        // service still applies the configured, streamed avatar byte cap.
        var formOptions = new FormOptions
        {
            MultipartBodyLengthLimit = avatars.MaxUploadBytes() + 1024 * 1024
        };
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = formOptions.MultipartBodyLengthLimit;
        }

        IFormCollection? form = null;
        if (context.Request.HasFormContentType)
        {
            try
            {
                form = await context.Request.ReadFormAsync(formOptions, ct);
            }
            catch (InvalidDataException)
            {
                throw new ApiError("RequestEntityTooLarge", 2, "上传文件超过大小限制", 413);
            }
        }

        var result = await avatars.UploadAsync(
            actor,
            form?.Files["file"],
            form?["type"].ToString(),
            form?["id"].ToString(),
            ct);

        return Results.Json(new { message = result.Message, avatar = result.Avatar });
    }
}
