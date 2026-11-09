using Amazon.S3.Model;

namespace Shoreline.Api.Storage;

/// <summary>
/// Metadata for one object in the relative key space exposed by <see cref="ObjectStorage"/>.
/// </summary>
public sealed record ObjectInfo(
    string Key,
    long Size,
    string? ETag = null,
    string? ContentType = null,
    DateTime? LastModified = null);

/// <summary>
/// A page of objects plus the relative key to use as <c>startAfter</c> for the next page.
/// </summary>
public sealed record ObjectPage(IReadOnlyList<ObjectInfo> Objects, string? Next);

/// <summary>
/// Streaming object download. Callers own the wrapper and must dispose it.
/// </summary>
public sealed class ObjectDownload : IDisposable
{
    private readonly GetObjectResponse? response;
    private readonly Stream content;

    public ObjectDownload(GetObjectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        this.response = response;
        content = response.ResponseStream;
        Length = response.ContentLength;
        ContentType = response.Headers?.ContentType ?? "application/octet-stream";
    }

    internal ObjectDownload(Stream content, long length, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        this.content = content;
        Length = length;
        ContentType = contentType;
    }

    public Stream Content => content;

    public long Length { get; }

    public string ContentType { get; }

    public void Dispose()
    {
        if (response is not null)
        {
            response.Dispose();
        }
        else
        {
            content.Dispose();
        }
    }
}
