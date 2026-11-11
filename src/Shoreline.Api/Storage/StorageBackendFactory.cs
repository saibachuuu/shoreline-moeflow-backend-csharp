using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Storage;

/// <summary>
/// Immutable description of one S3-compatible backend. It deliberately is not
/// serializable and its ToString omits credentials.
/// </summary>
public sealed class StorageBackendDescriptor
{
    public StorageBackendDescriptor(
        Uri endpoint,
        string bucket,
        string prefix,
        string region,
        bool forcePathStyle,
        string accessKey,
        string secretKey,
        string publicDomain = "",
        bool verifySsl = true)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Storage endpoint must be an absolute http(s) URI.", nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Storage endpoint must not contain user information.", nameof(endpoint));
        if (!string.IsNullOrWhiteSpace(endpoint.Query) || !string.IsNullOrWhiteSpace(endpoint.Fragment))
            throw new ArgumentException("Storage endpoint must not contain a query or fragment.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Storage bucket is required.", nameof(bucket));
        if (string.IsNullOrWhiteSpace(region)) throw new ArgumentException("Storage region is required.", nameof(region));
        if (string.IsNullOrWhiteSpace(accessKey)) throw new ArgumentException("Storage access key is required.", nameof(accessKey));
        if (string.IsNullOrWhiteSpace(secretKey)) throw new ArgumentException("Storage secret key is required.", nameof(secretKey));

        Endpoint = endpoint;
        Bucket = bucket;
        Prefix = NormalizePrefix(prefix);
        Region = region;
        ForcePathStyle = forcePathStyle;
        AccessKey = accessKey;
        SecretKey = secretKey;
        PublicDomain = (publicDomain ?? "").Trim();
        VerifySsl = verifySsl;
    }

    public Uri Endpoint { get; }
    public string Bucket { get; }
    public string Prefix { get; }
    public string Region { get; }
    public bool ForcePathStyle { get; }
    public string AccessKey { get; }
    public string SecretKey { get; }
    public string PublicDomain { get; }
    public bool VerifySsl { get; }

    public string MaskedAccessKey
    {
        get
        {
            var value = AccessKey ?? "";
            if (value.Length <= 8) return new string('*', value.Length);
            return value[..4] + "..." + value[^4..];
        }
    }

    public string Identity => Endpoint.AbsoluteUri.TrimEnd('/') + "/" + Bucket + "/" + Prefix;

    public override string ToString() =>
        Endpoint.AbsoluteUri.TrimEnd('/') + "/" + Bucket + "/" + Prefix;

    public static string NormalizePrefix(string? value)
    {
        // Matches Python normalize_sub_path (storage_pool/models.py): leading
        // and trailing slashes are all stripped, then exactly one trailing
        // slash is added. Interior runs are preserved on both sides.
        var raw = (value ?? "").Trim().Trim('/');
        if (raw.Length == 0) return "";
        return raw + "/";
    }
}

