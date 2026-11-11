using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure.Migrations;

namespace Shoreline.Tests;

/// <summary>
/// Focused tests for the legacy MongoDB migration chain (runner + m0001..m0009
/// equivalents). Uses an isolated database per test class instance on the
/// loopback-only test Mongo.
/// </summary>
public sealed class MigrationsTests : IAsyncLifetime
{
    private readonly string _database = "shoreline-migrations-" + Guid.NewGuid().ToString("N")[..12];
    private MongoClient _client = null!;
    private IMongoDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _client = new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}");
        await _client.DropDatabaseAsync(_database);
        _db = _client.GetDatabase(_database);
    }

    public async Task DisposeAsync()
    {
        try { await _client.DropDatabaseAsync(_database); }
        catch (Exception) { /* best effort cleanup */ }
    }

    private LegacyMigrationRunner Runner(IEnumerable<LegacyMigration>? migrations = null) =>
        new(_db, migrations ?? LegacyMigrationRunner.DefaultMigrations());

    private static async Task<long> CountAsync(IMongoDatabase db, string collection) =>
        await db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    private static async Task<HashSet<string>> IndexNamesAsync(IMongoDatabase db, string collection)
    {
        try
        {
            var indexes = await (await db.GetCollection<BsonDocument>(collection)
                .Indexes.ListAsync()).ToListAsync();
            return indexes
                .Select(i => i.TryGetValue("name", out var name) && name.IsString ? name.AsString : "?")
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (MongoCommandException ex) when (ex.Code is 26)
        {
            return [];
        }
    }

    private static async Task<Dictionary<string, long>> CollectionCountsAsync(IMongoDatabase db)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in await (await db.ListCollectionNamesAsync()).ToListAsync())
            counts[name] = await CountAsync(db, name);
        return counts;
    }

    private static async Task<Dictionary<string, HashSet<string>>> IndexCatalogAsync(IMongoDatabase db)
    {
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in await (await db.ListCollectionNamesAsync()).ToListAsync())
            catalog[name] = await IndexNamesAsync(db, name);
        return catalog;
    }

    private sealed class FailingMigration : LegacyMigration
    {
        public override string Version => "0001";
        public override string Name => "failing-test-migration";
        public override string Description => "Always throws from UpAsync.";
        public override Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
            throw new MigrationException("boom");
    }

    [Fact]
    public async Task FreshApply_AppliesInOrder_AndSecondApplyIsNoop()
    {
        var runner = Runner();

        var before = await runner.CheckAsync();
        Assert.Equal(10, before.Count);
        Assert.Equal(
            new[] { "0000", "0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008", "0009" },
            before.Select(m => m.Version));

        var applied = await runner.ApplyAsync();
        Assert.Equal(before.Select(m => m.Version), applied.Select(m => m.Version));

        var statuses = await runner.StatusAsync();
        Assert.All(statuses, s => Assert.True(s.Applied));
        Assert.Equal(10, await CountAsync(_db, LegacyMigrationRunner.RecordCollection));

        var again = await runner.ApplyAsync();
        Assert.Empty(again);
        Assert.Equal(10, await CountAsync(_db, LegacyMigrationRunner.RecordCollection));

        Assert.Empty(await runner.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_EmptyAfterApply_NonEmptyBefore()
    {
        var runner = Runner();
        Assert.NotEmpty(await runner.CheckAsync());
        await runner.ApplyAsync();
        Assert.Empty(await runner.CheckAsync());
    }

    [Fact]
    public async Task DryRunAsync_WritesNothing()
    {
        // Legacy-shaped seed that real migrations would change on apply.
        await _db.GetCollection<BsonDocument>("file").InsertManyAsync([
            new BsonDocument { ["t"] = 2 }, // m0002 backfill target
            new BsonDocument { ["t"] = 2, ["th"] = 7, ["the"] = "x", ["tht"] = "y" },
            new BsonDocument { ["t"] = 1 },
        ]);
        await _db.GetCollection<BsonDocument>("project").InsertOneAsync(
            new BsonDocument { ["st"] = 0, ["stv"] = 0, ["ouv"] = 0 });
        await _db.GetCollection<BsonDocument>("term").InsertOneAsync(
            new BsonDocument { ["name"] = "DryRun Probe" });

        var countsBefore = await CollectionCountsAsync(_db);
        var indexesBefore = await IndexCatalogAsync(_db);

        var runner = Runner();
        var pending = await runner.DryRunAsync();
        Assert.NotEmpty(pending);

        var countsAfter = await CollectionCountsAsync(_db);
        var indexesAfter = await IndexCatalogAsync(_db);

        Assert.Equal(countsBefore, countsAfter);
        Assert.Equal(indexesBefore.Keys, indexesAfter.Keys);
        foreach (var name in indexesBefore.Keys)
            Assert.Equal(indexesBefore[name], indexesAfter[name]);

        // Dry-run must not create the ledger or the lock, and must not touch data.
        Assert.DoesNotContain(LegacyMigrationRunner.RecordCollection, countsAfter.Keys);
        Assert.DoesNotContain(LegacyMigrationRunner.LockCollection, countsAfter.Keys);
        var untouched = await _db.GetCollection<BsonDocument>("file")
            .Find(Builders<BsonDocument>.Filter.Eq("t", 2) & Builders<BsonDocument>.Filter.Exists("th", false))
            .FirstOrDefaultAsync();
        Assert.NotNull(untouched);
    }

    [Fact]
    public async Task ApplyAsync_Failure_RecordsNothing_AndRetrySucceeds()
    {
        var runner = Runner([new M0000Baseline(), new FailingMigration(), new M0002ThumbnailStatus()]);

        var failure = await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync());
        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);

        // The baseline recorded; the failed version did not.
        Assert.Equal(1, await CountAsync(_db, LegacyMigrationRunner.RecordCollection));
        var rows = await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.Equal("0000", rows[0]["v"].AsString);

        // Retry with a fixed chain picks up exactly where it stopped.
        var retry = Runner([new M0000Baseline(), new M0001FileListIndex(), new M0002ThumbnailStatus()]);
        var applied = await retry.ApplyAsync();
        Assert.Equal(["0001", "0002"], applied.Select(m => m.Version));
        Assert.Equal(3, await CountAsync(_db, LegacyMigrationRunner.RecordCollection));
        Assert.Empty(await retry.CheckAsync());
    }

    [Fact]
    public async Task ApplyAsync_DuplicateVersionsThrow()
    {
        Assert.Throws<MigrationException>(() =>
            Runner([new M0001FileListIndex(), new M0001FileListIndex()]));
    }

    [Fact]
    public async Task M0002_AppliesThumbnailBackfill()
    {
        await _db.GetCollection<BsonDocument>("file").InsertManyAsync([
            new BsonDocument { ["t"] = 2 },
            new BsonDocument { ["t"] = 2, ["th"] = 7, ["the"] = "keep", ["tht"] = "keep" },
            new BsonDocument { ["t"] = 1 },
        ]);

        var applied = await Runner([new M0002ThumbnailStatus()]).ApplyAsync();
        Assert.Single(applied);

        var files = _db.GetCollection<BsonDocument>("file");
        var backfilled = await files.Find(
            Builders<BsonDocument>.Filter.Eq("t", 2) & Builders<BsonDocument>.Filter.Exists("th")).ToListAsync();
        Assert.Equal(2, backfilled.Count);
        var fresh = backfilled.Single(d => !d.TryGetValue("the", out var kept) || kept.AsString != "keep");
        Assert.Equal(0, fresh["th"].ToInt64());
        Assert.Equal(string.Empty, fresh["the"].AsString);
        var untouched = await files.Find(Builders<BsonDocument>.Filter.Eq("t", 1)).SingleAsync();
        Assert.False(untouched.Contains("th"));
    }

    [Fact]
    public async Task M0004_MigratesLegacyRelations_AndRepairsOwner()
    {
        var userId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var teamId = ObjectId.GenerateNewId();
        var roleId = ObjectId.GenerateNewId();
        var teamRoleId = ObjectId.GenerateNewId();

        await _db.GetCollection<BsonDocument>("user").InsertOneAsync(
            new BsonDocument { ["_id"] = userId, ["n"] = "Alice" });
        await _db.GetCollection<BsonDocument>("project_role").InsertOneAsync(
            new BsonDocument { ["_id"] = roleId, ["m_o"] = "creator" });
        await _db.GetCollection<BsonDocument>("team_role").InsertOneAsync(
            new BsonDocument { ["_id"] = teamRoleId, ["m_o"] = "creator" });
        await _db.GetCollection<BsonDocument>("project").InsertOneAsync(
            new BsonDocument { ["_id"] = projectId, ["st"] = 0, ["stv"] = 0, ["ouv"] = 0 });
        await _db.GetCollection<BsonDocument>("project_user_relation").InsertOneAsync(
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = roleId });
        await _db.GetCollection<BsonDocument>("team").InsertOneAsync(
            new BsonDocument { ["_id"] = teamId });
        await _db.GetCollection<BsonDocument>("team_user_relation").InsertOneAsync(
            new BsonDocument { ["g"] = teamId, ["u"] = userId, ["r"] = teamRoleId });

        var applied = await Runner([new M0004IdentityMembers()]).ApplyAsync();
        Assert.Single(applied);

        var members = _db.GetCollection<BsonDocument>("project_member");
        var member = await members.Find(
            Builders<BsonDocument>.Filter.Eq("project", projectId)).SingleAsync();
        Assert.Equal("active", member["status"].AsString);
        Assert.Equal(userId, member["user"].AsObjectId);
        Assert.Contains("creator", member["tags"].AsBsonArray.Select(t => t.AsString));

        var project = await _db.GetCollection<BsonDocument>("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", projectId)).SingleAsync();
        Assert.Equal(userId, project["ou"].AsObjectId);

        var teamMember = await _db.GetCollection<BsonDocument>("team_member")
            .Find(Builders<BsonDocument>.Filter.Eq("team", teamId)).SingleAsync();
        Assert.Equal("active", teamMember["status"].AsString);

        var summary = await _db.GetCollection<BsonDocument>("identity_migration_report")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "identity-v1-summary")).SingleAsync();
        Assert.Equal(0, summary["summary"].AsBsonDocument["owner_issue_count"].ToInt64());
    }
}
