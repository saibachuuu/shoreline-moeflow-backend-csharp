using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Storage;

/// <summary>
/// Serves media for storage sources that have the reverse-proxy switch on.
///
/// The visitor asks the site's own domain for <c>/api/v1/media/{source}/{key}</c>
/// and this endpoint streams the object from the backend it really lives in, so
/// the bucket endpoint and CDN domain never appear in a page. Because such a URL
/// is also pasted into chat and issue trackers, authorization is an HMAC over
/// (source, key, expiry) rather than a session cookie.
/// </summary>
public static class MediaProxyEndpoints
{
    public static void MapMediaProxy(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/media/{source}/{**key}", async (string source, string? key, HttpContext context,
            MediaProxySigner signer, ProjectStorageRouter router, CancellationToken ct) =>
        {
            var objectKey = key ?? "";
            try
            {
                StorageKey.ValidateObjectKey(objectKey);
            }
            catch (ArgumentException)
            {
                throw Invalid();
            }

            var query = context.Request.Query;
            if (!long.TryParse(query["e"], out var expires))
                throw Invalid();
            if (!signer.Verify(source, objectKey, expires, query["s"], query["cd"]))
                throw Invalid();

            StorageBackendLease lease;
            try
            {
                lease = await router.ForMediaTokenAsync(source, ct).ConfigureAwait(false);
            }
            catch (ApiError)
            {
                throw;
            }
            catch (Exception)
            {
                throw Unavailable();
            }

            await using (lease)
            {
                ObjectDownload download;
                try
                {
                    download = await lease.Backend.GetAsync(objectKey, ct).ConfigureAwait(false);
                }
                catch (Amazon.S3.AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw NotFound();
                }
                catch (FileNotFoundException)
                {
                    // Local-storage default backend signals a miss with an exception.
                    throw NotFound();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    throw Unavailable();
                }

                using (download)
                {
                    var remaining = Math.Max(0, expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    context.Response.Headers.CacheControl = "public, max-age=" + remaining.ToString();
                    if (MediaProxySigner.NormalizeDisposition(query["cd"]) is { Length: > 0 } disposition)
                        context.Response.Headers.ContentDisposition = FormatContentDisposition(disposition, objectKey);
                    context.Response.ContentType = download.ContentType;
                    context.Response.ContentLength = download.Length;
                    await download.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
                }
            }
        });
    }

    internal static string FormatContentDisposition(string disposition, string objectKey)
    {
        // Kestrel rejects any non-ASCII or control character in response headers.
        // Export archives routinely contain CJK/full-width characters in project
        // and target names, so emit RFC 6266 / RFC 5987 headers (ASCII fallback
        // in filename= plus UTF-8 in filename*=).
        var header = new ContentDispositionHeaderValue(disposition);
        var fileName = SanitizeFileName(Path.GetFileName(objectKey));
        if (fileName.Length > 0)
            header.SetHttpFileName(fileName);
        return header.ToString();
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        return string.Concat(fileName.Where(c => !char.IsControl(c)));
    }

    private static ApiError Invalid() => new("MediaSignatureInvalid", 1005, "媒体链接无效或已过期", 403);

    private static ApiError NotFound() => new("MediaNotFound", 1, "媒体不存在", 404);

    private static ApiError Unavailable() => new("MediaUnavailable", 6, "媒体存储暂不可用", 503);
}
