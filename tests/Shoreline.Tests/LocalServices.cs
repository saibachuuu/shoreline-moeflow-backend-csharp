using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

[CollectionDefinition("local-services")]
public sealed class LocalServicesCollection : ICollectionFixture<LocalServices>;

// Some classes exercise globally scoped collections (the admin file listing,
// the language catalog, the fixed owner account). They each get their own
// fixture instance, and therefore their own randomly named database, so the
// result no longer depends on which other class ran first in the shared
// collection.
[CollectionDefinition("local-services-file")]
public sealed class FileServicesCollection : ICollectionFixture<LocalServices>;

[CollectionDefinition("local-services-admin-user")]
public sealed class AdminUserServicesCollection : ICollectionFixture<LocalServices>;

[CollectionDefinition("local-services-language")]
public sealed class LanguageServicesCollection : ICollectionFixture<LocalServices>;

[CollectionDefinition("local-services-thumbnail")]
public sealed class ThumbnailServicesCollection : ICollectionFixture<LocalServicesWithoutWorker>;

public class LocalServices : IAsyncLifetime
{
    public string DatabaseName { get; } = "shoreline_csharp_test_" + Guid.NewGuid().ToString("N");
    public MongoStore Store { get; private set; } = null!;
    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public IConfiguration Configuration { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = "mongodb://127.0.0.1:27128/" + DatabaseName,
            ["Auth:Secret"] = SecurityTests.Secret,
            ["Auth:AdminEmail"] = "owner@example.test",
            ["Archive:AllowedHosts:0"] = "api.example.test",
            ["Storage:Endpoint"] = "http://127.0.0.1:29128",
            ["Storage:Bucket"] = "shoreline-test",
            ["Storage:AccessKey"] = "local-test-only",
            ["Storage:SecretKey"] = "local-disposable-test-secret",
            ["Storage:ForcePathStyle"] = "true",
            ["Mail:Enabled"] = "true",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = "21128",
            ["Mail:StartTls"] = "false",
            ["Mail:From"] = "noreply@example.test"
        };
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Store = new MongoStore(Configuration);
        await Store.InitializeAsync(CancellationToken.None);
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(ConfigureTestServices);
        });
        Http = App.CreateClient();
    }

    /// <summary>Test-only service overrides for hosts that need a different lifecycle.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
        // Only this randomly named database on the dedicated localhost port is removed.
        await new MongoClient("mongodb://127.0.0.1:27128").DropDatabaseAsync(DatabaseName);
    }
}

/// <summary>
/// Test host without the shared background <see cref="JobWorker"/>. Tests that
/// invoke a job handler directly (thumbnail end-to-end) must own the job; the
/// 500ms polling worker would otherwise claim and process the same row with
/// different storage settings, which is a test-isolation race, not a product
/// behaviour. Production registration in Program.cs is untouched.
/// </summary>
public sealed class LocalServicesWithoutWorker : LocalServices
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        foreach (var descriptor in services
                     .Where(descriptor => descriptor.ImplementationType == typeof(JobWorker))
                     .ToList())
        {
            services.Remove(descriptor);
        }
    }
}