/// <summary>
/// Creates short-lived backend instances from immutable descriptors. The
/// factory keeps no global client cache; each lease owns exactly one client.
/// </summary>
public sealed class StorageBackendFactory
{
    public IStorageBackend Create(StorageBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.VerifySsl)
            throw new InvalidOperationException("Storage verify_ssl=false is not supported by this backend.");

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Storage:Type"] = "S3",
            ["Storage:Endpoint"] = descriptor.Endpoint.AbsoluteUri.TrimEnd('/'),
            ["Storage:Bucket"] = descriptor.Bucket,
            ["Storage:KeyPrefix"] = descriptor.Prefix,
            ["Storage:Region"] = descriptor.Region,
            ["Storage:ForcePathStyle"] = descriptor.ForcePathStyle ? "true" : "false",
            ["Storage:AccessKey"] = descriptor.AccessKey,
            ["Storage:SecretKey"] = descriptor.SecretKey,
            ["Storage:ViaCdn"] = string.IsNullOrWhiteSpace(descriptor.PublicDomain) ? "false" : "true",
            ["Storage:Domain"] = descriptor.PublicDomain,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ObjectStorageBackend(new ObjectStorage(configuration), ownsStorage: true);
    }

    public StorageBackendLease CreateLease(StorageBackendDescriptor descriptor) =>
        new(Create(descriptor), ownsBackend: true);

    public StorageBackendLease BorrowDefault(ObjectStorage storage) =>
        new(new ObjectStorageBackend(storage, ownsStorage: false), ownsBackend: false);

    /// <summary>
    /// Performs a server-side copy when both descriptors point at the same S3
    /// endpoint. The copy source always uses the source bucket and the source
    /// prefix; the destination bucket/key cannot act as a decoy.
    /// </summary>
    public async Task<bool> TryCopyServerSideAsync(
        StorageBackendDescriptor source,
        StorageBackendDescriptor destination,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.VerifySsl || !destination.VerifySsl) return false;
        if (!SameEndpoint(source.Endpoint, destination.Endpoint)) return false;
        var sourcePhysical = source.Prefix + sourceKey;
        var destinationPhysical = destination.Prefix + destinationKey;
        if (source.Bucket == destination.Bucket && sourcePhysical == destinationPhysical) return false;

        using var client = CreateClient(source);
        try
        {
            var response = await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = source.Bucket,
                SourceKey = sourcePhysical,
                DestinationBucket = destination.Bucket,
                DestinationKey = destinationPhysical,
            }, cancellationToken).ConfigureAwait(false);
            return response.HttpStatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A server-side copy is an optimization. Any rejection (missing
            // source, unsupported header, provider quirk) falls back to the
            // bounded streaming path in the transfer engine.
            return false;
        }
    }

    private static bool SameEndpoint(Uri left, Uri right) =>
        string.Equals(left.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            right.GetLeftPart(UriPartial.Authority).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static AmazonS3Client CreateClient(StorageBackendDescriptor descriptor)
    {
        var configuration = new AmazonS3Config
        {
            ServiceURL = descriptor.Endpoint.AbsoluteUri.TrimEnd('/'),
            ForcePathStyle = descriptor.ForcePathStyle,
            AuthenticationRegion = descriptor.Region,
            MaxErrorRetry = 2,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(descriptor.AccessKey, descriptor.SecretKey), configuration);
    }
}

internal sealed class ObjectStorageBackend : IStorageBackend
{
    private readonly ObjectStorage storage;
    private readonly bool ownsStorage;

    public ObjectStorageBackend(ObjectStorage storage, bool ownsStorage)
    {
        this.storage = storage;
        this.ownsStorage = ownsStorage;
    }

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        storage.PutAsync(key, content, contentType, cancellationToken);

    public Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken) =>
        storage.GetAsync(key, cancellationToken);

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken) =>
        storage.HeadAsync(key, cancellationToken);

    public Task<long?> SizeAsync(string key, CancellationToken cancellationToken) =>
        storage.SizeAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        storage.DeleteAsync(key, cancellationToken);

    public Task CopyAsync(string source, string destination, CancellationToken cancellationToken) =>
        storage.CopyAsync(source, destination, cancellationToken);

    public Task<string> SignGetAsync(string key, TimeSpan validity) =>
        storage.SignGetAsync(key, validity);

    public Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition) =>
        storage.SignGetAsync(key, validity, contentDisposition);

    public Task<ObjectPage> ListAsync(string prefix, string? startAfter, int maxKeys,
        CancellationToken cancellationToken) =>
        storage.ListAsync(prefix, startAfter, maxKeys, cancellationToken);

    public void Dispose()
    {
        if (ownsStorage) storage.Dispose();
    }
}

/// <summary>
/// A backend plus ownership. Borrowed leases (the site default backend) must not
/// dispose the singleton ObjectStorage.
/// </summary>
public sealed class StorageBackendLease : IAsyncDisposable, IDisposable
{
    private IStorageBackend? backend;
    private readonly bool ownsBackend;

    public StorageBackendLease(IStorageBackend backend, bool ownsBackend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.ownsBackend = ownsBackend;
    }

    public IStorageBackend Backend =>
        backend ?? throw new ObjectDisposedException(nameof(StorageBackendLease));

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref backend, null);
        if (ownsBackend && value is not null) value.Dispose();
    }
}

public static class ProjectStorageServiceCollectionExtensions
{
    /// <summary>Registers only the module-neutral router/factory foundation.</summary>
    public static IServiceCollection AddProjectStorageCore(this IServiceCollection services)
    {
        services.AddSingleton<StorageBackendFactory>();
        services.AddSingleton<ProjectStorageResolverRegistry>();
        services.AddScoped<ProjectStorageRouter>();
        return services;
    }
}
