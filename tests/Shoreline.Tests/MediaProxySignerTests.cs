using Microsoft.Extensions.Configuration;
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
    private static MediaProxySigner Signer(string? secret = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Secret"] = secret ?? SecurityTests.Secret,
        }).Build(), TimeProvider.System);

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
