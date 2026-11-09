using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

[CollectionDefinition("local-services")]
public sealed class LocalServicesCollection : ICollectionFixture<LocalServices>;

public sealed class LocalServices : IAsyncLifetime
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
        });
        Http = App.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
        // Only this randomly named database on the dedicated localhost port is removed.
        await new MongoClient("mongodb://127.0.0.1:27128").DropDatabaseAsync(DatabaseName);
    }
}
