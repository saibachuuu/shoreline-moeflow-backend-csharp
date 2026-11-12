using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shoreline.Api.Storage;

/// <summary>
/// Unified object-storage facade.
///
/// The public API remains the small S3-shaped surface used by the existing
/// application. Relative keys are the only key space exposed to callers; the
/// configured <c>Storage:KeyPrefix</c> is applied once inside the S3 backend.
/// </summary>
public sealed class ObjectStorage : IStorageBackend
{
    /// <summary>
    /// Default lifetime for read URLs. Python signs every storage URL with
    /// 604800 seconds (7 days, <c>app/services/oss.py</c>), so the C# build uses
    /// the same window: a link copied from a project page must keep working
    /// exactly as long as it does on the legacy backend.
    /// </summary>
    public static readonly TimeSpan DefaultSignedUrlValidity = TimeSpan.FromDays(7);

    private readonly IStorageBackend backend;

    public ObjectStorage(IConfiguration configuration)
        : this(configuration, NullLogger<ObjectStorage>.Instance)
    {
    }

    public ObjectStorage(IConfiguration configuration, ILogger<ObjectStorage>? logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        logger ??= NullLogger<ObjectStorage>.Instance;

        var configuredType = FirstNonEmpty(
            configuration["Storage:Type"],
            LegacyStorageType(configuration),
            configuration["Storage:BucketStyle"],
            "S3");

        var provider = ResolveProvider(configuredType, logger);
        var domain = ParseOptionalHttpUri(
            FirstNonEmpty(configuration["Storage:Domain"], configuration["STORAGE_DOMAIN"]),
            "Storage:Domain");

        if (provider == StorageProvider.Local)
        {
            var root = FirstNonEmpty(
                configuration["Storage:Root"],
                configuration["Storage:LocalRoot"],
                string.Empty);

            if (root.Length == 0)
            {
                throw new InvalidOperationException(
                    "Storage:Root (or Storage:LocalRoot) is required for Local storage.");
            }

            backend = new LocalStorageBackend(root, domain);
            return;
        }

        var bucket = Required(configuration, "Storage:Bucket", "OSS_BUCKET_NAME");
        var endpoint = ParseHttpUri(
            FirstNonEmpty(configuration["Storage:Endpoint"], configuration["OSS_ENDPOINT"]),
            "Storage:Endpoint");

        if (endpoint.Host.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Storage:Endpoint points at Aliyun OSS, but Aliyun native signing has not been ported. "
                + "Refusing to silently treat it as generic S3-compatible storage.");
        }

        var accessKey = Required(configuration, "Storage:AccessKey", "OSS_ACCESS_KEY_ID");
        var secretKey = Required(configuration, "Storage:SecretKey", "OSS_ACCESS_KEY_SECRET");
        var region = FirstNonEmpty(
            configuration["Storage:Region"],
            configuration["OSS_S3_REGION"],
            "us-east-1");
        var forcePathStyle = configuration.GetValue(
            "Storage:ForcePathStyle",
            LegacyPathStyle(configuration));
        var viaCdn = configuration.GetValue(
            "Storage:ViaCdn",
            IsTrue(configuration["OSS_VIA_CDN"]));

        if (viaCdn && domain is null)
        {
            throw new InvalidOperationException(
                "Storage:Domain is required when Storage:ViaCdn is enabled.");
        }

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint.AbsoluteUri.TrimEnd('/'),
            ForcePathStyle = forcePathStyle,
            AuthenticationRegion = region,
            MaxErrorRetry = 2,

            // AWSSDK.S3 4.x defaults to WHEN_SUPPORTED, which can add optional
            // flexible-checksum headers that some S3-compatible providers reject.
            // R2 and the other supported providers work with checksums only when the
            // operation actually requires them. Do not disable HTTPS validation.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        var client = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            s3Config);

