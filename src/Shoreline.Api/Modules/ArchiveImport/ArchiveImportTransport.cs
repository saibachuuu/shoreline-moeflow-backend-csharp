using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>Resolve and download ports; one interface so tests can use an in-memory transport.</summary>
public interface IArchiveImportTransport
{
    Task<string?> ResolveArchiveUrlAsync(string apiUrl, string gid, string token, string apiKey, CancellationToken ct);
    Task DownloadAsync(string zipUrl, string destinationPath, long maxBytes, CancellationToken ct);
}

/// <summary>
/// Production transport. Provider credentials are only sent to the configured provider endpoint, never to the
/// download host. Redirects are disabled and every actual connection address is checked to be public.
/// </summary>
public sealed class ArchiveImportTransport : IArchiveImportTransport, IDisposable
{
    private static readonly Regex ArchiveHost =
        new(@"^[a-z0-9-]+\.hath\.network$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public ArchiveImportTransport(IConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _ = configuration;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShorelineArchiveImport/1.0");
    }

    public async Task<string?> ResolveArchiveUrlAsync(string apiUrl, string gid, string token, string apiKey,
        CancellationToken ct)
    {
        if (!TryParseApiBase(apiUrl, out var baseUri)) return null;
        var endpoint = new Uri(baseUri, "api/v1/parse");
        var body = JsonSerializer.Serialize(new
        {
            gallery_id = gid,
            gallery_key = token,
            force = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Headers.TryAddWithoutValidation("X-Client", "");
        // TODO(archive-import): 空 X-Client 头的服务端行为未验证（Python 侧是否同值未知），
        // 后续按 Python 行为补真实 client-key/版本串或改为可配置项并加断言。当前仅保证编译通过。

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out _)) return null;
            if (!root.TryGetProperty("archive_url", out var archiveUrl) ||
                archiveUrl.ValueKind != JsonValueKind.String) return null;
            return Canonicalize(archiveUrl.GetString(), gid);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task DownloadAsync(string zipUrl, string destinationPath, long maxBytes, CancellationToken ct)
    {
        if (!TryParseDownloadUrl(zipUrl, out var uri))
            throw new ArchiveImportFailure("下载失败", "归档下载地址无效");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ArchiveImportFailure("下载失败", "归档下载超时");
        }
        catch (HttpRequestException)
        {
            throw new ArchiveImportFailure("下载失败", "归档下载异常");
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
                throw new ArchiveImportFailure("下载失败", "归档下载地址发生了重定向");
            if (!response.IsSuccessStatusCode)
                throw new ArchiveImportFailure("下载失败", $"归档下载异常：HTTP {(int)response.StatusCode}");
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && !SameOrigin(uri, finalUri))
                throw new ArchiveImportFailure("下载失败", "归档下载地址发生了重定向");

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
                throw new ArchiveDownloadLimitException(maxBytes);

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true);
            var buffer = new byte[256 * 1024];
            long total = 0;
            while (true)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        read = await input.ReadAsync(buffer, idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new ArchiveImportFailure("下载失败", "归档下载超时");
                    }
                }
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new ArchiveDownloadLimitException(maxBytes);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            if (contentLength is > 0 && total != contentLength.Value)
                throw new ArchiveImportFailure("下载失败", "归档下载不完整");
        }
    }

    public void Dispose() => _http.Dispose();

    private static bool TryParseApiBase(string value, out Uri uri)
    {
        uri = null!;
        try
        {
            var normalized = ExternalApiUrl.Normalize(value, Array.Empty<string>(), requireAllowlist: false);
            if (!Uri.TryCreate(normalized + "/", UriKind.Absolute, out var candidate) ||
                candidate.Scheme != "https")
                return false;
            uri = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseDownloadUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != "https") return false;
        if (parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0) return false;
        if (parsed.Query.Length != 0 && parsed.Query != "?start=1") return false;
        var host = parsed.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!ArchiveHost.IsMatch(host)) return false;
        uri = parsed;
        return true;
    }

    private static bool SameOrigin(Uri expected, Uri actual) =>
        actual.Scheme == "https" &&
        actual.UserInfo.Length == 0 &&
        expected.Port == actual.Port &&
        expected.IdnHost.TrimEnd('.').Equals(actual.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static string? Canonicalize(string? archiveUrl, string gid)
    {
        if (string.IsNullOrWhiteSpace(archiveUrl)) return null;
        var match = Regex.Match(archiveUrl,
            @"https://(?<host>[A-Za-z0-9-]+)\.hath\.network/archive/[^/\s""']+/(?<a>[A-Za-z0-9]+)/(?<b>[A-Za-z0-9]+)/",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return $"https://{match.Groups["host"].Value}.hath.network/archive/{gid}/" +
               $"{match.Groups["a"].Value}/{match.Groups["b"].Value}/2?start=1";
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        // The connection-level public-address check below is the security
        // boundary. An environment HTTP(S)_PROXY would make ConnectCallback
        // dial the proxy instead of the target, so the guard would only see the
        // proxy address and the archive host could still be reached indirectly.
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, ct) => PublicNetworkGuard.ConnectAsync(context, ct, "归档主机"),
    };
}
