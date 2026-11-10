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
            configuration["Storage:BucketStyle"],
            "S3");

        var provider = ResolveProvider(configuredType, logger);
        var domain = ParseOptionalHttpUri(configuration["Storage:Domain"], "Storage:Domain");

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

        var bucket = Required(configuration, "Storage:Bucket");
        var endpoint = ParseHttpUri(configuration["Storage:Endpoint"], "Storage:Endpoint");

        if (endpoint.Host.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Storage:Endpoint points at Aliyun OSS, but Aliyun native signing has not been ported. "
                + "Refusing to silently treat it as generic S3-compatible storage.");
        }

        var accessKey = Required(configuration, "Storage:AccessKey");
        var secretKey = Required(configuration, "Storage:SecretKey");
        var region = FirstNonEmpty(configuration["Storage:Region"], "us-east-1");
        var forcePathStyle = configuration.GetValue("Storage:ForcePathStyle", true);
        var viaCdn = configuration.GetValue("Storage:ViaCdn", false);

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
            configuration["Storage:KeyPrefix"] ?? string.Empty,
            viaCdn,
            domain);
    }

    public Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        backend.PutAsync(key, content, contentType, cancellationToken);

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

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required for S3 storage.");
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
