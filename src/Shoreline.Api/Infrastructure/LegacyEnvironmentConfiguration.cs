using Microsoft.Extensions.Configuration;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Bridges the environment variable names used by the Python deployment to the
/// configuration keys used by the C# application. Explicit <c>Auth__*</c>,
/// <c>Mail__*</c>, or <c>Mongo__*</c> values always win; the legacy names are
/// only consulted when the modern key is absent. This keeps an existing
/// <c>.env-backend</c> usable during a Python-to-C# cutover.
/// </summary>
public static class LegacyEnvironmentConfiguration
{
    public static void Apply(IConfiguration configuration)
    {
        Alias(configuration, "Auth:Secret", "SECRET_KEY");
        Alias(configuration, "Auth:AdminEmail", "ADMIN_EMAIL");
        Alias(configuration, "Auth:AdminInitialPassword", "ADMIN_INITIAL_PASSWORD");

        Alias(configuration, "Mail:Host", "EMAIL_SMTP_HOST");
        Alias(configuration, "Mail:Port", "EMAIL_SMTP_PORT");
        Alias(configuration, "Mail:Username", "EMAIL_USERNAME");
        Alias(configuration, "Mail:Password", "EMAIL_PASSWORD");
        Alias(configuration, "Mail:From", "EMAIL_ADDRESS");
        Alias(configuration, "Mail:UseSsl", "EMAIL_USE_SSL");

        if (string.IsNullOrWhiteSpace(configuration["Mail:Enabled"]))
        {
            var enabled = IsTrue(configuration["ENABLE_USER_EMAIL"]);
            if (enabled)
            {
                configuration["Mail:Enabled"] = "true";
            }
        }

        Alias(configuration, "Site:Name", "SITE_NAME");
        Alias(configuration, "Site:Origin", "SITE_ORIGIN");

        // Storage: the legacy .env names are the ones present on the production
        // host. ObjectStorage already consults them directly, but the admin
        // overview and storage-pool workers read Storage:* first, so bridge them
        // once here (Python app/config.py + app/services/oss.py).
        Alias(configuration, "Storage:Bucket", "OSS_BUCKET_NAME");
        Alias(configuration, "Storage:Endpoint", "OSS_ENDPOINT");
        Alias(configuration, "Storage:AccessKey", "OSS_ACCESS_KEY_ID");
        Alias(configuration, "Storage:SecretKey", "OSS_ACCESS_KEY_SECRET");
        Alias(configuration, "Storage:Region", "OSS_S3_REGION");
        Alias(configuration, "Storage:Domain", "STORAGE_DOMAIN");
        Alias(configuration, "Storage:KeyPrefix", "OSS_KEY_PREFIX");
        Alias(configuration, "Storage:BucketStyle", "OSS_BUCKET_STYLE");
        if (string.IsNullOrWhiteSpace(configuration["Storage:Type"]))
        {
            var legacyType = configuration["STORAGE_TYPE"]?.Trim();
            if (!string.IsNullOrWhiteSpace(legacyType))
            {
                var normalized = legacyType.ToUpperInvariant();
                configuration["Storage:Type"] = normalized switch
                {
                    "LOCAL_STORAGE" => "LOCAL",
                    "OSS" => string.IsNullOrWhiteSpace(configuration["OSS_BUCKET_STYLE"])
                        ? "S3" : configuration["OSS_BUCKET_STYLE"]!.Trim().ToUpperInvariant(),
                    _ => legacyType
                };
            }
        }
        if (string.IsNullOrWhiteSpace(configuration["Storage:ViaCdn"]) && IsTrue(configuration["OSS_VIA_CDN"]))
        {
            configuration["Storage:ViaCdn"] = "true";
        }

        if (string.IsNullOrWhiteSpace(configuration["Mongo:Uri"]))
        {
            var legacyUri = BuildLegacyMongoUri(configuration);
            if (legacyUri is not null)
            {
                configuration["Mongo:Uri"] = legacyUri;
            }
        }
    }

    private static void Alias(IConfiguration configuration, string key, string legacyKey)
    {
        if (!string.IsNullOrWhiteSpace(configuration[key]))
        {
            return;
        }

        var legacy = configuration[legacyKey];
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            configuration[key] = legacy;
        }
    }

    private static string? BuildLegacyMongoUri(IConfiguration configuration)
    {
        var explicitUri = configuration["MONGODB_URI"];
        if (!string.IsNullOrWhiteSpace(explicitUri))
        {
            return explicitUri.Trim();
        }

        var user = configuration["MONGODB_USER"];
        var password = configuration["MONGODB_PASS"];
        var database = configuration["MONGODB_DB_NAME"];
        if (string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(database))
        {
            return null;
        }

        // Matches app/config.py: the legacy compose service name and port.
        return $"mongodb://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}"
            + $"@moeflow-mongodb:27017/{Uri.EscapeDataString(database)}?authSource=admin";
    }

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;
}
