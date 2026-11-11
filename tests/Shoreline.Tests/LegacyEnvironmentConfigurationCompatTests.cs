using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

public sealed class LegacyEnvironmentConfigurationCompatTests
{
    [Fact]
    public void MapsEmailUseSslFalseWithoutEnablingMail()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["EMAIL_USE_SSL"] = "False",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal("False", configuration["Mail:UseSsl"]);
        Assert.Null(configuration["Mail:Enabled"]);
    }

    [Fact]
    public void DoesNotEnableMailWhenUserEmailIsFalse()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ENABLE_USER_EMAIL"] = "False",
            ["Mail:Enabled"] = "",
        });

        LegacyEnvironmentConfiguration.Apply(configuration);

        Assert.Equal("", configuration["Mail:Enabled"]);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}