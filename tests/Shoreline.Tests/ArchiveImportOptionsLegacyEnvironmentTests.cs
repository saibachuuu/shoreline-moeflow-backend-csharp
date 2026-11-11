using Microsoft.Extensions.Configuration;
using Shoreline.Api.Modules.ArchiveImport;

namespace Shoreline.Tests;

public sealed class ArchiveImportOptionsLegacyEnvironmentTests
{
    [Fact]
    public void LegacyZipAndEntryLimitsAreMegabytes()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ARCHIVE_MAX_ZIP_BYTES"] = "7",
            ["ARCHIVE_MAX_ENTRY_BYTES"] = "5",
        });

        var options = ArchiveImportOptions.From(configuration);

        Assert.Equal(7L * 1024 * 1024, options.MaxZipBytes);
        Assert.Equal(5L * 1024 * 1024, options.MaxEntryBytes);
    }

    [Fact]
    public void ModernByteLimitsWinOverLegacyMegabytes()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ARCHIVE_MAX_ZIP_BYTES"] = "7",
            ["Archive:MaxZipBytes"] = "123",
            ["ARCHIVE_MAX_ENTRY_BYTES"] = "5",
            ["Archive:MaxEntryBytes"] = "456",
        });

        var options = ArchiveImportOptions.From(configuration);

        Assert.Equal(123, options.MaxZipBytes);
        Assert.Equal(456, options.MaxEntryBytes);
    }

    [Fact]
    public void LegacyUncompressedLimitIsMegabytes()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ARCHIVE_MAX_ZIP_UNCOMPRESSED_MB"] = "3",
        });

        var options = ArchiveImportOptions.From(configuration);

        Assert.Equal(3L * 1024 * 1024, options.MaxUncompressedBytes);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}