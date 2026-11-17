using Microsoft.Extensions.Configuration;
using MongoDB.Bson;

namespace Shoreline.Api.Modules.StoragePool;

/// <summary>
/// The site's own storage, as configured by the deployment's <c>STORAGE_*</c>
/// environment variables.
///
/// It has no row in <c>storage_pool_source</c> — projects that live on it simply
/// have no <c>csid</c> — so the admin console used to hide it. The console now
/// pins a synthetic read-only entry to the top of the list, and this class is
/// the single place that derives it, so the list, the probe and the media proxy
/// all describe the same backend.
/// </summary>
public static class StoragePoolDefaultSource
{
    /// <summary>Path/sentinel id of the synthetic entry.</summary>
    public const string Id = "default";

    private static readonly string[] S3Types =
    [
        "S3", "R2", "AWS", "MINIO", "GARAGE", "S3_COMPATIBLE", "S3-COMPATIBLE",
    ];

    public static bool IsDefaultId(string? id) =>
        string.Equals((id ?? "").Trim(), Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The storage-source row shape for the environment configuration, or
    /// <c>null</c> when the site does not use an S3-compatible backend (local
    /// disk, or an incomplete configuration). Returning null keeps the entry out
    /// of the list instead of showing a row that cannot be probed.
    /// </summary>
    public static BsonDocument? Read(IConfiguration? configuration)
    {
        if (configuration is null) return null;
        var type = (configuration["Storage:Type"] ?? configuration["Storage:BucketStyle"] ?? "S3").Trim();
        if (!S3Types.Contains(type, StringComparer.OrdinalIgnoreCase)) return null;
        var endpoint = (configuration["Storage:Endpoint"] ?? "").Trim().TrimEnd('/');
        var bucket = (configuration["Storage:Bucket"] ?? "").Trim();
        var accessKey = (configuration["Storage:AccessKey"] ?? "").Trim();
        var secretKey = configuration["Storage:SecretKey"] ?? "";
        if (endpoint.Length == 0 || bucket.Length == 0 || accessKey.Length == 0 || secretKey.Length == 0)
            return null;

        var region = (configuration["Storage:Region"] ?? "").Trim();
        // Mirror ObjectStorage's own fallbacks so the pinned row, the media URL
        // and the transfer engine can never describe a different bucket layout.
        var keyPrefix = configuration["Storage:KeyPrefix"];
        if (string.IsNullOrWhiteSpace(keyPrefix)
            && string.Equals(configuration["STORAGE_TYPE"]?.Trim(), "OSS", StringComparison.OrdinalIgnoreCase))
            keyPrefix = string.IsNullOrWhiteSpace(configuration["OSS_KEY_PREFIX"])
                ? "storage/"
                : configuration["OSS_KEY_PREFIX"];
        var addressingStyle = configuration["Storage:ForcePathStyle"];
        if (string.IsNullOrWhiteSpace(addressingStyle))
            addressingStyle = configuration["OSS_S3_ADDRESSING_STYLE"];
        return new BsonDocument
        {
            ["_id"] = Id,
            ["n"] = "默认存储",
            ["e"] = true,
            ["ep"] = endpoint,
            ["b"] = bucket,
            ["sp"] = StoragePoolBson.NormalizeSubPath(keyPrefix),
            ["r"] = region.Length == 0 ? "us-east-1" : region,
            ["as"] = IsPathStyle(addressingStyle) ? "path" : "virtual",
            ["vs"] = true,
            ["ak"] = accessKey,
            ["sk"] = secretKey,
            ["pd"] = (configuration["Storage:Domain"] ?? "").Trim(),
            ["rm"] = "由环境变量 STORAGE_* 配置，不可修改",
            ["iad"] = 90,
            ["mo"] = 2000,
            // Left false: projects on the default backend carry no csid, so their
            // bytes are never attributed here and "has data" would be a guess.
            ["hd"] = false,
            ["px"] = configuration.GetValue("Storage:MediaProxy", false),
            ["ct"] = BsonNull.Value,
            ["ut"] = BsonNull.Value,
        };
    }

    /// <summary>
    /// ObjectStorage's path-style rule: an unset value means path-style, and
    /// anything other than <c>virtual</c> does too.
    /// </summary>
    private static bool IsPathStyle(string? addressingStyle) =>
        string.IsNullOrWhiteSpace(addressingStyle) ||
        !string.Equals(addressingStyle.Trim(), "virtual", StringComparison.OrdinalIgnoreCase);
}