        backend = new S3StorageBackend(
            client,
            bucket,
            FirstNonEmpty(configuration["Storage:KeyPrefix"], LegacyKeyPrefix(configuration)),
            viaCdn,
            domain);
    }

    public Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken,
        long? contentLength = null) =>
        backend.PutAsync(key, content, contentType, cancellationToken, contentLength);

    public Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken) =>
        backend.GetAsync(key, cancellationToken);

    public Task<ObjectInfo?> HeadAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        backend.HeadAsync(key, cancellationToken);

    public Task<long?> SizeAsync(string key, CancellationToken cancellationToken) =>
        backend.SizeAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        backend.DeleteAsync(key, cancellationToken);

    public Task CopyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken) =>
        backend.CopyAsync(source, destination, cancellationToken);

    public Task<string> SignGetAsync(string key, TimeSpan validity) =>
        backend.SignGetAsync(key, validity);

    public Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition) =>
        backend.SignGetAsync(key, validity, contentDisposition);

    public Task<ObjectPage> ListAsync(
        string prefix,
        string? startAfter,
        int maxKeys,
        CancellationToken cancellationToken = default) =>
        backend.ListAsync(prefix, startAfter, maxKeys, cancellationToken);

    public void Dispose() => backend.Dispose();

    private static StorageProvider ResolveProvider(string configured, ILogger logger)
    {
        var normalized = configured.Trim().ToUpperInvariant();

        if (normalized is "LOCAL" or "FILESYSTEM" or "FILE" or "DISK")
        {
            return StorageProvider.Local;
        }

        if (normalized is "ALI" or "ALIYUN" or "ALIYUN_OSS" or "OSS")
        {
            throw new NotSupportedException(
                "Aliyun OSS native signing is not implemented in this build. "
                + "Use an S3-compatible provider/endpoint, or port the Aliyun signer before enabling this type.");
        }

        if (normalized is "S3" or "R2" or "AWS" or "MINIO" or "GARAGE" or "S3_COMPATIBLE" or "S3-COMPATIBLE")
        {
            return StorageProvider.S3;
        }

        logger.LogWarning(
            "Unknown storage provider value '{Provider}'; falling back to S3-compatible mode. "
            + "Supported values are S3, R2, AWS, MINIO, GARAGE, S3_COMPATIBLE, and LOCAL.",
            SanitizeProvider(configured));

        return StorageProvider.S3;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Maps the Python deployment environment (<c>STORAGE_TYPE</c> plus
    /// <c>OSS_BUCKET_STYLE</c>) onto the C# provider names. Python's
    /// <c>OSS</c> value is ambiguous: with <c>OSS_BUCKET_STYLE=S3</c> it uses
    /// the standard S3 client, while <c>ALI</c> selects the Aliyun native SDK.
    /// </summary>
    private static string? LegacyStorageType(IConfiguration configuration)
    {
        var storageType = configuration["STORAGE_TYPE"];
        if (string.IsNullOrWhiteSpace(storageType))
        {
            return null;
        }

        var normalized = storageType.Trim().ToUpperInvariant();
        if (normalized == "LOCAL_STORAGE")
        {
            return "LOCAL";
        }

        if (normalized != "OSS")
        {
            return storageType;
        }

        var bucketStyle = configuration["OSS_BUCKET_STYLE"];
        if (string.IsNullOrWhiteSpace(bucketStyle))
        {
            // Python defaults to S3 for the standard S3-compatible client.
            return "S3";
        }

        return bucketStyle.Trim().ToUpperInvariant() switch
        {
            "ALI" or "ALIYUN" or "ALIYUN_OSS" => "ALI",
            _ => "S3",
        };
    }

    private static string? LegacyKeyPrefix(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["STORAGE_TYPE"]))
        {
            return null;
        }

        // Python applies OSS_KEY_PREFIX only to the standard S3 branch and
        // defaults it to storage/ to stay compatible with historical objects.
        if (string.Equals(
                configuration["STORAGE_TYPE"]?.Trim(),
                "OSS",
                StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(configuration["OSS_KEY_PREFIX"], "storage/");
        }

        return null;
    }

    private static bool LegacyPathStyle(IConfiguration configuration)
    {
        var addressingStyle = configuration["OSS_S3_ADDRESSING_STYLE"];
        if (string.IsNullOrWhiteSpace(addressingStyle))
        {
            return true;
        }

        return !string.Equals(addressingStyle.Trim(), "virtual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static string Required(IConfiguration configuration, string key, string? legacyKey = null)
    {
        var value = FirstNonEmpty(configuration[key], legacyKey is null ? null : configuration[legacyKey]);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                legacyKey is null
                    ? $"{key} is required for S3 storage."
                    : $"{key} (or {legacyKey}) is required for S3 storage.");
        }

        return value;
    }

    private static Uri ParseHttpUri(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{settingName} is required.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{settingName} must be an absolute http(s) URI without user info, query, or fragment.");
        }

        return uri;
    }

    private static string? ParseOptionalHttpUri(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseHttpUri(value, settingName).AbsoluteUri.TrimEnd('/');
    }

    private static string SanitizeProvider(string value)
    {
        // Provider names are configuration labels, not free-form text. Keep the
        // warning useful for typo diagnosis without echoing arbitrary values
        // (for example a credential pasted into the wrong key).
        var sanitized = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                    ? character
                    : '?')
            .Take(64)
            .ToArray());

        return sanitized.Length == 0 ? "(empty)" : sanitized;
    }

    private enum StorageProvider
    {
        S3,
        Local,
    }
}
