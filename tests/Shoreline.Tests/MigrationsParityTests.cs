using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure.Migrations;

namespace Shoreline.Tests;

/// <summary>
/// Parity boundary tests for the legacy migration chain: empty collections,
/// pre-existing targets, reruns, legacy-shaped documents, Python-written
/// ledger rows (no <c>runner</c> field), &gt;100-document batches, lock
/// contention and verify-failure paths. Complements the happy-path coverage
/// in <c>MigrationsTests</c>.
/// </summary>
public sealed class MigrationsParityTests : IAsyncLifetime
{
    private readonly string _database = "shoreline-parity-" + Guid.NewGuid().ToString("N")[..12];
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

    private static BsonDocument PythonLedgerRow(string version, string name) => new()
    {
        ["v"] = version,
        ["n"] = name,
        ["at"] = DateTime.UtcNow,
        ["d"] = 12L,
        ["c"] = "deadbeef-python-oracle",
    };

    // ---- runner / ledger interop ----

    [Fact]
    public async Task PythonLedgerRows_AreTrusted_AndApplyIsNoop()
    {
        var versions = new[] { "0000", "0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008", "0009" };
        await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .InsertManyAsync(versions.Select(v => PythonLedgerRow(v, $"name-{v}")));

        var runner = Runner();
        Assert.Empty(await runner.CheckAsync());
        Assert.All(await runner.StatusAsync(), s => Assert.True(s.Applied));
        Assert.Empty(await runner.ApplyAsync());
        Assert.Equal(10, await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task PythonRow_BogusChecksum_DoesNotTripTamperDetection()
    {
        // Python rows predate the C# checksum scheme and are accepted as-is.
        await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .InsertOneAsync(PythonLedgerRow("0004", "identity_members"));
        var statuses = await Runner([new M0004IdentityMembers()]).StatusAsync();
        Assert.True(statuses.Single().Applied);
    }

    [Fact]
    public async Task CsharpRow_TamperDetected()
    {
        await Runner([new M0001FileListIndex()]).ApplyAsync();
        var records = _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection);
        await records.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("v", "0001"),
            Builders<BsonDocument>.Update.Set("c", "tampered"));
        await Assert.ThrowsAsync<MigrationException>(() => Runner([new M0001FileListIndex()]).StatusAsync());
    }

