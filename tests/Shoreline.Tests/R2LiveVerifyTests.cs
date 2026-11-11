using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shoreline.Api.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Shoreline.Tests;

/// <summary>
/// Opt-in live check against the real Cloudflare R2 bucket. Runs only when
/// OSS_ENDPOINT/OSS_BUCKET_NAME/OSS_ACCESS_KEY_ID/OSS_ACCESS_KEY_SECRET are set.
/// Every object it writes lives under the caller-supplied OSS_KEY_PREFIX and is
/// appended to the manifest file so it can be deleted afterwards.
/// </summary>
public sealed class R2LiveVerifyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_R2_PutHeadGetSignAndList()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);

        var endpoint = Env("OSS_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            output.WriteLine("SKIP: OSS_ENDPOINT not set");
            return;
        }

        var bucket = Env("OSS_BUCKET_NAME") ?? throw new InvalidOperationException("OSS_BUCKET_NAME missing");
        var prefix = Env("OSS_KEY_PREFIX") ?? "_csharp-verify/";
        var manifestPath = Env("R2_MANIFEST") ?? Path.Combine(Path.GetTempPath(), "csharp-r2-manifest.txt");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = Env("OSS_BUCKET_STYLE") ?? "S3",
                ["Storage:Bucket"] = bucket,
                ["Storage:Endpoint"] = endpoint,
                ["Storage:AccessKey"] = Env("OSS_ACCESS_KEY_ID"),
                ["Storage:SecretKey"] = Env("OSS_ACCESS_KEY_SECRET"),
                ["Storage:Region"] = Env("OSS_S3_REGION") ?? "auto",
                ["Storage:ForcePathStyle"] = "true",
                ["Storage:KeyPrefix"] = prefix,
                ["Storage:ViaCdn"] = "false",
            })
            .Build();

        var storage = new ObjectStorage(configuration, NullLogger<ObjectStorage>.Instance);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var smallKey = $"smoke/{stamp}/small-{Guid.NewGuid():N}.bin";
        var largeKey = $"smoke/{stamp}/large-{Guid.NewGuid():N}.bin";

        var small = RandomNumberGenerator.GetBytes(1024);
        var large = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);

        var lines = new List<string>
        {
            $"# bucket={bucket}",
            $"# endpoint={endpoint}",
            $"# prefix={prefix}",
            $"# when={DateTime.UtcNow:O}",
            $"# format: bucket<TAB>key<TAB>bytes<TAB>sha256",
        };

        async Task<string> PutAndVerifyAsync(string key, byte[] payload, string contentType)
        {
            using (var input = new MemoryStream(payload, writable: false))
            {
                await storage.PutAsync(key, input, contentType, CancellationToken.None);
            }

            var head = await storage.HeadAsync(key, CancellationToken.None);
            Assert.NotNull(head);
            Assert.Equal(payload.Length, head!.Size);

            using var download = await storage.GetAsync(key, CancellationToken.None);
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, CancellationToken.None);
            var roundTripped = buffer.ToArray();
            Assert.Equal(payload.Length, roundTripped.Length);
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(roundTripped));

            var signed = await storage.SignGetAsync(key, ObjectStorage.DefaultSignedUrlValidity);
            Assert.False(string.IsNullOrWhiteSpace(signed));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await http.GetAsync(signed, CancellationToken.None);
            Assert.True(response.IsSuccessStatusCode, $"signed GET failed: {(int)response.StatusCode}");
            var viaSignedUrl = await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(viaSignedUrl));

            var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            lines.Add($"{bucket}\t{key}\t{payload.Length}\t{sha}");
            output.WriteLine($"OK {key} {payload.Length}B sha256={sha}");
            return key;
        }

        await PutAndVerifyAsync(smallKey, small, "application/octet-stream");
        await PutAndVerifyAsync(largeKey, large, "application/octet-stream");

        var page = await storage.ListAsync("smoke/", null, 100, CancellationToken.None);
        var keys = page.Objects.Select(i => i.Key).ToArray();
        output.WriteLine($"list(smoke/) -> {keys.Length} objects");
        Assert.Contains(keys, k => k.Contains("smoke/"));

        // Full inventory under the verify prefix, so the manifest is the exact
        // delete list even when an earlier attempt left objects behind.
        var inventory = await storage.ListAsync(string.Empty, null, 1000, CancellationToken.None);
        lines.Add("# inventory under prefix");
        foreach (var item in inventory.Objects)
        {
            lines.Add($"{bucket}\t{item.Key}\t{item.Size}\t-");
        }

        output.WriteLine($"inventory({prefix}) -> {inventory.Objects.Count} objects");

        await File.AppendAllLinesAsync(manifestPath, lines, CancellationToken.None);
        output.WriteLine($"manifest appended: {manifestPath}");
    }
}
