using Amazon.S3;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.StoragePool;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

// Locks the R2/storage contract parity between the Python oracle
// (app/services/oss.py, app/services/storage_router.py,
// app/modules/storage_pool/*) and the C# port.
// Python references in comments use the oracle file + line.
public sealed class StorageContractUnitTests
{
    private static IConfiguration BuildConfig(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static IConfiguration S3OfflineConfig(params (string Key, string? Value)[] extra)
    {
        var values = new List<(string Key, string? Value)>
        {
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "contract-bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "AKIDEXAMPLE"),
            ("Storage:SecretKey", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"),
            ("Storage:Region", "us-east-1"),
            ("Storage:KeyPrefix", "storage/"),
            ("Storage:ForcePathStyle", "true"),
        };
        values.AddRange(extra);
        return BuildConfig(values.ToArray());
    }

    [Fact]
    public async Task RouterFailsClosedOnUnknownSourceInsteadOfFallingBack()
    {
        // Python storage_router._resolve_source returns None for unknown/disabled
        // sources and the caller falls back with `backend_for(project) or oss`
        // (storage_router.py:98,139-156). C# deliberately fails closed instead:
        // an unknown csid must never silently read the default bucket.
        var defaultStorage = new ObjectStorage(S3OfflineConfig());
        try
        {
            var router = new ProjectStorageRouter(
                defaultStorage, new StorageBackendFactory(), new ProjectStorageResolverRegistry());
            var project = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["csid"] = ObjectId.GenerateNewId(),
            };

            var error = await Assert.ThrowsAsync<ApiError>(
                async () => await router.ForProjectAsync(project));
            Assert.Equal("StorageSourceUnavailable", error.Error);
            Assert.Equal(6, error.Code);
            Assert.Equal(409, error.Status);
        }
        finally
        {
            defaultStorage.Dispose();
        }
    }

    [Fact]
    public async Task RouterThrowsOnMalformedCsidString()
    {
        var defaultStorage = new ObjectStorage(S3OfflineConfig());
        try
        {
            var router = new ProjectStorageRouter(
                defaultStorage, new StorageBackendFactory(), new ProjectStorageResolverRegistry());
            var project = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["csid"] = "not-an-objectid",
            };

            var error = await Assert.ThrowsAsync<ApiError>(() => router.ForSourceAsync(ObjectId.GenerateNewId()).AsTask());
            Assert.Equal("StorageSourceUnavailable", error.Error);

            // ReadSourceId itself rejects the malformed value the same way.
            Assert.Throws<ApiError>(() => ProjectStorageRouter.ReadSourceId(project));
        }
        finally
        {
            defaultStorage.Dispose();
        }
    }

    [Fact]
    public async Task RouterBorrowsDefaultForMissingEmptyCsidAndLeaseDoesNotOwnIt()
    {
        // Python: csid None -> backend_for returns None -> caller uses global oss.
        // C#: missing/BsonNull/empty csid -> borrowed default lease (ownsBackend
        // false), so disposing the lease must not dispose the shared storage.
        var root = Path.Combine(Path.GetTempPath(), "shoreline-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configuration = BuildConfig(
            ("Storage:Type", "Local"),
            ("Storage:Root", root),
            ("Storage:Domain", "https://files.example.test"));
        using var defaultStorage = new ObjectStorage(configuration);
        var router = new ProjectStorageRouter(
            defaultStorage, new StorageBackendFactory(), new ProjectStorageResolverRegistry());

        try
        {
            var variants = new[]
            {
                new BsonDocument { ["_id"] = ObjectId.GenerateNewId() },
                new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = BsonNull.Value },
                new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = "" },
            };

            foreach (var project in variants)
            {
                var fallback = await router.ForProjectOrNullAsync(project);
                Assert.Null(fallback);

                var lease = await router.ForProjectAsync(project);
                Assert.NotNull(lease.Backend);
                await lease.DisposeAsync();
            }

