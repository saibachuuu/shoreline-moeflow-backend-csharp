using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Shoreline.Api.Storage;

/// <summary>
/// S3-compatible backend. A configured <c>Storage:KeyPrefix</c> is prepended exactly
/// once to every relative key; the prefix is opaque and is never trimmed or rewritten.
/// </summary>
internal sealed class S3StorageBackend : IStorageBackend
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly string keyPrefix;
    private readonly bool viaCdn;
    private readonly string? domain;

    public S3StorageBackend(
        AmazonS3Client client,
        string bucket,
        string keyPrefix,
        bool viaCdn,
        string? domain)
    {
        this.client = client;
        this.bucket = bucket;
        this.keyPrefix = keyPrefix;
        this.viaCdn = viaCdn;
        this.domain = domain;
    }

    public async Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        StorageKey.ValidateObjectKey(key);

        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = PhysicalKey(key),
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateObjectKey(key);
        var response = await client
            .GetObjectAsync(bucket, PhysicalKey(key), cancellationToken)
            .ConfigureAwait(false);

        return new ObjectDownload(response);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateObjectKey(key);

        try
        {
            var response = await client
                .GetObjectMetadataAsync(bucket, PhysicalKey(key), cancellationToken)
                .ConfigureAwait(false);

            return new ObjectInfo(
                key,
                response.ContentLength,
                StripETag(response.ETag),
                response.ContentType,
                response.LastModified);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task<long?> SizeAsync(string key, CancellationToken cancellationToken)
    {
        var info = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
        return info?.Size;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateObjectKey(key);

        var request = new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = PhysicalKey(key),
        };

        await client.DeleteObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        StorageKey.ValidateObjectKey(source, nameof(source));
        StorageKey.ValidateObjectKey(destination, nameof(destination));

        var request = new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = PhysicalKey(source),
            DestinationBucket = bucket,
            DestinationKey = PhysicalKey(destination),
        };

        await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> SignGetAsync(string key, TimeSpan validity) =>
        SignGetAsync(key, validity, null);

    public Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition)
    {
        StorageKey.ValidateObjectKey(key);

        if (validity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "Signed URL validity must be positive.");
        }

        if (viaCdn)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new InvalidOperationException(
                    "Storage:Domain is required when Storage:ViaCdn is enabled.");
            }

            // CDN URLs address the relative key directly. The S3 key prefix and AWS
            // credentials are deliberately absent from this URL.
            return Task.FromResult(domain.TrimEnd('/') + "/" + StorageKey.EncodePath(key));
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = PhysicalKey(key),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validity),
        };

        if (!string.IsNullOrEmpty(contentDisposition))
        {
            request.ResponseHeaderOverrides.ContentDisposition = contentDisposition;
        }

        return client.GetPreSignedURLAsync(request);
    }

    public async Task<ObjectPage> ListAsync(
        string prefix,
        string? startAfter,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        if (maxKeys is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "maxKeys must be between 1 and 1000.");
        }

        prefix ??= string.Empty;
        StorageKey.ValidatePrefix(prefix, nameof(prefix));

        if (!string.IsNullOrEmpty(startAfter))
        {
            StorageKey.ValidatePrefix(startAfter, nameof(startAfter));
        }

        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = PhysicalKey(prefix),
            MaxKeys = maxKeys,
        };

        if (!string.IsNullOrEmpty(startAfter))
        {
            request.StartAfter = PhysicalKey(startAfter);
        }

        var response = await client
            .ListObjectsV2Async(request, cancellationToken)
            .ConfigureAwait(false);

        var objects = new List<ObjectInfo>();
        foreach (var item in response.S3Objects ?? new List<S3Object>())
        {
            var rawKey = item.Key ?? string.Empty;
            if (!rawKey.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                // A defensive namespace check. ListObjectsV2 should only return the
                // requested prefix, but an S3-compatible server could be buggy.
                continue;
            }

            var relativeKey = rawKey[keyPrefix.Length..];
            if (relativeKey.Length == 0)
            {
                continue;
            }

            objects.Add(new ObjectInfo(
                relativeKey,
                item.Size ?? 0,
                StripETag(item.ETag),
                ContentType: null,
                LastModified: item.LastModified));
        }

        var next = response.IsTruncated == true && objects.Count > 0
            ? objects[^1].Key
            : null;

        return new ObjectPage(objects, next);
    }

    public void Dispose() => client.Dispose();

    private string PhysicalKey(string key) => keyPrefix + key;

    private static string? StripETag(string? etag) =>
        etag?.Trim().Trim('"');

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || exception.ErrorCode is "404" or "NoSuchKey" or "NotFound";
}
