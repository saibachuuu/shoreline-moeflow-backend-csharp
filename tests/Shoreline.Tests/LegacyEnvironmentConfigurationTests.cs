using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

public sealed class LegacyEnvironmentConfigurationTests
{
    [Fact]
    public void MapsPythonEnvironmentNames()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["SECRET_KEY"] = "0123456789abcdef0123456789abcdef",
            ["ADMIN_EMAIL"] = "owner@example.test",
            ["ADMIN_INITIAL_PASSWORD"] = "initial-password",
            ["EMAIL_SMTP_HOST"] = "smtp.example.test",
            ["EMAIL_SMTP_PORT"] = "465",
            ["EMAIL_USERNAME"] = "mailer",
            ["EMAIL_PASSWORD"] = "mail-password",
            ["EMAIL_ADDRESS"] = "no-reply@example.test",
            ["EMAIL_USE_SSL"] = "True",
            ["ENABLE_USER_EMAIL"] = "True",
            ["SITE_NAME"] = "欶澜",
            ["SITE_ORIGIN"] = "https://moeflow.example.test",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal("0123456789abcdef0123456789abcdef", configuration["Auth:Secret"]);
        Assert.Equal("owner@example.test", configuration["Auth:AdminEmail"]);
        Assert.Equal("initial-password", configuration["Auth:AdminInitialPassword"]);
        Assert.Equal("smtp.example.test", configuration["Mail:Host"]);
        Assert.Equal("465", configuration["Mail:Port"]);
        Assert.Equal("mailer", configuration["Mail:Username"]);
        Assert.Equal("mail-password", configuration["Mail:Password"]);
        Assert.Equal("no-reply@example.test", configuration["Mail:From"]);
        Assert.Equal("True", configuration["Mail:UseSsl"]);
        Assert.Equal("true", configuration["Mail:Enabled"]);
        Assert.Equal("欶澜", configuration["Site:Name"]);
        Assert.Equal("https://moeflow.example.test", configuration["Site:Origin"]);
    }

    [Fact]
    public void ModernKeysWinOverLegacyNames()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["SECRET_KEY"] = "legacy-secret",
            ["Auth:Secret"] = "modern-secret",
            ["ADMIN_EMAIL"] = "legacy@example.test",
            ["Auth:AdminEmail"] = "modern@example.test",
            ["EMAIL_SMTP_HOST"] = "legacy.example.test",
            ["Mail:Host"] = "modern.example.test",
            ["ENABLE_USER_EMAIL"] = "True",
            ["Mail:Enabled"] = "false",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal("modern-secret", configuration["Auth:Secret"]);
        Assert.Equal("modern@example.test", configuration["Auth:AdminEmail"]);
        Assert.Equal("modern.example.test", configuration["Mail:Host"]);
        Assert.Equal("false", configuration["Mail:Enabled"]);
    }

    [Fact]
    public void BuildsLegacyMongoUri()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["MONGODB_USER"] = "moeflow",
            ["MONGODB_PASS"] = "p@ss word",
            ["MONGODB_DB_NAME"] = "moeflow",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal(
            "mongodb://moeflow:p%40ss%20word@moeflow-mongodb:27017/moeflow?authSource=admin",
            configuration["Mongo:Uri"]);
    }

    [Fact]
    public void PrefersExplicitMongoUri()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["MONGODB_URI"] = "mongodb://explicit:27017/moeflow",
            ["MONGODB_USER"] = "moeflow",
            ["MONGODB_PASS"] = "password",
            ["MONGODB_DB_NAME"] = "moeflow",
            ["Mongo:Uri"] = "mongodb://modern:27017/moeflow",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal("mongodb://modern:27017/moeflow", configuration["Mongo:Uri"]);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
