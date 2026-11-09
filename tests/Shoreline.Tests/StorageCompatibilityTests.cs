using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class StorageCompatibilityS3Tests(LocalServices services)
{
    [Fact]
    public async Task PrefixCrudAndCopyUsePhysicalKeyAndExposeRelativeKey()
    {
        var namespacePrefix = "compat/" + Guid.NewGuid().ToString("N") + "/";
        using var raw = new ObjectStorage(S3Config(services, string.Empty));
        using var scoped = new ObjectStorage(S3Config(services, namespacePrefix));
        var source = "crud/source.txt";
        var destination = "copy/destination.txt";
        var bytes = Encoding.UTF8.GetBytes("prefix is applied exactly once");
        var physicalSource = namespacePrefix + source;
        var physicalDestination = namespacePrefix + destination;

        try
        {
            using var input = new MemoryStream(bytes);
            await scoped.PutAsync(source, input, "text/plain", default);

            Assert.True(input.CanRead);
            Assert.Equal(bytes.Length, await raw.SizeAsync(physicalSource, default));
            Assert.Null(await raw.SizeAsync(source, default));

            await scoped.CopyAsync(source, destination, default);
            Assert.Equal(bytes.Length, await raw.SizeAsync(physicalDestination, default));

            var head = await scoped.HeadAsync(source);
            Assert.NotNull(head);
            var info = head!;
            Assert.Equal(source, info.Key);
            Assert.Equal(bytes.Length, info.Size);
            Assert.False(string.IsNullOrEmpty(info.ETag));
            Assert.DoesNotContain("\"", info.ETag!);
            Assert.Equal("text/plain", info.ContentType);
            Assert.Null(await scoped.HeadAsync("missing.txt"));

            await scoped.DeleteAsync(source, default);
            await scoped.DeleteAsync(destination, default);
            Assert.Null(await raw.SizeAsync(physicalSource, default));
            Assert.Null(await raw.SizeAsync(physicalDestination, default));
        }
        finally
        {
            await TryDeleteAsync(raw, physicalSource);
            await TryDeleteAsync(raw, physicalDestination);
        }
    }

    [Fact]
    public async Task ListAndHeadStayInsideTheConfiguredNamespace()
    {
        var namespacePrefix = "compat/" + Guid.NewGuid().ToString("N") + "/";
        using var raw = new ObjectStorage(S3Config(services, string.Empty));
        using var scoped = new ObjectStorage(S3Config(services, namespacePrefix));
        var inside = "list/inside.txt";
        var outside = "other/secret-" + Guid.NewGuid().ToString("N") + ".txt";

        try
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("inside")))
            {
                await scoped.PutAsync(inside, input, "text/plain", default);
            }

            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("outside")))
            {
                await raw.PutAsync(outside, input, "text/plain", default);
            }

            var page = await scoped.ListAsync(string.Empty, null, 100, default);
            Assert.Contains(page.Objects, item => item.Key == inside);
            Assert.DoesNotContain(page.Objects, item => item.Key.Contains("other/", StringComparison.Ordinal));

            Assert.Null(await scoped.HeadAsync(outside));
            var head = await scoped.HeadAsync(inside);
            Assert.NotNull(head);
            Assert.Equal(inside, head!.Key);
        }
        finally
        {
            await TryDeleteAsync(raw, namespacePrefix + inside);
            await TryDeleteAsync(raw, outside);
        }
    }

    [Fact]
    public async Task ListUsesRelativeKeyCursorForPagination()
    {
        var namespacePrefix = "compat/" + Guid.NewGuid().ToString("N") + "/";
        using var scoped = new ObjectStorage(S3Config(services, namespacePrefix));
        var keys = new[] { "page/01.txt", "page/02.txt", "page/03.txt" };

        try
        {
            foreach (var key in keys)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(key));
                await scoped.PutAsync(key, input, "text/plain", default);
            }

            var first = await scoped.ListAsync("page/", null, 2, default);
            Assert.Equal(new[] { "page/01.txt", "page/02.txt" }, first.Objects.Select(item => item.Key));
            Assert.Equal("page/02.txt", first.Next);

            var second = await scoped.ListAsync("page/", first.Next, 2, default);
            Assert.Single(second.Objects);
            Assert.Equal("page/03.txt", second.Objects[0].Key);
            Assert.Null(second.Next);
        }
        finally
        {
            foreach (var key in keys)
            {
                await TryDeleteAsync(scoped, key);
            }
        }
    }

    [Fact]
    public async Task SignedUrlHasSigV4ShapeAndIndependentVerificationRejectsTampering()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        var configuration = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "compat-bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "AKIDEXAMPLE"),
            ("Storage:SecretKey", secret),
            ("Storage:Region", "auto"),
            ("Storage:KeyPrefix", "storage/"),
            ("Storage:ForcePathStyle", "true"));

        var logger = new CapturingLogger<ObjectStorage>();
        using var storage = new ObjectStorage(configuration, logger);
        var url = await storage.SignGetAsync("dir/猫 1.txt", TimeSpan.FromMinutes(5));

        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("/compat-bucket/storage/dir/", url);
        Assert.Contains("/auto/s3/aws4_request", Uri.UnescapeDataString(url));
        Assert.DoesNotContain(secret, url);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("AKIDEXAMPLE", StringComparison.Ordinal));
        Assert.True(SigV4.Verify(url, secret));

        Assert.False(SigV4.Verify(TamperPath(url), secret));
        Assert.False(SigV4.Verify(SetQueryValue(url, "X-Amz-Expires", "999"), secret));
        Assert.False(SigV4.Verify(SetQueryValue(url, "X-Amz-Signature", new string('0', 64)), secret));
    }

    [Fact]
    public async Task CdnSignGetUsesRelativeKeyAndDoesNotIncludePrefixOrCredentials()
    {
        const string secret = "local-cdn-test-secret";
        var configuration = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "compat-bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "AKIDEXAMPLE"),
            ("Storage:SecretKey", secret),
            ("Storage:Region", "us-east-1"),
            ("Storage:KeyPrefix", "storage/"),
            ("Storage:ViaCdn", "true"),
            ("Storage:Domain", "https://cdn.example.com/assets"));

        using var storage = new ObjectStorage(configuration);
        var url = await storage.SignGetAsync("图像/猫 写真.png", TimeSpan.FromMinutes(5));

        Assert.Equal(
            "https://cdn.example.com/assets/%E5%9B%BE%E5%83%8F/%E7%8C%AB%20%E5%86%99%E7%9C%9F.png",
            url);
        Assert.DoesNotContain("storage/", url);
        Assert.DoesNotContain("X-Amz", url);
        Assert.DoesNotContain(secret, url);
    }

    private static IConfiguration S3Config(LocalServices services, string keyPrefix) =>
        new ConfigurationBuilder()
            .AddConfiguration(services.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = "S3",
                ["Storage:KeyPrefix"] = keyPrefix,
                ["Storage:ForcePathStyle"] = "true",
                ["Storage:Region"] = "us-east-1",
            })
            .Build();

    private static async Task TryDeleteAsync(ObjectStorage storage, string key)
    {
        try
        {
            await storage.DeleteAsync(key, default);
        }
        catch
        {
            // Best-effort cleanup of uniquely named test keys.
        }
    }

    private static IConfiguration BuildConfig(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static string TamperPath(string url)
    {
        var builder = new UriBuilder(new Uri(url, UriKind.Absolute))
        {
            Path = "/tampered" + new Uri(url).AbsolutePath,
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string SetQueryValue(string url, string name, string value)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var parts = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var separator = part.IndexOf('=');
                return (Name: separator < 0 ? part : part[..separator], Value: separator < 0 ? string.Empty : part[(separator + 1)..]);
            })
            .Select(part => part.Name.Equals(name, StringComparison.Ordinal)
                ? (Name: part.Name, Value: value)
                : part)
            .Select(part => part.Name + "=" + part.Value)
            .ToArray();

        var builder = new UriBuilder(uri) { Query = string.Join("&", parts) };
        return builder.Uri.AbsoluteUri;
    }
}