            // The shared default backend survives borrowed-lease disposal.
            var url = await defaultStorage.SignGetAsync("files/x.png", TimeSpan.FromMinutes(1));
            Assert.StartsWith("https://files.example.test/", url);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task RouterPropagatesResolverFailureInsteadOfDegradingToDefault()
    {
        // DIVERGENCE (documented, fail-closed by design): Python catches resolver
        // exceptions and degrades to the default bucket (storage_router.py:143-151).
        // C# lets the resolver exception propagate so a half-broken pool module
        // cannot silently serve objects from the wrong bucket.
        var defaultStorage = new ObjectStorage(S3OfflineConfig());
        try
        {
            var registry = new ProjectStorageResolverRegistry();
            registry.Register(new ThrowingResolver());
            var router = new ProjectStorageRouter(
                defaultStorage, new StorageBackendFactory(), registry);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => router.ForSourceAsync(ObjectId.GenerateNewId()).AsTask());
        }
        finally
        {
            defaultStorage.Dispose();
        }
    }

    [Fact]
    public async Task SignedUrlExpiresMatchesRequestedValidity()
    {
        // Python _sign_s3_url passes ExpiresIn=int(expires) (oss.py:487-489);
        // C# GetPreSignedURLRequest.Expires must encode the same seconds.
        using var storage = new ObjectStorage(S3OfflineConfig());
        var url = await storage.SignGetAsync("dir/a.txt", TimeSpan.FromMinutes(5));

        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("X-Amz-Expires=300", url);
    }

    [Fact]
    public async Task CdnSignIgnoresValidityAndOmitsPrefixAndSignature()
    {
        // Python CDN branch (oss.py:476-479): plain domain + path + quote(name),
        // no signing, no expiry. Validity must not leak into the URL.
        var configuration = S3OfflineConfig(
            ("Storage:ViaCdn", "true"),
            ("Storage:Domain", "https://cdn.example.com/assets"));
        using var storage = new ObjectStorage(configuration);

        var shortLived = await storage.SignGetAsync("dir/猫 1.txt", TimeSpan.FromMinutes(1));
        var longLived = await storage.SignGetAsync("dir/猫 1.txt", TimeSpan.FromDays(7));

        Assert.Equal(shortLived, longLived);
        Assert.Equal("https://cdn.example.com/assets/dir/%E7%8C%AB%201.txt", shortLived);
        Assert.DoesNotContain("storage/", shortLived);
        Assert.DoesNotContain("X-Amz", shortLived);
        Assert.DoesNotContain("Expires", shortLived);
    }

    [Fact]
    public async Task DefaultRegionIsUsEast1UnlikePythonAuto()
    {
        // DIVERGENCE (documented): Python defaults the SigV4 region to "auto"
        // (oss.py:162) as R2 recommends; C# defaults to "us-east-1"
        // (ObjectStorage.cs ResolveProvider path) when neither Storage:Region
        // nor OSS_S3_REGION is configured. This test locks the current C#
        // behaviour; change it only as a conscious product decision.
        var configuration = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "contract-bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "AKIDEXAMPLE"),
            ("Storage:SecretKey", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"),
            ("Storage:KeyPrefix", "storage/"),
            ("Storage:ForcePathStyle", "true"));
        using var storage = new ObjectStorage(configuration);
        var url = await storage.SignGetAsync("dir/a.txt", TimeSpan.FromMinutes(5));

        Assert.Contains("/us-east-1/s3/aws4_request", Uri.UnescapeDataString(url));
    }

    [Theory]
    [InlineData("storage", "storage/")]
    [InlineData("/storage//", "storage/")]
    [InlineData("  storage/sub  ", "storage/sub/")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("storage/", "storage/")]
    [InlineData("a//b", "a//b/")]
    public void NormalizePrefixMatchesPythonNormalizeSubPath(string input, string expected)
    {
        // Python StoragePoolSource.normalized_sub_path (models.py:84-86,
        // normalize_sub_path:36-43) and C# NormalizePrefix must agree so a
        // source created by either side addresses the same physical keys.
        Assert.Equal(expected, StorageBackendDescriptor.NormalizePrefix(input));
    }

    [Theory]
    [InlineData("12345678", "********")]
    [InlineData("short", "*****")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "AKIA...MPLE")]
    public void MaskedAccessKeyMatchesPythonMasking(string input, string expected)
    {
        // Python to_api masked_access_key_id (models.py:88-93): len<=8 -> all
        // stars, else first 4 + ... + last 4.
        var descriptor = new StorageBackendDescriptor(
            new Uri("https://s3.example.com"), "bucket", "storage/",
            "us-east-1", forcePathStyle: true, accessKey: input, secretKey: "secret");
        Assert.Equal(expected, descriptor.MaskedAccessKey);
        Assert.DoesNotContain(input.Length > 8 ? input : "secret-never-present",
            descriptor.ToString());
    }

    [Fact]
    public void BackendDescriptorToStringOmitsCredentials()
    {
        var descriptor = new StorageBackendDescriptor(
            new Uri("https://s3.example.com"), "bucket", "storage/",
            "us-east-1", forcePathStyle: true,
            accessKey: "AKIAIOSFODNN7EXAMPLE", secretKey: "super-secret-value",
            publicDomain: "https://cdn.example.com");
        var text = descriptor.ToString() + " " + descriptor.Identity;
        Assert.DoesNotContain("super-secret", text);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", text);
        Assert.Contains("bucket", text);
    }

    [Fact]
    public void FactoryRejectsVerifySslFalse()
    {
        // DIVERGENCE (documented, fail-closed): Python honours verify=False via
        // botocore (oss.py:171, backend.py:122); C# refuses verify_ssl=false
        // outright instead of silently downgrading TLS.
        var factory = new StorageBackendFactory();
        var descriptor = new StorageBackendDescriptor(
            new Uri("https://s3.example.com"), "bucket", "storage/",
            "us-east-1", forcePathStyle: true,
            accessKey: "key", secretKey: "secret", verifySsl: false);
        Assert.Throws<InvalidOperationException>(() => factory.Create(descriptor));
    }

    [Fact]
    public void StoragePoolConstantsMatchPythonTransferStates()
    {
        // Python constants.py: TransferStatus 0..7, ACTIVE=(0,1,2,3),
        // RETRYABLE=(5,6); TransferObjectStatus 0..5; direction 0..2;
        // MAX_STALLS=3, DELETE_BATCH_SIZE=1000.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[]
        {
            StoragePoolConstants.Pending, StoragePoolConstants.Copying,
            StoragePoolConstants.Verified, StoragePoolConstants.Switched,
            StoragePoolConstants.Done, StoragePoolConstants.Failed,
            StoragePoolConstants.Aborted, StoragePoolConstants.Cancelled,
        });
        foreach (var active in new[] { 0, 1, 2, 3 })
        {
            Assert.True(StoragePoolConstants.IsActive(active));
        }

        foreach (var inactive in new[] { 4, 5, 6, 7 })
        {
            Assert.False(StoragePoolConstants.IsActive(inactive));
        }

        Assert.True(StoragePoolConstants.IsRetryable(5));
        Assert.True(StoragePoolConstants.IsRetryable(6));
        Assert.False(StoragePoolConstants.IsRetryable(0));
        Assert.False(StoragePoolConstants.IsRetryable(4));
        Assert.False(StoragePoolConstants.IsRetryable(7));

        Assert.Equal(0, StoragePoolConstants.DirectionIn);
        Assert.Equal(1, StoragePoolConstants.DirectionOut);
        Assert.Equal(2, StoragePoolConstants.DirectionMove);
        Assert.Equal(3, StoragePoolConstants.MaxStalls);
        Assert.Equal(1000, StoragePoolConstants.DeleteBatchSize);
        Assert.Equal(".moeflow-storage-probe", StoragePoolConstants.ProbeObjectName);

        Assert.Equal("待执行", StoragePoolConstants.StatusName(0));
        Assert.Equal("已切换", StoragePoolConstants.StatusName(3));
        Assert.Equal("已完成", StoragePoolConstants.StatusName(4));
        Assert.Equal("已取消", StoragePoolConstants.StatusName(7));
        Assert.Equal("", StoragePoolConstants.StatusName(99));
        Assert.Equal("迁入存储池", StoragePoolConstants.DirectionName(0));
        Assert.Equal("回迁到默认存储", StoragePoolConstants.DirectionName(1));
        Assert.Equal("转到其他来源", StoragePoolConstants.DirectionName(2));
        Assert.Equal("", StoragePoolConstants.DirectionName(99));
    }

    private sealed class ThrowingResolver : IProjectStorageResolver
    {
        public Task<StorageBackendLease?> ResolveAsync(
            MongoDB.Bson.ObjectId sourceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}

[Collection("local-services")]
public sealed class StorageContractS3Tests(LocalServices services)
{
    [Fact]
    public async Task MissingObjectMapsToNotFoundAndDeleteStaysIdempotent()
    {
        // Python: head_object -> None, is_exist -> False, download -> NoSuchKey,
        // delete(missing) -> success (S3 delete is idempotent). C# mirrors this
        // with HeadAsync -> null and raw AmazonS3Exception on GetAsync.
        using var storage = new ObjectStorage(services.Configuration);
        var missing = "contract/missing-" + Guid.NewGuid().ToString("N") + ".bin";

        Assert.Null(await storage.HeadAsync(missing));
        Assert.Null(await storage.SizeAsync(missing, CancellationToken.None));

        var getError = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => storage.GetAsync(missing, CancellationToken.None));
        Assert.True(
            getError.StatusCode == System.Net.HttpStatusCode.NotFound
            || getError.ErrorCode is "404" or "NoSuchKey" or "NotFound",
            $"unexpected S3 error: {getError.StatusCode}/{getError.ErrorCode}");

        // Idempotent like S3/Python: deleting a missing key must not throw.
        await storage.DeleteAsync(missing, CancellationToken.None);
        await storage.DeleteAsync(missing, CancellationToken.None);
    }

    [Fact]
    public async Task DownloadDeleteRoundTripKeepsAccountingShape()
    {
        // Python upload/download/delete round trip through the same relative-key
        // space (oss.py:208-235 upload via upload_fileobj for streams,
        // download 257-294, delete 328-347 with empty-batch no-op).
        using var storage = new ObjectStorage(services.Configuration);
        var key = "contract/roundtrip-" + Guid.NewGuid().ToString("N") + ".bin";
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        try
        {
            await using (var input = new MemoryStream(bytes))
            {
                await storage.PutAsync(key, input, "application/octet-stream", CancellationToken.None);
            }

            Assert.True(await storage.SizeAsync(key, CancellationToken.None) == bytes.Length);
            using (var download = await storage.GetAsync(key, CancellationToken.None))
            {
                Assert.Equal(bytes.Length, download.Length);
            }

            await storage.DeleteAsync(key, CancellationToken.None);
            Assert.Null(await storage.HeadAsync(key));
        }
        finally
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
    }

    [Fact]
    public async Task LargeUploadUsesMultipartAndRoundTrips()
    {
        // Python switches to multipart through boto3 upload_fileobj (oss.py:222).
        // Above the 16 MB threshold the C# path must produce identical bytes.
        using var storage = new ObjectStorage(services.Configuration);
        var key = "contract/multipart-" + Guid.NewGuid().ToString("N") + ".bin";
        var bytes = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(bytes);
        try
        {
            await using (var input = new MemoryStream(bytes, writable: false))
            {
                await storage.PutAsync(key, input, "application/octet-stream", CancellationToken.None);
            }

            Assert.Equal(bytes.Length, await storage.SizeAsync(key, CancellationToken.None));
            using (var download = await storage.GetAsync(key, CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await download.Content.CopyToAsync(buffer);
                Assert.Equal(bytes, buffer.ToArray());
            }
        }
        finally
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
    }
}

// Python signs every storage URL with 604800 seconds (app/services/oss.py:464,480),
// so a link copied from a project page must keep working for seven days on both
// backends. Every signed-URL call site now references this single constant.
public sealed class StorageSignedUrlLifetimeTests
{
    [Fact]
    public void SignedUrlDefaultsToSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), ObjectStorage.DefaultSignedUrlValidity);
        Assert.Equal(604800, (int)ObjectStorage.DefaultSignedUrlValidity.TotalSeconds);
    }
}