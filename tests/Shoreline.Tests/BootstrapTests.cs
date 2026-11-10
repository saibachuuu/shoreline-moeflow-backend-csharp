using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// Fresh-install bootstrap parity with Python <c>init_db</c>: site setting,
/// configured administrator and the bootstrap "Default Team". Each test owns a
/// random disposable database and an explicit opt-in Bootstrap:Enabled flag.
/// </summary>
public sealed class BootstrapTests : IAsyncLifetime
{
    private readonly string database = "shoreline_csharp_bootstrap_" + Guid.NewGuid().ToString("N");
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), "shoreline-bootstrap-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? app;
    private IMongoDatabase db = null!;
    private const string AdminEmail = "bootstrap-admin@example.test";
    private const string AdminPassword = "bootstrap-initial-password";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(storageRoot);
        db = new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").GetDatabase(database);
        await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database);
        app = CreateApp();
        _ = app.CreateClient(); // Force host startup so Program's bootstrap runs.
    }

    public async Task DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        try { await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database); }
        catch { /* best effort */ }
        try { Directory.Delete(storageRoot, recursive: true); } catch { /* best effort */ }
    }

    private WebApplicationFactory<Program> CreateApp(bool includePassword = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = $"mongodb://127.0.0.1:{TestEnvironment.MongoPort}/{database}",
            ["Auth:Secret"] = SecurityTests.Secret,
            ["Auth:AdminEmail"] = AdminEmail,
            ["Bootstrap:Enabled"] = "true",
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = storageRoot,
            ["Storage:Domain"] = "http://files.example.test",
            ["Mail:Enabled"] = "false",
            ["Archive:AllowedHosts:0"] = "api.example.test"
        };
        if (includePassword) settings["Auth:AdminInitialPassword"] = AdminPassword;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    [Fact]
    public async Task FreshStartup_SeedsSettingsAdminAndDefaultTeam()
    {
        var setting = await db.GetCollection<BsonDocument>("site_setting").Find(new BsonDocument("n", "site")).SingleAsync();
        Assert.True(setting.GetValue("ew", false).AsBoolean);
        Assert.True(setting.GetValue("oacg", false).AsBoolean);

        var admin = await db.GetCollection<BsonDocument>("user").Find(new BsonDocument("e", AdminEmail)).SingleAsync();
        Assert.True(admin.Flag("admin"));
        Assert.Equal("Admin", admin.Text("n"));
        var passwords = app!.Services.GetRequiredService<PasswordService>();
        Assert.True(await passwords.VerifyAsync(admin.Text("p"), AdminPassword, default));

        var team = await db.GetCollection<BsonDocument>("team").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(BootstrapService.DefaultTeamName, team.Text("n"));
        Assert.Equal(2, team.Integer("m_a"));
        Assert.Equal(2, team.Integer("m_t"));
        Assert.Equal(BootstrapService.DefaultTeamIntro, team.Text("i"));

        var memberRole = await db.GetCollection<BsonDocument>("team_role")
            .Find(new BsonDocument { ["m_o"] = "member", ["m_s"] = true }).SingleAsync();
        Assert.Equal(memberRole["_id"], team["dr"]);

        var sets = await db.GetCollection<BsonDocument>("project_set").Find(new BsonDocument { ["t"] = team["_id"], ["d"] = true }).ToListAsync();
        Assert.Single(sets);

        var relation = await db.GetCollection<BsonDocument>("team_member")
            .Find(new BsonDocument { ["team"] = team["_id"], ["user"] = admin["_id"] }).SingleAsync();
        Assert.Equal("creator", relation.Text("base_tag"));
        Assert.Equal("active", relation.Text("status"));

        var autoJoin = await db.GetCollection<BsonDocument>("site_setting")
            .Find(new BsonDocument("n", "site")).SingleAsync();
        Assert.Equal(team["_id"], autoJoin["ajt"].AsBsonArray.Single());
    }

    [Fact]
    public async Task Restart_IsIdempotentAndPreservesExistingRows()
    {
        var firstTeam = await db.GetCollection<BsonDocument>("team").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await app!.DisposeAsync();
        app = CreateApp();
        _ = app.CreateClient();

        Assert.Equal(1, await db.GetCollection<BsonDocument>("team").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(1, await db.GetCollection<BsonDocument>("user").CountDocumentsAsync(new BsonDocument("e", AdminEmail)));
        Assert.Equal(1, await db.GetCollection<BsonDocument>("site_setting").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var again = await db.GetCollection<BsonDocument>("team").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(firstTeam["_id"], again["_id"]);
    }

    [Fact]
    public async Task ExistingAdmin_IsPromotedWithoutRewritingPassword()
    {
        await app!.DisposeAsync();
        await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database);
        var passwords = new PasswordService();
        var hash = await passwords.CreateAsync("existing-password", default);
        await db.GetCollection<BsonDocument>("user").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["e"] = AdminEmail, ["n"] = "Existing",
            ["as"] = new BsonArray(), ["ns"] = "existing", ["asrch"] = new BsonArray(),
            ["ddn"] = "", ["s"] = "", ["l"] = "auto", ["t"] = "", ["a"] = "",
            ["b"] = false, ["p"] = hash, ["admin"] = false, ["c"] = DateTime.UtcNow
        });
        app = CreateApp(includePassword: false);
        _ = app.CreateClient();

        var admin = await db.GetCollection<BsonDocument>("user").Find(new BsonDocument("e", AdminEmail)).SingleAsync();
        Assert.True(admin.Flag("admin"));
        Assert.Equal(hash, admin.Text("p"));
        Assert.Equal("Existing", admin.Text("n"));
    }

    [Fact]
    public async Task ExistingTeam_PreventsSecondDefaultTeam()
    {
        await app!.DisposeAsync();
        await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database);
        await db.GetCollection<BsonDocument>("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = "ExistingTeam", ["i"] = "",
            ["m_a"] = 1, ["m_t"] = 2, ["u"] = 100000, ["m_uc"] = 0
        });
        app = CreateApp();
        _ = app.CreateClient();

        Assert.Equal(1, await db.GetCollection<BsonDocument>("team").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await db.GetCollection<BsonDocument>("project_set").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}

[Collection("local-services")]
public sealed class LegacyDiagnosticsTests(LocalServices services)
{
    [Fact]
    public async Task UrlListAndDiagnosticRoutesAreRegistered()
    {
        var urls = await services.Http.GetAsync("/urls");
        Assert.Equal(HttpStatusCode.OK, urls.StatusCode);
        Assert.Contains("/ping", await urls.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, (await services.Http.GetAsync("/error")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await services.Http.GetAsync("/warning")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await services.Http.GetAsync("/docs/missing.html")).StatusCode);
    }
}
