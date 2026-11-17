using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Shoreline.Api.Storage;

/// <summary>
/// Signs self-hosted media URLs.
///
/// A source with the reverse-proxy switch on hands out
/// <c>/api/v1/media/{source}/{key}</c> instead of a bucket/CDN URL, so the
/// visitor always sees the site's own domain and the backend streams the object
/// from wherever it really lives. Such a link has to stay shareable without a
/// session (a poster link pasted into chat must keep working), so the object key
/// is authenticated by an HMAC rather than by a login.
///
/// It is deliberately a singleton: the key is derived once from
/// <c>Auth:Secret</c>, and rotating that secret invalidates every outstanding
/// link, which is the same trade-off the signed-URL path already makes.
/// </summary>
public sealed class MediaProxySigner
{
    /// <summary>Path token used for the site's own default storage.</summary>
    public const string DefaultSourceToken = "default";

    private const string RoutePrefix = "/v1/media/";

    private readonly byte[] signingKey;
    private readonly TimeProvider clock;
    private readonly string basePath;

    public MediaProxySigner(IConfiguration configuration, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var secret = configuration["Auth:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("Auth:Secret must contain at least 32 characters");
        // Domain separation: the derivation never reuses the raw secret, so a
        // leaked media signature cannot be replayed against sessions or tokens.
        signingKey = SHA256.HashData(
            [.. Encoding.UTF8.GetBytes(secret), .. Encoding.UTF8.GetBytes("|media-proxy")]);
        this.clock = clock;
        // The frontend talks to the API under /api (nginx strips it before the
        // backend sees the request), so a self-hosted link has to start there.
        var configured = configuration["Storage:MediaProxyBase"];
        basePath = string.IsNullOrWhiteSpace(configured) ? "/api" : configured.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Builds the public link for one object. <paramref name="sourceToken"/> is
    /// either <see cref="DefaultSourceToken"/> or a storage-source ObjectId.
    /// </summary>
    public string BuildUrl(string sourceToken, string key, TimeSpan validity, string? contentDisposition = null)
    {
        StorageKey.ValidateObjectKey(key);
        if (validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(validity), "Media URL validity must be positive.");
        var expires = clock.GetUtcNow().Add(validity).ToUnixTimeSeconds();
        var disposition = NormalizeDisposition(contentDisposition);
        var signature = Sign(sourceToken, key, expires, disposition);
        var url = basePath + RoutePrefix + Uri.EscapeDataString(sourceToken) + "/"
            + StorageKey.EncodePath(key)
            + "?e=" + expires.ToString(CultureInfo.InvariantCulture)
            + "&s=" + signature;
        if (disposition.Length > 0)
            url += "&cd=" + disposition;
        return url;
    }

    /// <summary>
    /// Recomputes the signature for an inbound request. A false result means the
    /// link was forged, key-swapped, or already expired.
    /// </summary>
    public bool Verify(string sourceToken, string key, long expires, string? signature, string? contentDisposition)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (sourceToken.Length == 0 || sourceToken.Length > 64) return false;
        if (key.Length == 0 || key.Length > 1024) return false;
        if (expires < clock.GetUtcNow().ToUnixTimeSeconds()) return false;
        var disposition = NormalizeDisposition(contentDisposition);
        var expected = Sign(sourceToken, key, expires, disposition);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>
    /// Only <c>attachment</c> is honoured: letting the query string pick an
    /// arbitrary <c>Content-Disposition</c> would turn a media link into a
    /// header-injection surface.
    /// </summary>
    public static string NormalizeDisposition(string? value) =>
        string.Equals(value?.Trim(), "attachment", StringComparison.OrdinalIgnoreCase) ? "attachment" : "";

    private string Sign(string sourceToken, string key, long expires, string disposition)
    {
        var payload = string.Join('|',
            sourceToken,
            key,
            expires.ToString(CultureInfo.InvariantCulture),
            disposition);
        var mac = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
