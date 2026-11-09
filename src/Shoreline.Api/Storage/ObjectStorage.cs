using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Shoreline.Api.Storage;

public sealed class ObjectStorage : IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;

    public ObjectStorage(IConfiguration configuration)
    {
        bucket = configuration["Storage:Bucket"] ?? throw new InvalidOperationException("Storage:Bucket is required");
        var endpoint = configuration["Storage:Endpoint"] ?? throw new InvalidOperationException("Storage:Endpoint is required");
        var access = configuration["Storage:AccessKey"] ?? throw new InvalidOperationException("Storage:AccessKey is required");
        var secret = configuration["Storage:SecretKey"] ?? throw new InvalidOperationException("Storage:SecretKey is required");
        client = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = configuration.GetValue("Storage:ForcePathStyle", true),
            AuthenticationRegion = configuration["Storage:Region"] ?? "us-east-1",
            MaxErrorRetry = 2
        });
    }

    // SDK consumes the stream directly; callers retain ownership of upload streams.
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken ct) =>
        client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = content, ContentType = contentType,
            AutoCloseStream = false
        }, ct);

    public async Task<ObjectDownload> GetAsync(string key, CancellationToken ct)
    {
        var response = await client.GetObjectAsync(bucket, key, ct);
        return new ObjectDownload(response);
    }

    public async Task<long?> SizeAsync(string key, CancellationToken ct)
    {
        try { return (await client.GetObjectMetadataAsync(bucket, key, ct)).ContentLength; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task DeleteAsync(string key, CancellationToken ct) => client.DeleteObjectAsync(bucket, key, ct);

    public Task CopyAsync(string source, string destination, CancellationToken ct) => client.CopyObjectAsync(new CopyObjectRequest
    {
        SourceBucket = bucket, DestinationBucket = bucket, SourceKey = source, DestinationKey = destination
    }, ct);

    public async Task<string> SignGetAsync(string key, TimeSpan validity) => await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(validity)
    });

    public void Dispose() => client.Dispose();
}

public sealed class ObjectDownload(GetObjectResponse response) : IDisposable
{
    public Stream Content => response.ResponseStream;
    public long Length => response.ContentLength;
    public string ContentType => response.Headers.ContentType;
    public void Dispose() => response.Dispose();
}
