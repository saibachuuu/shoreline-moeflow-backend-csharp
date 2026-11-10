using Microsoft.Extensions.Configuration;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

public sealed class StorageLegacyEnvironmentTests
{
    [Fact]
    public async Task ReadsPythonOssS3Environment()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["STORAGE_TYPE"] = "OSS",
            ["OSS_BUCKET_STYLE"] = "S3",
            ["OSS_ENDPOINT"] = "https://account.r2.cloudflarestorage.com",
            ["OSS_BUCKET_NAME"] = "moeflow",
            ["OSS_ACCESS_KEY_ID"] = "test-key",
            ["OSS_ACCESS_KEY_SECRET"] = "test-secret",
            ["OSS_S3_REGION"] = "auto",
            ["OSS_KEY_PREFIX"] = "storage/",
            ["OSS_VIA_CDN"] = "True",
            ["STORAGE_DOMAIN"] = "https://cdn.example.test/storage/",
        });

        using var storage = new ObjectStorage(configuration);
        var url = await storage.SignGetAsync("files/a b.png", TimeSpan.FromMinutes(5));

        Assert.Equal("https://cdn.example.test/storage/files/a%20b.png", url);
    }

    [Fact]
    public async Task ReadsPythonLocalStorageEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "shoreline-legacy-local-" + Guid.NewGuid().ToString("N"));
        var configuration = Build(new Dictionary<string, string?>
        {
            ["STORAGE_TYPE"] = "LOCAL_STORAGE",
            ["STORAGE_DOMAIN"] = "https://files.example.test/storage/",
            ["Storage:Root"] = root,
        });

        using var storage = new ObjectStorage(configuration);
        var url = await storage.SignGetAsync("images/example.png", TimeSpan.FromMinutes(5));

        Assert.Equal("https://files.example.test/storage/images/example.png", url);
    }

    [Fact]
    public void RejectsPythonAliyunNativeStyle()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["STORAGE_TYPE"] = "OSS",
            ["OSS_BUCKET_STYLE"] = "ALI",
            ["OSS_ENDPOINT"] = "https://bucket.oss-cn-hangzhou.aliyuncs.com",
            ["OSS_BUCKET_NAME"] = "moeflow",
            ["OSS_ACCESS_KEY_ID"] = "test-key",
            ["OSS_ACCESS_KEY_SECRET"] = "test-secret",
            ["STORAGE_DOMAIN"] = "https://cdn.example.test/storage/",
        });

        Assert.Throws<NotSupportedException>(() => new ObjectStorage(configuration));
    }

    [Fact]
    public void ReportsLegacyNamesWhenRequiredSettingsAreMissing()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["STORAGE_TYPE"] = "OSS",
            ["OSS_BUCKET_STYLE"] = "S3",
            ["OSS_ACCESS_KEY_ID"] = "test-key",
            ["OSS_ACCESS_KEY_SECRET"] = "test-secret",
        });

        var error = Assert.Throws<InvalidOperationException>(() => new ObjectStorage(configuration));
        Assert.Contains("Storage:Bucket (or OSS_BUCKET_NAME)", error.Message);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