    [Fact]
    public async Task LockContention_ApplyThrows_AndWritesNothing()
    {
        await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.LockCollection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = LegacyMigrationRunner.LockId,
            ["holder"] = "someone-else",
            ["expires_at"] = DateTime.UtcNow.AddMinutes(29),
        });
        var failure = await Assert.ThrowsAsync<MigrationException>(() => Runner().ApplyAsync());
        Assert.Contains("migration lock", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task EmptyDatabase_FullApply_Succeeds_AndRerunIsNoop()
    {
        var applied = await Runner().ApplyAsync();
        Assert.Equal(10, applied.Count);
        Assert.Empty(await Runner().ApplyAsync());
        Assert.Empty(await Runner().CheckAsync());
    }

    // ---- m0002 ----

    [Fact]
    public async Task M0002_EmptyCollection_Noop()
    {
        var applied = await Runner([new M0002ThumbnailStatus()]).ApplyAsync();
        Assert.Single(applied);
        Assert.True(await new M0002ThumbnailStatus().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0002_Rerun_Idempotent_AndDown_ClearsThumbnailFields()
    {
        var runner = Runner([new M0002ThumbnailStatus()]);
        await runner.ApplyAsync();
        Assert.Empty(await runner.ApplyAsync());

        var migration = new M0002ThumbnailStatus();
        await migration.DownAsync(_db, CancellationToken.None);
        var files = _db.GetCollection<BsonDocument>("file");
        Assert.Equal(0, await files.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Exists("th"),
                Builders<BsonDocument>.Filter.Exists("the"),
                Builders<BsonDocument>.Filter.Exists("tht"))));
    }

    [Fact]
    public async Task M0002_MissingFields_OldDocs_Backfilled()
    {
        await _db.GetCollection<BsonDocument>("file").InsertManyAsync([
            new BsonDocument { ["t"] = 2 },                       // no th/the at all
            new BsonDocument { ["t"] = 2, ["th"] = 3 },           // partial legacy row
            new BsonDocument { ["t"] = 1 },                       // non-image untouched
        ]);
        await Runner([new M0002ThumbnailStatus()]).ApplyAsync();
        var images = await _db.GetCollection<BsonDocument>("file")
            .Find(Builders<BsonDocument>.Filter.Eq("t", 2)).ToListAsync();
        Assert.Equal(2, images.Count);
        Assert.Contains(images, d => d["th"].ToInt64() == 0 && d["the"].AsString == string.Empty);
        Assert.Contains(images, d => d["th"].ToInt64() == 3);
        var other = await _db.GetCollection<BsonDocument>("file")
            .Find(Builders<BsonDocument>.Filter.Eq("t", 1)).SingleAsync();
        Assert.False(other.Contains("th"));
    }

    // ---- m0005 ----

    [Fact]
    public async Task M0005_NullExternalId_Unset_RerunStable()
    {
        var project = ObjectId.GenerateNewId();
        var user = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project,
            ["user"] = user,
            ["external_id"] = BsonNull.Value,
            ["ik"] = "x",
        });
        var runner = Runner([new M0005IdentityMemberIndexOptions()]);
        await runner.ApplyAsync();
        var doc = await _db.GetCollection<BsonDocument>("project_member")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.False(doc.Contains("external_id"));
        Assert.Empty(await runner.ApplyAsync());
        Assert.True(await new M0005IdentityMemberIndexOptions().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0005_SameKeysDivergentName_ReconciledToCanonical()
    {
        var members = _db.GetCollection<BsonDocument>("project_member");
        await members.InsertOneAsync(new BsonDocument { ["project"] = ObjectId.GenerateNewId() });
        // Legacy auto-named index carrying the canonical keys.
        await members.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                M0005IdentityMemberIndexOptions.UserIndexKeys,
                new CreateIndexOptions { Unique = true }));
        await Runner([new M0005IdentityMemberIndexOptions()]).ApplyAsync();
        Assert.True(await new M0005IdentityMemberIndexOptions().VerifyAsync(_db, CancellationToken.None));
    }

    // ---- m0006 ----

    [Fact]
    public async Task M0006_EmptyCollections_CreatesIndexes()
    {
        await Runner([new M0006SearchProjections()]).ApplyAsync();
        Assert.True(await new M0006SearchProjections().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0006_LargeBatch_BackfillsAll()
    {
        const int count = 650; // exceeds the 500-document write batch
        await _db.GetCollection<BsonDocument>("user").InsertManyAsync(
            Enumerable.Range(0, count).Select(i => new BsonDocument
            {
                ["n"] = $"User {i} ",
                ["aliases"] = new BsonArray { $" Alias{i} ", string.Empty },
            }));
        await Runner([new M0006SearchProjections()]).ApplyAsync();
        var users = _db.GetCollection<BsonDocument>("user");
        Assert.Equal(0, await users.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("ns"))));
        Assert.Equal(0, await users.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("asrch"))));
        var sample = await users.Find(Builders<BsonDocument>.Filter.Eq("n", "User 7 ")).SingleAsync();
        Assert.Equal("user 7", sample["ns"].AsString);
        Assert.Equal(["alias7"], sample["asrch"].AsBsonArray.Select(v => v.AsString));
        Assert.True(await new M0006SearchProjections().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0006_Verify_DetectsStaleProjection()
    {
        await _db.GetCollection<BsonDocument>("user").InsertOneAsync(
            new BsonDocument { ["n"] = "Alice" });
        var migration = new M0006SearchProjections();
        await Runner([migration]).ApplyAsync();
        Assert.True(await migration.VerifyAsync(_db, CancellationToken.None));
        await _db.GetCollection<BsonDocument>("user").UpdateOneAsync(
            FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("ns", "stale"));
        Assert.False(await migration.VerifyAsync(_db, CancellationToken.None));
    }

    // ---- m0007 ----

    [Fact]
    public async Task M0007_MissingNullAndPresentTags()
    {
        await _db.GetCollection<BsonDocument>("project_member").InsertManyAsync([
            new BsonDocument { ["ik"] = "missing" },
            new BsonDocument { ["ik"] = "null", ["tags"] = BsonNull.Value },
            new BsonDocument { ["ik"] = "present", ["tags"] = new BsonArray { "creator" } },
        ]);
        var runner = Runner([new M0007ProjectMemberTags()]);
        await runner.ApplyAsync();
        var docs = await _db.GetCollection<BsonDocument>("project_member")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.All(docs.Where(d => d["ik"].AsString != "present"),
            d => Assert.Empty(d["tags"].AsBsonArray));
        Assert.Equal(["creator"],
            docs.Single(d => d["ik"].AsString == "present")["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Empty(await runner.ApplyAsync());
    }

    // ---- m0008 ----

    [Fact]
    public async Task M0008_Rerun_DoesNotTouchFiles()
    {
        await _db.GetCollection<BsonDocument>("file").InsertOneAsync(
            new BsonDocument { ["p"] = ObjectId.GenerateNewId() });
        var runner = Runner([new M0008FileManualOrder()]);
        await runner.ApplyAsync();
        Assert.Empty(await runner.ApplyAsync());
        var doc = await _db.GetCollection<BsonDocument>("file")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.False(doc.Contains("mo"));
        Assert.True(await new M0008FileManualOrder().VerifyAsync(_db, CancellationToken.None));
    }

    // ---- m0009 ----

    [Fact]
    public async Task M0009_LegacyFn_Migrated_WithoutClobbering()
    {
        var outputs = _db.GetCollection<BsonDocument>("output");
        await outputs.InsertManyAsync([
            new BsonDocument { ["fn"] = "legacy-name" },
            new BsonDocument { ["fn"] = "spurious", ["file_name"] = "keep-me" },
            new BsonDocument { ["file_name"] = "untouched" },
        ]);
        await Runner([new M0009StorageUsageBackfill()]).ApplyAsync();
        var docs = await outputs.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.All(docs, d => Assert.False(d.Contains("fn")));
        Assert.Contains(docs, d => d["file_name"].AsString == "legacy-name");
        Assert.Contains(docs, d => d["file_name"].AsString == "keep-me");
        Assert.True(await new M0009StorageUsageBackfill().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0009_StorageTotals_OnlyMissingOrZero_Backfilled()
    {
        var keep = ObjectId.GenerateNewId();
        var missing = ObjectId.GenerateNewId();
        var zero = ObjectId.GenerateNewId();
        var nulled = ObjectId.GenerateNewId();
        var unrelated = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("file").InsertManyAsync([
            new BsonDocument { ["p"] = missing, ["fs"] = 10L },
            new BsonDocument { ["p"] = missing, ["fs"] = 5L },
            new BsonDocument { ["p"] = zero, ["fs"] = 7L },
            new BsonDocument { ["p"] = nulled, ["fs"] = 3L },
            new BsonDocument { ["p"] = keep, ["fs"] = 4L },
            new BsonDocument { ["p"] = missing, ["fs"] = 0L },   // ignored
            new BsonDocument { ["p"] = missing },                // ignored
        ]);
        await _db.GetCollection<BsonDocument>("project").InsertManyAsync([
            new BsonDocument { ["_id"] = keep, ["fs"] = 123L },
            new BsonDocument { ["_id"] = missing },
            new BsonDocument { ["_id"] = zero, ["fs"] = 0L },
            new BsonDocument { ["_id"] = nulled, ["fs"] = BsonNull.Value },
            new BsonDocument { ["_id"] = unrelated, ["fs"] = 9L },
        ]);
        await Runner([new M0009StorageUsageBackfill()]).ApplyAsync();
        var projects = _db.GetCollection<BsonDocument>("project");
        BsonDocument ById(ObjectId id) => projects
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync().GetAwaiter().GetResult();
        Assert.Equal(123L, ById(keep)["fs"].ToInt64());
        Assert.Equal(15L, ById(missing)["fs"].ToInt64());
        Assert.Equal(7L, ById(zero)["fs"].ToInt64());
        Assert.Equal(3L, ById(nulled)["fs"].ToInt64());
        Assert.Equal(9L, ById(unrelated)["fs"].ToInt64());
        Assert.True(await new M0009StorageUsageBackfill().VerifyAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task M0009_LargeBatch_TotalIsExact()
    {
        var project = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("file").InsertManyAsync(
            Enumerable.Range(0, 150).Select(_ => new BsonDocument { ["p"] = project, ["fs"] = 2L }));
        await _db.GetCollection<BsonDocument>("project").InsertOneAsync(
            new BsonDocument { ["_id"] = project });
        await Runner([new M0009StorageUsageBackfill()]).ApplyAsync();
        var doc = await _db.GetCollection<BsonDocument>("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", project)).SingleAsync();
        Assert.Equal(300L, doc["fs"].ToInt64());
    }

    // ---- m0004 boundaries ----

    private async Task<(ObjectId User, ObjectId Project, ObjectId Role)> SeedIdentityBasicsAsync(
        string roleCode = "creator", string userName = "Alice")
    {
        var userId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var roleId = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("user").InsertOneAsync(
            new BsonDocument { ["_id"] = userId, ["n"] = userName });
        await _db.GetCollection<BsonDocument>("project_role").InsertOneAsync(
            new BsonDocument { ["_id"] = roleId, ["m_o"] = roleCode });
        await _db.GetCollection<BsonDocument>("project").InsertOneAsync(
            new BsonDocument { ["_id"] = projectId, ["st"] = 0, ["stv"] = 0, ["ouv"] = 0 });
        return (userId, projectId, roleId);
    }

    [Fact]
    public async Task M0004_EmptyDatabase_ApplyAndVerify()
    {
        var runner = Runner([new M0004IdentityMembers()]);
        Assert.Single(await runner.ApplyAsync());
        Assert.True(await new M0004IdentityMembers().VerifyAsync(_db, CancellationToken.None));
        var summary = await _db.GetCollection<BsonDocument>("identity_migration_report")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "identity-v1-summary")).SingleAsync();
        Assert.Equal(0, summary["summary"].AsBsonDocument["owner_issue_count"].ToInt64());
    }

    [Fact]
    public async Task M0004_DanglingRelation_Skipped_DuplicateRelation_Merged()
    {
        var (userId, projectId, roleId) = await SeedIdentityBasicsAsync();
        var relations = _db.GetCollection<BsonDocument>("project_user_relation");
        var firstId = ObjectId.GenerateNewId();
        await relations.InsertManyAsync([
            new BsonDocument { ["_id"] = firstId, ["g"] = projectId, ["u"] = userId, ["r"] = roleId },
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = roleId },
            new BsonDocument { ["g"] = ObjectId.GenerateNewId(), ["u"] = userId, ["r"] = roleId },
        ]);
        await Runner([new M0004IdentityMembers()]).ApplyAsync();

        var member = await _db.GetCollection<BsonDocument>("project_member")
            .Find(Builders<BsonDocument>.Filter.Eq("project", projectId)).SingleAsync();
        Assert.Equal("active", member["status"].AsString);
        Assert.Equal(["creator"], member["tags"].AsBsonArray.Select(t => t.AsString));
        var summary = await _db.GetCollection<BsonDocument>("identity_migration_report")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "identity-v1-summary")).SingleAsync()
            .ContinueWith(t => t.Result["summary"].AsBsonDocument);
        Assert.Equal(1, summary["project_relation_duplicate_count"].ToInt64());
        Assert.Equal(3, summary["project_relation_count"].ToInt64());
        var project = await _db.GetCollection<BsonDocument>("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", projectId)).SingleAsync();
        Assert.Equal(userId, project["ou"].AsObjectId); // single creator candidate repaired
    }

    [Fact]
    public async Task M0004_MissingAndUnmappedRoles_CountedLikeOracle()
    {
        // Mirrors Python m0004: a missing role reference counts twice
        // (_record_missing_role + unmapped branch), a known-but-unmapped
        // custom role counts once.
        var (userId, projectId, _) = await SeedIdentityBasicsAsync();
        var customRoleId = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("project_role").InsertOneAsync(
            new BsonDocument { ["_id"] = customRoleId, ["m_o"] = "custom_x" });
        await _db.GetCollection<BsonDocument>("project_user_relation").InsertManyAsync([
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = ObjectId.GenerateNewId() },
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = customRoleId },
        ]);
        await Runner([new M0004IdentityMembers()]).ApplyAsync();
        var summary = await _db.GetCollection<BsonDocument>("identity_migration_report")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "identity-v1-summary")).SingleAsync();
        Assert.Equal(3, summary["summary"].AsBsonDocument["custom_role_issue_count"].ToInt64());
    }

    [Fact]
    public async Task M0004_OwnerConflict_LeavesOwnerAlone_AndFailsVerify()
    {
        var (userId, projectId, roleId) = await SeedIdentityBasicsAsync();
        var otherId = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("user").InsertOneAsync(
            new BsonDocument { ["_id"] = otherId, ["n"] = "Bob" });
        await _db.GetCollection<BsonDocument>("project_user_relation").InsertManyAsync([
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = roleId },
            new BsonDocument { ["g"] = projectId, ["u"] = otherId, ["r"] = roleId },
        ]);
        // Two creator candidates: verify fails, so the run aborts without a
        // ledger row (same as Python's run_pending), leaving data + reports.
        var runner = Runner([new M0004IdentityMembers()]);
        await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync());
        Assert.Equal(0, await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.False(await new M0004IdentityMembers().VerifyAsync(_db, CancellationToken.None));
        var summary = await _db.GetCollection<BsonDocument>("identity_migration_report")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "identity-v1-summary")).SingleAsync();
        Assert.True(summary["summary"].AsBsonDocument["owner_issue_count"].ToInt64() > 0);
    }

    [Fact]
    public async Task M0004_JoinProcess_PendingInviteBecomesInvited_PendingApplicationDoesNot()
    {
        var (userId, projectId, _) = await SeedIdentityBasicsAsync();
        var invitedId = ObjectId.GenerateNewId();
        var applicantId = ObjectId.GenerateNewId();
        await _db.GetCollection<BsonDocument>("user").InsertManyAsync([
            new BsonDocument { ["_id"] = invitedId, ["n"] = "Invited" },
            new BsonDocument { ["_id"] = applicantId, ["n"] = "Applicant" },
        ]);
        await _db.GetCollection<BsonDocument>("invitation").InsertOneAsync(new BsonDocument
        {
            ["g"] = projectId, ["u"] = invitedId, ["s"] = 1,
        });
        await _db.GetCollection<BsonDocument>("application").InsertOneAsync(new BsonDocument
        {
            ["g"] = projectId, ["u"] = applicantId, ["s"] = 1,
        });
        await Runner([new M0004IdentityMembers()]).ApplyAsync();
        var members = _db.GetCollection<BsonDocument>("project_member");
        var invited = await members.Find(Builders<BsonDocument>.Filter.Eq("user", invitedId))
            .SingleAsync();
        Assert.Equal("invited", invited["status"].AsString);
        Assert.Equal(0, await members.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("user", applicantId)));
    }

    [Fact]
    public async Task M0004_UpTwice_Converges_AndVerifies()
    {
        var (userId, projectId, roleId) = await SeedIdentityBasicsAsync();
        await _db.GetCollection<BsonDocument>("project_user_relation").InsertOneAsync(
            new BsonDocument { ["g"] = projectId, ["u"] = userId, ["r"] = roleId });
        var migration = new M0004IdentityMembers();
        await migration.UpAsync(_db, CancellationToken.None);
        await migration.UpAsync(_db, CancellationToken.None); // crash-retry path
        Assert.True(await migration.VerifyAsync(_db, CancellationToken.None));
        Assert.Equal(1, await _db.GetCollection<BsonDocument>("project_member")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    // ---- rollback ----

    [Fact]
    public async Task Rollback_ReversibleMigrations_RemovesRows_AndUnsetsFields()
    {
        await _db.GetCollection<BsonDocument>("file").InsertOneAsync(
            new BsonDocument { ["t"] = 2 });
        var chain = new LegacyMigration[] { new M0000Baseline(), new M0001FileListIndex(), new M0002ThumbnailStatus() };
        await Runner(chain).ApplyAsync();
        Assert.Equal(3, await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        var rolledBack = await Runner(chain).RollbackAsync("0000");
        Assert.Equal(["0002", "0001"], rolledBack.Select(m => m.Version));
        Assert.Equal(1, await _db.GetCollection<BsonDocument>(LegacyMigrationRunner.RecordCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var file = await _db.GetCollection<BsonDocument>("file")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.False(file.Contains("th"));
    }
}