public sealed class StorageCompatibilityUnitTests
{
    [Fact]
    public void KnownAwsSigV4VectorMatchesPublishedSignature()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        const string stringToSign = "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63";

        Assert.Equal(
            "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            SigV4.HmacHex(SigV4.SigningKey(secret, "20150830", "us-east-1", "service"), stringToSign));
    }

    [Fact]
    public void UnknownProviderFallsBackToS3WithSanitizedWarningAndNoSecretsInLog()
    {
        const string secret = "unknown-provider-secret";
        var logger = new CapturingLogger<ObjectStorage>();
        var configuration = BuildConfig(
            ("Storage:Type", "mystery-provider"),
            ("Storage:Bucket", "compat-bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "AKIDEXAMPLE"),
            ("Storage:SecretKey", secret));

        using var storage = new ObjectStorage(configuration, logger);

        Assert.Contains(logger.Messages, message => message.Contains("mystery-provider", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("S3-compatible", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("AKIDEXAMPLE", StringComparison.Ordinal));
    }

    [Fact]
    public void AliyunConfigurationFailsFastInsteadOfSilentlyUsingS3()
    {
        var explicitAli = BuildConfig(
            ("Storage:Type", "ALI"),
            ("Storage:Bucket", "bucket"),
            ("Storage:Endpoint", "https://oss-cn-hangzhou.aliyuncs.com"),
            ("Storage:AccessKey", "key"),
            ("Storage:SecretKey", "secret"));

        var aliException = Assert.Throws<NotSupportedException>(() => new ObjectStorage(explicitAli));
        Assert.Contains("not implemented", aliException.Message, StringComparison.OrdinalIgnoreCase);

        var aliyunEndpoint = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "bucket"),
            ("Storage:Endpoint", "https://bucket.oss-cn-hangzhou.aliyuncs.com"),
            ("Storage:AccessKey", "key"),
            ("Storage:SecretKey", "secret"));

        var endpointException = Assert.Throws<InvalidOperationException>(() => new ObjectStorage(aliyunEndpoint));
        Assert.Contains("Aliyun", endpointException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EndpointAndCdnValidationRejectsCredentialsAndNonHttpUris()
    {
        const string password = "do-not-log-this-password";

        var userInfo = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "bucket"),
            ("Storage:Endpoint", "https://user:" + password + "@example.com"),
            ("Storage:AccessKey", "key"),
            ("Storage:SecretKey", "secret"));

        var userInfoException = Assert.Throws<InvalidOperationException>(() => new ObjectStorage(userInfo));
        Assert.DoesNotContain(password, userInfoException.Message, StringComparison.Ordinal);

        var nonHttp = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "bucket"),
            ("Storage:Endpoint", "ftp://example.com"),
            ("Storage:AccessKey", "key"),
            ("Storage:SecretKey", "secret"));

