using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

/// <summary>
/// The reverse-proxy switch hands out <c>/api/v1/media/{source}/{key}</c> links
/// instead of bucket or CDN URLs. Those links are pasted into chat and issue
/// trackers, so they carry no session: the object key is authenticated by an
/// HMAC over (source, key, expiry). These tests pin the properties the feature
/// relies on.
/// </summary>
public sealed class MediaProxySignerTests
{
    private sealed class TogglePolicy(bool enabled) : IMediaProxyPolicy
    {
        public bool Enabled { get; set; } = enabled;
        public ValueTask<bool> UseNeverExpireUrlsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Enabled);
    }

    private static MediaProxySigner Signer(string? secret = null, IMediaProxyPolicy? policy = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Secret"] = secret ?? SecurityTests.Secret,
        }).Build(), TimeProvider.System, policy);

    [Fact]
    public void BuildUrl_KeepsTheSiteOriginAndDropsBucketDetails()
    {
        var url = Signer().BuildUrl("default", "files/a b.png", TimeSpan.FromMinutes(5));

        Assert.StartsWith("/api/v1/media/default/files/a%20b.png?e=", url);
        Assert.DoesNotContain("http://", url);
        Assert.DoesNotContain("aws", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_AcceptsTheExactSignatureAndRejectsAnyTampering()
    {
        var signer = Signer();
        var url = signer.BuildUrl("default", "files/one.png", TimeSpan.FromMinutes(5));
        var (expires, signature) = Parse(url);

        Assert.True(signer.Verify("default", "files/one.png", expires, signature, null));
        // A different object under the same signature must not resolve.
        Assert.False(signer.Verify("default", "files/two.png", expires, signature, null));
        // Neither must the same key under a different source token.
        Assert.False(signer.Verify("507f1f77bcf86cd799439011", "files/one.png", expires, signature, null));
        Assert.False(signer.Verify("default", "files/one.png", expires + 60, signature, null));
        Assert.False(signer.Verify("default", "files/one.png", expires, null, null));
    }

    [Fact]
    public void BuildUrl_RefusesNonPositiveValidity()
    {
        // An already-expired link would be useless anyway; rejecting it here keeps
        // the invariant that every minted URL verified at least once.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Signer().BuildUrl("default", "files/old.png", TimeSpan.FromMinutes(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Signer().BuildUrl("default", "files/old.png", TimeSpan.Zero));
    }

    [Fact]
    public void Verify_RejectsALinkWhoseExpiryWasPushedBack()
    {
        var signer = Signer();
        var url = signer.BuildUrl("default", "files/one.png", TimeSpan.FromMinutes(5));
        var (expires, signature) = Parse(url);

        // Moving the expiry forward keeps the old signature valid only up to the
        // signed instant, so an expired request cannot be revived by editing `e`.
        Assert.False(signer.Verify("default", "files/one.png", expires - 1, signature, null));
    }

    [Fact]
    public void ContentDisposition_IsLimitedToAttachment()
    {
        var signer = Signer();
        var attachment = signer.BuildUrl("default", "out/report.zip", TimeSpan.FromMinutes(5), "attachment");
        var (expires, signature) = Parse(attachment, out var disposition);
        Assert.Equal("attachment", disposition);
        Assert.True(signer.Verify("default", "out/report.zip", expires, signature, disposition));

        // Anything else is normalised away, so a link cannot smuggle a header.
        Assert.Equal("", MediaProxySigner.NormalizeDisposition("inline; filename=\"x\""));
        Assert.Equal("", MediaProxySigner.NormalizeDisposition(null));
    }

    [Fact]
    public void FormatContentDisposition_EncodesNonAsciiAndStripsControlCharacters()
    {
        var exportName = "Blue Archive - 眉毛 私だけの先生…EX - 中文（繁体）.zip";
        var formatted = MediaProxyEndpoints.FormatContentDisposition(
            "attachment", $"outputs/6afe8758737906ff97b59e9f/{exportName}");

        // Kestrel rejects any non-ASCII or control character in response headers.
        Assert.All(formatted, c => Assert.InRange(c, '\x20', '\x7E'));
        Assert.True(ContentDispositionHeaderValue.TryParse(formatted, out var parsed));
        Assert.Equal("attachment", parsed!.DispositionType.Value);
        Assert.Equal(exportName, parsed.FileNameStar.Value);

        var ascii = MediaProxyEndpoints.FormatContentDisposition("attachment", "outputs/1/report.zip");
        Assert.All(ascii, c => Assert.InRange(c, '\x20', '\x7E'));
        Assert.True(ContentDispositionHeaderValue.TryParse(ascii, out var parsedAscii));
        Assert.Equal("attachment", parsedAscii!.DispositionType.Value);
        Assert.Equal("report.zip", parsedAscii.FileName.Value);
        Assert.Equal("report.zip", parsedAscii.FileNameStar.Value);

        var withControls = MediaProxyEndpoints.FormatContentDisposition(
            "attachment", "outputs/1/bad\r\nname\t.zip");
        Assert.All(withControls, c => Assert.InRange(c, '\x20', '\x7E'));
    }

    [Fact]
    public void BuildUrl_AndVerify_RoundTripNonAsciiExportKey()
    {
        var signer = Signer();
        var key = "outputs/6afe8758737906ff97b59e9f/Blue Archive - 眉毛 私だけの先生…EX - 中文（繁体）.zip";
        var url = signer.BuildUrl(MediaProxySigner.DefaultSourceToken, key, TimeSpan.FromMinutes(5), "attachment");
        var (expires, signature) = Parse(url, out var disposition);

        Assert.Equal("attachment", disposition);
        Assert.True(signer.Verify(MediaProxySigner.DefaultSourceToken, key, expires, signature, disposition));
    }

    [Fact]
    public void Signature_IsBoundToTheSecret()
    {
        var first = Signer();
        var second = Signer("another-test-secret-0123456789012345");
        var url = first.BuildUrl("default", "files/one.png", TimeSpan.FromMinutes(5));
        var (expires, signature) = Parse(url);

        Assert.False(second.Verify("default", "files/one.png", expires, signature, null));
    }

    [Fact]
    public void Signer_RequiresAStrongSecret()
    {
        Assert.Throws<InvalidOperationException>(() => Signer("too-short"));
    }

    [Fact]
    public async Task Factory_WithProxyTokenSignsSelfHostedUrlsOnlyWhenEnabled()
    {
        var signer = Signer();
        var descriptor = new StorageBackendDescriptor(
            new Uri("https://s3.example.com"), "bucket", "storage/", "us-east-1", true,
            "AKIDEXAMPLE", "secret-example-key", "https://cdn.example.com/storage/", true);

        // Without the switch the descriptor keeps handing out CDN links.
        using var plain = new StorageBackendFactory(signer).CreateLease(descriptor);
        var cdn = await plain.Backend.SignGetAsync("files/one.png", TimeSpan.FromMinutes(5));
        Assert.StartsWith("https://cdn.example.com/storage/", cdn);

        // With it, the same backend signs through this site instead.
        var proxied = new StorageBackendDescriptor(
            descriptor.Endpoint, descriptor.Bucket, descriptor.Prefix, descriptor.Region,
            descriptor.ForcePathStyle, descriptor.AccessKey, descriptor.SecretKey,
            descriptor.PublicDomain, descriptor.VerifySsl, "507f1f77bcf86cd799439011");
        using var lease = new StorageBackendFactory(signer).CreateLease(proxied);
        var url = await lease.Backend.SignGetAsync("files/one.png", TimeSpan.FromMinutes(5));
        Assert.StartsWith("/api/v1/media/507f1f77bcf86cd799439011/files/one.png?e=", url);
    }

    [Fact]
    public void BuildUrl_NeverExpireOmitsExpiryAndSignsDeterministically()
    {
        var signer = Signer();
        var first = signer.BuildUrl("default", "files/one.png", TimeSpan.FromMinutes(5), "attachment", neverExpire: true);
        var second = signer.BuildUrl("default", "files/one.png", TimeSpan.FromMinutes(30), "attachment", neverExpire: true);

        Assert.Equal(first, second);
        Assert.DoesNotContain("e=", first);
        Assert.StartsWith("/api/v1/media/default/files/one.png?s=", first);

        var (expires, signature) = Parse(first, out var disposition);
        Assert.Equal(0, expires);
        Assert.Equal("attachment", disposition);
        Assert.True(signer.VerifyNever("default", "files/one.png", signature, disposition));
        Assert.False(signer.VerifyNever("default", "files/two.png", signature, disposition));
        // Expiring and non-expiring signatures must not cross-verify.
        Assert.False(signer.Verify("default", "files/one.png", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            signature, disposition));
    }

    [Fact]
    public async Task PolicyToggle_ControlsBuildUrlAsyncAndVerifyAsync()
    {
        var policy = new TogglePolicy(false);
        var signer = Signer(policy: policy);

        var expiringUrl = await signer.BuildUrlAsync("default", "files/one.png", TimeSpan.FromMinutes(5));
        var (expiringAt, expiringSig) = Parse(expiringUrl);
        Assert.True(expiringAt > 0);
        Assert.True(await signer.VerifyAsync("default", "files/one.png", expiringAt, expiringSig, null));

        // Enabling non-expiring mode mints stable URLs while honoring unexpired legacy links.
        policy.Enabled = true;
        var neverUrl = await signer.BuildUrlAsync("default", "files/one.png", TimeSpan.FromMinutes(5));
        var (noExpiry, neverSig) = Parse(neverUrl);
        Assert.Equal(0, noExpiry);
        Assert.True(await signer.VerifyAsync("default", "files/one.png", null, neverSig, null));
        Assert.True(await signer.VerifyAsync("default", "files/one.png", expiringAt, expiringSig, null));

        // Turning the policy back off immediately invalidates non-expiring links.
        policy.Enabled = false;
        Assert.False(await signer.VerifyAsync("default", "files/one.png", null, neverSig, null));
    }

    private static (long Expires, string Signature) Parse(string url) => Parse(url, out _);

    private static (long Expires, string Signature) Parse(string url, out string? disposition)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        long expires = 0;
        string? signature = null;
        disposition = null;
        foreach (var part in query.Split('&'))
        {
            var split = part.Split('=', 2);
            switch (split[0])
            {
                case "e": expires = long.Parse(split[1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "s": signature = split[1]; break;
                case "cd": disposition = split[1]; break;
            }
        }

        return (expires, signature!);
    }
}
