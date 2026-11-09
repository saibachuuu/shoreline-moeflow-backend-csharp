using System.Globalization;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Configuration for the optional Ziteng partner module. The Python module reads
/// the same keys from the <c>ZITENG_PARTNER_*</c> environment variables and its
/// local configuration file. The C# host exposes the values through the normal
/// ASP.NET configuration system, while still accepting the legacy environment
/// variable names explicitly (single underscores are not mapped by the standard
/// environment provider to a configuration section).
/// </summary>
public sealed class ZitengPartnerSettings
{
    public const string SectionName = "ZitengPartner";
    public const string DefaultApiUrl = "https://partner-api.ziteng.org/partner-api/v1/works/search";

    public string ApiUrl { get; init; } = DefaultApiUrl;
    public string ApiKey { get; init; } = "";
    public bool IncludeInactive { get; init; }
    public double CacheTtlSeconds { get; init; } = 604800;
    public int MaxPages { get; init; } = 3;
    public double TimeoutSeconds { get; init; } = 20;
    public double MinIntervalSeconds { get; init; } = 1.1;
    public int MaxResults { get; init; } = 5;
    public double TaskTimeoutSeconds { get; init; } = 180;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Never include the API key in logs, exception text, or diagnostics.</summary>
    public override string ToString() =>
        $"{nameof(ZitengPartnerSettings)}({nameof(ApiUrl)}={ApiUrl}, {nameof(Enabled)}={Enabled}, " +
        $"{nameof(IncludeInactive)}={IncludeInactive}, {nameof(CacheTtlSeconds)}={CacheTtlSeconds.ToString(CultureInfo.InvariantCulture)}, " +
        $"{nameof(MaxPages)}={MaxPages}, {nameof(TimeoutSeconds)}={TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}, " +
        $"{nameof(MinIntervalSeconds)}={MinIntervalSeconds.ToString(CultureInfo.InvariantCulture)}, " +
        $"{nameof(MaxResults)}={MaxResults}, {nameof(TaskTimeoutSeconds)}={TaskTimeoutSeconds.ToString(CultureInfo.InvariantCulture)})";

    public static ZitengPartnerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ZitengPartnerSettings
        {
            ApiUrl = ReadString(configuration, "ApiUrl", "ZITENG_PARTNER_API_URL")?.Trim() ?? DefaultApiUrl,
            // The legacy config helper strips the key. An explicitly empty value is
            // meaningful: it disables the module without removing the routes.
            ApiKey = (ReadString(configuration, "ApiKey", "ZITENG_PARTNER_API_KEY") ?? "").Trim(),
            IncludeInactive = ReadBool(configuration, "IncludeInactive", "ZITENG_PARTNER_INCLUDE_INACTIVE", false),
            CacheTtlSeconds = ReadDouble(configuration, "CacheTtl", "ZITENG_PARTNER_CACHE_TTL", 604800),
            MaxPages = ReadInt(configuration, "MaxPages", "ZITENG_PARTNER_MAX_PAGES", 3),
            TimeoutSeconds = ReadDouble(configuration, "Timeout", "ZITENG_PARTNER_TIMEOUT", 20),
            MinIntervalSeconds = ReadDouble(configuration, "MinInterval", "ZITENG_PARTNER_MIN_INTERVAL", 1.1),
            MaxResults = ReadInt(configuration, "MaxResults", "ZITENG_PARTNER_MAX_RESULTS", 5),
            TaskTimeoutSeconds = ReadDouble(configuration, "TaskTimeout", "ZITENG_PARTNER_TASK_TIMEOUT", 180),
        };
    }

    /// <summary>Remove a configured secret from diagnostic text.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = text;
        if (!string.IsNullOrEmpty(ApiKey) && ApiKey.Length >= 3)
            value = value.Replace(ApiKey, "***", StringComparison.Ordinal);
        value = System.Text.RegularExpressions.Regex.Replace(
            value, @"(?i)(authorization|bearer)\s*[:=]?\s*[^\s,;]+", "$1 ***");
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 300 ? value : value[..300];
    }

    private static string? ReadString(IConfiguration configuration, string key, string legacyKey)
    {
        // Legacy environment variables take priority, matching the Python module
        // (environment > local configuration file > defaults).
        var legacy = configuration[legacyKey];
        if (legacy is not null) return legacy;
        return configuration[SectionName + ":" + key];
    }

    private static bool ReadBool(IConfiguration configuration, string key, string legacyKey, bool fallback) =>
        bool.TryParse(ReadString(configuration, key, legacyKey), out var parsed) ? parsed : fallback;

    private static int ReadInt(IConfiguration configuration, string key, string legacyKey, int fallback) =>
        int.TryParse(ReadString(configuration, key, legacyKey), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ReadDouble(IConfiguration configuration, string key, string legacyKey, double fallback) =>
        double.TryParse(ReadString(configuration, key, legacyKey), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : fallback;
}