        Assert.Throws<InvalidOperationException>(() => new ObjectStorage(nonHttp));

        var cdnWithoutDomain = BuildConfig(
            ("Storage:Type", "S3"),
            ("Storage:Bucket", "bucket"),
            ("Storage:Endpoint", "https://s3.example.com"),
            ("Storage:AccessKey", "key"),
            ("Storage:SecretKey", "secret"),
            ("Storage:ViaCdn", "true"));

        Assert.Throws<InvalidOperationException>(() => new ObjectStorage(cdnWithoutDomain));
    }

    [Fact]
    public void LocalStorageRequiresAnAbsoluteRoot()
    {
        var missingRoot = BuildConfig(("Storage:Type", "Local"));
        Assert.Throws<InvalidOperationException>(() => new ObjectStorage(missingRoot));

        var relativeRoot = BuildConfig(
            ("Storage:Type", "Local"),
            ("Storage:Root", "relative/storage-root"));
        Assert.Throws<InvalidOperationException>(() => new ObjectStorage(relativeRoot));
    }

    private static IConfiguration BuildConfig(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}

public sealed class LocalStorageCompatibilityTests
{
    [Fact]
    public async Task LocalCrudCopyAtomicReplaceAndStreamOwnership()
    {
        var (storage, root) = CreateLocalStorage();
        try
        {
            var key = "nested/atomic.txt";
            var copy = "nested/copy.txt";

            using var first = new MemoryStream(Encoding.UTF8.GetBytes("one"));
            await storage.PutAsync(key, first, "text/plain", default);
            Assert.True(first.CanRead);
            Assert.Equal(3, await storage.SizeAsync(key, default));

            var head = await storage.HeadAsync(key);
            Assert.NotNull(head);
            Assert.Equal(key, head!.Key);
            Assert.Equal(3, head.Size);
            Assert.Null(head.ETag);
            Assert.Null(head.ContentType);
            Assert.NotNull(head.LastModified);

            using (var download = await storage.GetAsync(key, default))
            {
                Assert.Equal(3, download.Length);
                Assert.Equal("application/octet-stream", download.ContentType);
                Assert.Equal("one", await ReadTextAsync(download.Content));
            }

            using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("two"));
            await storage.PutAsync(key, replacement, "text/plain", default);
            using (var download = await storage.GetAsync(key, default))
            {
                Assert.Equal("two", await ReadTextAsync(download.Content));
            }

            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

            await storage.CopyAsync(key, copy, default);
            using (var download = await storage.GetAsync(copy, default))
            {
                Assert.Equal("two", await ReadTextAsync(download.Content));
            }

            await storage.DeleteAsync(copy, default);
            await storage.DeleteAsync(copy, default);
            Assert.Null(await storage.SizeAsync(copy, default));
            await storage.DeleteAsync(key, default);
            Assert.False(File.Exists(Path.Combine(root, "nested", "atomic.txt")));
        }
        finally
        {
            Cleanup(storage, root);
        }
    }

    [Fact]
    public async Task LocalCancellationCleansUpTemporaryFileAndLeavesInputOpen()
    {
        var (storage, root) = CreateLocalStorage();
        try
        {
            using (var original = new MemoryStream(Encoding.UTF8.GetBytes("old-object")))
            {
                await storage.PutAsync("cancel/target.bin", original, "text/plain", default);
            }

            using var input = new MemoryStream(new byte[1024 * 1024]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.PutAsync(
                "cancel/target.bin",
                input,
                "application/octet-stream",
                cancellation.Token));

            Assert.True(input.CanRead);
            using var download = await storage.GetAsync("cancel/target.bin", default);
            Assert.Equal("old-object", await ReadTextAsync(download.Content));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(storage, root);
        }
    }

    [Fact]
    public async Task LocalRejectsRootedBackslashTraversalAndEmptyKeys()
    {
        var (storage, root) = CreateLocalStorage();
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("x"));

            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("../escape.txt", input, "text/plain", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("a/../../escape.txt", input, "text/plain", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("/rooted.txt", input, "text/plain", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("a\\b.txt", input, "text/plain", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync(string.Empty, input, "text/plain", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.GetAsync("../escape.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.HeadAsync("/rooted.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync("a\\b.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.CopyAsync("ok.txt", "../bad.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync("../", null, 10, default));
        }
        finally
        {
            Cleanup(storage, root);
        }
    }

    [Fact]
    public async Task LocalDoesNotFollowDirectorySymlinks()
    {
        var (storage, root) = CreateLocalStorage();
        var outside = Path.Combine(Path.GetTempPath(), "shoreline-storage-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-root");

        try
        {
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or NotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.GetAsync("link/secret.txt", default));

            var page = await storage.ListAsync(string.Empty, null, 100, default);
            Assert.DoesNotContain(page.Objects, item => item.Key.StartsWith("link/", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(storage, root);
            try
            {
                Directory.Delete(outside, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task LocalListingIsSortedPaginatedAndBounded()
    {
        var (storage, root) = CreateLocalStorage();
        var keys = new[] { "p/03.txt", "p/01.txt", "p/02.txt" };
        try
        {
            foreach (var key in keys)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(key));
                await storage.PutAsync(key, input, "text/plain", default);
            }

            var first = await storage.ListAsync("p/", null, 2, default);
            Assert.Equal(new[] { "p/01.txt", "p/02.txt" }, first.Objects.Select(item => item.Key));
            Assert.Equal("p/02.txt", first.Next);

            var second = await storage.ListAsync("p/", first.Next, 2, default);
            Assert.Single(second.Objects);
            Assert.Equal("p/03.txt", second.Objects[0].Key);
            Assert.Null(second.Next);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ListAsync(string.Empty, null, 0, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ListAsync(string.Empty, null, 1001, default));
        }
        finally
        {
            Cleanup(storage, root);
        }
    }

    [Fact]
    public async Task LocalSigningUsesDomainAndRelativeKeyWithoutReadingTheFile()
    {
        var (storage, root) = CreateLocalStorage(
            domain: "https://files.example.test",
            keyPrefix: "storage/");

        try
        {
            var url = await storage.SignGetAsync("图像/猫 1.txt", TimeSpan.FromMinutes(5));
            Assert.Equal(
                "https://files.example.test/%E5%9B%BE%E5%83%8F/%E7%8C%AB%201.txt",
                url);
            Assert.DoesNotContain("storage/", url);
            Assert.DoesNotContain("X-Amz", url);

            using var input = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            await storage.PutAsync("x.txt", input, "text/plain", default);
            Assert.True(File.Exists(Path.Combine(root, "x.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "storage")));
        }
        finally
        {
            Cleanup(storage, root);
        }
    }

    private static (ObjectStorage Storage, string Root) CreateLocalStorage(
        string? domain = "https://files.example.test",
        string keyPrefix = "")
    {
        var root = Path.Combine(Path.GetTempPath(), "shoreline-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var configuration = BuildConfig(
            ("Storage:Type", "Local"),
            ("Storage:Root", root),
            ("Storage:Domain", domain),
            ("Storage:KeyPrefix", keyPrefix));

        return (new ObjectStorage(configuration), root);
    }

    private static void Cleanup(ObjectStorage storage, string root)
    {
        storage.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a uniquely named test root.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a uniquely named test root.
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static IConfiguration BuildConfig(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

internal static class SigV4
{
    public static bool Verify(string url, string secretKey)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var pairs = ParseQuery(uri.Query);
        var signature = Get(pairs, "X-Amz-Signature");
        var algorithm = Get(pairs, "X-Amz-Algorithm");
        var credential = Get(pairs, "X-Amz-Credential");
        var amzDate = Get(pairs, "X-Amz-Date");
        var signedHeadersValue = Get(pairs, "X-Amz-SignedHeaders");

        if (signature is null
            || algorithm != "AWS4-HMAC-SHA256"
            || credential is null
            || amzDate is null
            || signedHeadersValue is null)
        {
            return false;
        }

        credential = Uri.UnescapeDataString(credential);
        signedHeadersValue = Uri.UnescapeDataString(signedHeadersValue);

        var credentialParts = credential.Split('/');
        if (credentialParts.Length != 5 || credentialParts[4] != "aws4_request")
        {
            return false;
        }

        var signedHeaders = signedHeadersValue
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (signedHeaders.Length == 0)
        {
            return false;
        }

        var canonicalQuery = string.Join(
            "&",
            pairs
                .Where(pair => !pair.Name.Equals("X-Amz-Signature", StringComparison.Ordinal))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => pair.Name + "=" + pair.Value));

        var canonicalUri = uri.AbsolutePath;
        if (canonicalUri.Length == 0)
        {
            canonicalUri = "/";
        }

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var payloadHash = Get(pairs, "X-Amz-Content-Sha256") ?? "UNSIGNED-PAYLOAD";
        var canonicalHeaders = new StringBuilder();
        foreach (var name in signedHeaders)
        {
            var value = name switch
            {
                "host" => host,
                "x-amz-content-sha256" => payloadHash,
                "x-amz-date" => amzDate,
                "x-amz-security-token" => Get(pairs, "X-Amz-Security-Token"),
                _ => null,
            };

            if (value is null)
            {
                return false;
            }

            canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');
        }

        signedHeadersValue = string.Join(";", signedHeaders);
        var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeadersValue}\n{payloadHash}";
        var scope = $"{credentialParts[1]}/{credentialParts[2]}/{credentialParts[3]}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = SigningKey(secretKey, credentialParts[1], credentialParts[2], credentialParts[3]);
        var expected = HmacHex(signingKey, stringToSign);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string HmacHex(byte[] key, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static byte[] SigningKey(string secretKey, string date, string region, string service)
    {
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        return Hmac(key, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static List<(string Name, string Value)> ParseQuery(string query)
    {
        var result = new List<(string Name, string Value)>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            result.Add(separator < 0
                ? (part, string.Empty)
                : (part[..separator], part[(separator + 1)..]));
        }

        return result;
    }

    private static string? Get(List<(string Name, string Value)> pairs, string name)
    {
        foreach (var pair in pairs)
        {
            if (pair.Name.Equals(name, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
