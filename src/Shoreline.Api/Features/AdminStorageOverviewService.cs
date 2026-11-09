namespace Shoreline.Api.Features;

/// <summary>
/// Read-only, redacted view of the configured storage backend. This mirrors the
/// legacy <c>GET /v1/admin/storage/default</c> response shape without exposing
/// access keys, secrets, ports, account paths, or a full endpoint URL.
/// </summary>
public sealed class AdminStorageOverviewService(IConfiguration configuration)
{
    private const string AliyunEndpointWarning =
        "配置危险：endpoint 指向阿里云，但储存风格被解析为标准 S3。阿里云请显式设置 OSS_BUCKET_STYLE=ALI，否则签名方式与图片处理都会失效。";

    public Dictionary<string, object?> View()
    {
        // Report the configured raw token when one exists. An absent token means
        // the C# backend default is S3, but we do not invent a raw legacy value.
        var typeRaw = FirstNonEmpty(configuration["Storage:Type"], configuration["Storage:BucketStyle"]);
        var token = typeRaw.ToUpperInvariant();
        var isLocal = token is "LOCAL" or "FILESYSTEM" or "FILE" or "DISK";
        var style = ResolveStyle(token, out var unknownStyle);
        var storageType = isLocal ? "LOCAL_STORAGE" : "OSS";
        // For local storage the legacy overview has no OSS bucket-style value.
        var raw = isLocal ? "" : typeRaw;
        var endpoint = configuration["Storage:Endpoint"] ?? "";
        var endpointHost = storageType == "OSS" && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            ? endpointUri.Host
            : "";
        var keyPrefix = storageType == "OSS" && style == "S3"
            ? configuration["Storage:KeyPrefix"] ?? ""
            : "";
        var viaCdn = storageType == "OSS" && configuration.GetValue<bool>("Storage:ViaCdn");
        var cdnDomain = configuration["Storage:Domain"] ?? "";
        var serverSideImageProcessing = style == "ALI";
        var cdnUrlAuth = style == "ALI";
        var warning = Warning(raw, style, unknownStyle, endpoint);

        return new Dictionary<string, object?>
        {
            ["storage_type"] = storageType,
            ["bucket_style"] = storageType == "OSS" ? style : null,
            ["bucket_style_raw"] = raw,
            ["endpoint_host"] = endpointHost,
            ["bucket"] = storageType == "OSS" ? configuration["Storage:Bucket"] ?? "" : "",
            ["key_prefix"] = keyPrefix,
            ["via_cdn"] = viaCdn,
            ["cdn_domain"] = cdnDomain,
            ["prefixes"] = new Dictionary<string, object?>
            {
                ["files"] = Prefix(configuration["Storage:FilePrefix"], "files/"),
                ["outputs"] = Prefix(configuration["Storage:OutputPrefix"], "outputs/"),
                ["user_avatars"] = AvatarStoragePrefixes.User(configuration),
                ["team_avatars"] = AvatarStoragePrefixes.Team(configuration)
            },
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["server_side_image_processing"] = serverSideImageProcessing,
                ["cdn_url_auth"] = cdnUrlAuth
            },
            ["requires_local_thumbnail"] = storageType != "OSS" || !serverSideImageProcessing,
            ["warning"] = warning
        };
    }

    private static string? ResolveStyle(string token, out bool unknown)
    {
        unknown = false;
        if (token is "ALI" or "ALIYUN" or "ALIYUN_OSS" or "OSS") return "ALI";
        if (token is "" or "S3" or "R2" or "AWS" or "MINIO" or "GARAGE" or "S3_COMPATIBLE" or "S3-COMPATIBLE") return "S3";
        if (token is "LOCAL" or "FILESYSTEM" or "FILE" or "DISK") return null;
        unknown = true;
        return "S3";
    }

    private static string Warning(string raw, string? style, bool unknownStyle, string endpoint)
    {
        if (unknownStyle)
        {
            var escaped = raw.Replace("\\", "\\\\").Replace("'", "\\'");
            return $"OSS_BUCKET_STYLE='{escaped}' 不是已知取值，已按标准 S3 处理。可用取值：S3 / ALI。";
        }

        if (style == "S3" && endpoint.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase))
        {
            return AliyunEndpointWarning;
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    private static string Prefix(string? configured, string fallback)
    {
        if (configured is null) return fallback;
        var value = configured.Trim();
        if (value.Length == 0) return "";
        return value.TrimEnd('/') + "/";
    }
}
