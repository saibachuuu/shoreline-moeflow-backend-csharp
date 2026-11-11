using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Shoreline.Api.Modules.ZitengPartner;

public sealed class ZitengPartnerApiException : Exception
{
    public ZitengPartnerApiException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record ZitengPartnerWork(
    string Id,
    string Reference,
    string DisplayTitle,
    string OriginalTitle,
    string Author,
    string Circle,
    string State,
    string Stage);

public sealed record ZitengPartnerSearchResult(
    string Query,
    IReadOnlyList<ZitengPartnerWork> Works,
    int Total,
    bool Truncated);

/// <summary>
/// HTTP client for the official partner API. All third-party traffic goes
/// through the injected <see cref="HttpClient"/>, which makes it possible to
/// exercise every response in tests without a real network call.
/// </summary>
public sealed class ZitengPartnerClient(
    ZitengPartnerSettings settings,
    HttpClient http,
    IZitengPartnerApiGate gate,
    TimeProvider clock,
    IZitengPartnerDelay delay)
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    private const string UserAgent = "MoeFlow/ZitengPartnerSearch";

    private sealed class CatalogChangedException : Exception;

    public async Task<ZitengPartnerSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        var normalized = ZitengPartnerText.Strip(query);
        if (normalized.Length == 0) throw new ZitengPartnerApiException("检索词为空");
        if (ZitengPartnerText.Length(normalized) > 240)
            throw new ZitengPartnerApiException("检索词超过 240 字符上限");

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (settings.TaskTimeoutSeconds > 0)
            overall.CancelAfter(TimeSpan.FromSeconds(settings.TaskTimeoutSeconds));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await SearchPagesAsync(normalized, overall.Token, ct).ConfigureAwait(false);
            }
            catch (CatalogChangedException) when (attempt == 0)
            {
                // The whole page round is discarded; the retry starts at page 1
                // without a revision, exactly like the Python client.
            }
            catch (CatalogChangedException)
            {
                throw new ZitengPartnerApiException("索引持续变化，未能取得稳定结果");
            }
        }
        throw new ZitengPartnerApiException("索引持续变化，未能取得稳定结果");
    }

    private async Task<ZitengPartnerSearchResult> SearchPagesAsync(string query, CancellationToken overall,
        CancellationToken caller)
    {
        if (string.IsNullOrEmpty(settings.ApiKey))
            throw new ZitengPartnerApiException("未配置 API 密钥");

        var works = new List<ZitengPartnerWork>();
        var total = 0;
        var truncated = false;
        string? revision = null;
        for (var page = 1; page <= settings.MaxPages; page++)
        {
            var uri = BuildUri(query, page, revision);
            var payload = await RequestAsync(uri, overall, caller).ConfigureAwait(false);
            if (!payload.TryGetProperty("revision", out var revisionElement))
                revisionElement = default;
            if (string.IsNullOrEmpty(revision) && PythonTruthy(revisionElement))
                revision = PythonString(revisionElement);

            if (payload.TryGetProperty("total", out var totalElement))
                total = PythonInt(totalElement);
            else
                total = 0;

            if (payload.TryGetProperty("results", out var results) && PythonTruthy(results))
            {
                if (results.ValueKind != JsonValueKind.Array)
                    throw new ZitengPartnerApiException("返回内容格式不合法");
                foreach (var item in results.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new ZitengPartnerApiException("返回内容格式不合法");
                    works.Add(ParseWork(item));
                }
            }

            var hasNext = payload.TryGetProperty("next_page", out var nextPage) && PythonTruthy(nextPage);
            if (!hasNext) break;
            if (page == settings.MaxPages) truncated = true;
        }

        return new ZitengPartnerSearchResult(query, works, total, truncated);
    }

    private async Task<JsonElement> RequestAsync(Uri uri, CancellationToken overall, CancellationToken caller)
    {
        var startedAt = clock.GetUtcNow();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            caller.ThrowIfCancellationRequested();
            try
            {
                await gate.WaitAsync(overall).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new ZitengPartnerApiException("查询总超时");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall);
            if (settings.TimeoutSeconds > 0)
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                throw new ZitengPartnerApiException("查询总超时");
            }
            catch (OperationCanceledException)
            {
                throw new ZitengPartnerApiException("请求超时");
            }
            catch (HttpRequestException exception)
            {
                // Keep the transport cause (for example the public-network
                // guard rejection) for diagnostics; only Message is user-visible.
                throw new ZitengPartnerApiException("请求失败: 网络错误", exception);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 409) throw new CatalogChangedException();
                if (status == 429)
                {
                    if (attempt > 0) throw new ZitengPartnerApiException("被限流且重试后仍失败");
                    var wait = RetryAfterDelay(response);
                    var remaining = RemainingTaskTime(startedAt);
                    if (remaining <= TimeSpan.Zero) throw new ZitengPartnerApiException("查询总超时");
                    if (wait > remaining) wait = remaining;
                    if (wait > TimeSpan.Zero)
                    {
                        try
                        {
                            await delay.DelayAsync(wait, overall).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (caller.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            throw new ZitengPartnerApiException("查询总超时");
                        }
                    }
                    continue;
                }
                if (status >= 500) throw new ZitengPartnerApiException($"对方服务不可用: HTTP {status}");
                if (status >= 300)
                    throw new ZitengPartnerApiException($"请求被拒绝: HTTP {status}");
                if (status >= 400)
                    // Never echo the provider body: it may contain credentials or
                    // internal request details. The Python client included a
                    // truncated body; the C# replacement deliberately keeps only
                    // the status in the user-visible error.
                    throw new ZitengPartnerApiException($"请求被拒绝: HTTP {status}");

                try
                {
                    return await ReadPayloadAsync(response, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (overall.IsCancellationRequested)
                {
                    throw new ZitengPartnerApiException("查询总超时");
                }
                catch (OperationCanceledException)
                {
                    throw new ZitengPartnerApiException("请求超时");
                }
                catch (IOException)
                {
                    throw new ZitengPartnerApiException("请求失败: 网络错误");
                }
            }
        }
        throw new ZitengPartnerApiException("被限流且重试后仍失败");
    }

    private TimeSpan RemainingTaskTime(DateTimeOffset startedAt)
    {
        // Even when the caller disables the overall timeout, a broken Retry-After
        // value must not translate into an unbounded Task.Delay.
        if (settings.TaskTimeoutSeconds <= 0) return TimeSpan.FromSeconds(180);
        var used = clock.GetUtcNow() - startedAt;
        if (used < TimeSpan.Zero) used = TimeSpan.Zero;
        return TimeSpan.FromSeconds(settings.TaskTimeoutSeconds) - used;
    }

    private static TimeSpan RetryAfterDelay(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return TimeSpan.FromSeconds(1);
        var raw = values.FirstOrDefault()?.Trim();
        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(1);
        if (double.IsNaN(seconds)) return TimeSpan.FromSeconds(1);
        if (double.IsPositiveInfinity(seconds)) return TimeSpan.MaxValue;
        if (seconds < 0) return TimeSpan.Zero;
        return seconds > TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(seconds);
    }

    private Uri BuildUri(string query, int page, string? revision)
    {
        var builder = new UriBuilder(settings.ApiUrl);
        var parts = new List<string>
        {
            "q=" + Uri.EscapeDataString(query),
            "page=" + page.ToString(CultureInfo.InvariantCulture),
        };
        if (settings.IncludeInactive) parts.Add("include_inactive=true");
        if (!string.IsNullOrEmpty(revision)) parts.Add("revision=" + Uri.EscapeDataString(revision));
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0 ? string.Join("&", parts) : existing + "&" + string.Join("&", parts);
        return builder.Uri;
    }

    private static async Task<JsonElement> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new ZitengPartnerApiException("返回内容过大");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new ZitengPartnerApiException("返回内容过大");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(offset));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ZitengPartnerApiException("返回内容格式不合法");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ZitengPartnerApiException("返回内容不是合法 JSON");
        }
    }

    private static ZitengPartnerWork ParseWork(JsonElement raw) => new(
        PythonOrEmpty(raw, "id"),
        PythonOrEmpty(raw, "reference"),
        PythonOrEmpty(raw, "display_title"),
        PythonOrEmpty(raw, "original_title"),
        PythonOrEmpty(raw, "author"),
        PythonOrEmpty(raw, "circle"),
        PythonOrEmpty(raw, "state"),
        PythonOrEmpty(raw, "stage"));

    private static string PythonOrEmpty(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || !PythonTruthy(value)) return "";
        return PythonString(value);
    }

    private static string PythonString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Array => PythonRepr(value),
        JsonValueKind.Object => PythonRepr(value),
        _ => "",
    };

    private static string PythonRepr(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return "[" + string.Join(", ", value.EnumerateArray().Select(PythonReprValue)) + "]";
        if (value.ValueKind == JsonValueKind.Object)
            return "{" + string.Join(", ", value.EnumerateObject()
                .Select(property => PythonReprString(property.Name) + ": " + PythonReprValue(property.Value))) + "}";
        return PythonReprValue(value);
    }

    private static string PythonReprValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => PythonReprString(value.GetString() ?? ""),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "None",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array or JsonValueKind.Object => PythonRepr(value),
        _ => "",
    };

    private static string PythonReprString(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(quote.ToString(), "\\" + quote, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return quote + escaped + quote;
    }

    private static bool PythonTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => false,
    };

    private static int PythonInt(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return 0;
        if (value.ValueKind == JsonValueKind.False) return 0;
        if (value.ValueKind == JsonValueKind.True) return 1;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var integer)) return integer;
            if (value.TryGetInt64(out var longInteger))
                return longInteger is >= int.MinValue and <= int.MaxValue ? (int)longInteger : throw TooLarge();
            if (value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                var truncated = Math.Truncate(number);
                if (truncated is >= int.MinValue and <= int.MaxValue) return (int)truncated;
            }
            throw TooLarge();
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        throw new ZitengPartnerApiException("返回内容格式不合法");
    }

    private static ZitengPartnerApiException TooLarge() => new("返回内容格式不合法");
}
