using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>
/// Executes versioned MongoDB migrations with a ledger, a distributed lease
/// and dry-run/check gates. Ports Python's <c>app/migrations/runner.py</c>.
///
/// Ledger: collection <c>migration_record</c>, one document per applied
/// version (<c>v</c>, <c>n</c>, <c>at</c>, <c>d</c> ms, <c>c</c> checksum)
/// plus a unique index on <c>v</c>. Lock: collection <c>migration_lock</c>,
/// document <c>_id: "migrate"</c> with a 30-minute <c>holder</c> lease.
/// </summary>
public sealed class LegacyMigrationRunner
{
    public const string RecordCollection = "migration_record";
    public const string LockCollection = "migration_lock";
    public const string LockId = "migrate";
    public const string RunnerTag = "csharp";

    /// <summary>Matches Python's <c>LOCK_LEASE</c> of 30 minutes.</summary>
    public static readonly TimeSpan LockLease = TimeSpan.FromMinutes(30);

    private readonly IMongoDatabase _database;
    private readonly IReadOnlyList<LegacyMigration> _migrations;
    private readonly ILogger? _log;

    public LegacyMigrationRunner(
        IMongoDatabase database,
        IEnumerable<LegacyMigration> migrations,
        ILogger? log = null)
    {
        _database = database;
        var ordered = migrations.OrderBy(m => m.Version, StringComparer.Ordinal).ToArray();
        var duplicates = ordered.GroupBy(m => m.Version, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new MigrationException(
                $"Migration versions must be unique: {string.Join(", ", duplicates)}");
        _migrations = ordered;
        _log = log;
    }

    /// <summary>The full legacy chain, 0000 (baseline) through 0009.</summary>
    public static IReadOnlyList<LegacyMigration> DefaultMigrations() =>
    [
        new M0000Baseline(),
        new M0001FileListIndex(),
        new M0002ThumbnailStatus(),
        new M0003AdminFileEditTimeIndex(),
        new M0004IdentityMembers(),
        new M0005IdentityMemberIndexOptions(),
        new M0006SearchProjections(),
        new M0007ProjectMemberTags(),
        new M0008FileManualOrder(),
        new M0009StorageUsageBackfill(),
    ];

    public async Task EnsureMetadataIndexesAsync(CancellationToken ct)
    {
        var records = _database.GetCollection<BsonDocument>(RecordCollection);
        try
        {
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("v"),
                    new CreateIndexOptions { Name = "migration_version_unique", Unique = true, Background = true }),
                cancellationToken: ct);
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // An equivalent unique index already exists under another name.
        }
    }

    private async Task<Dictionary<string, BsonDocument>> AppliedRecordsAsync(CancellationToken ct)
    {
        var records = _database.GetCollection<BsonDocument>(RecordCollection);
        List<BsonDocument> rows;
        try
        {
            rows = await records.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        }
        catch (MongoCommandException ex) when (ex.Code is 26)
        {
            return new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        }
        var result = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.TryGetValue("v", out var version) && version.IsString)
                result[version.AsString] = row;
        }
        return result;
    }

    /// <summary>
    /// Migration status with tamper detection. Records written by the Python
    /// runner predate the C# checksum scheme and are accepted as applied
    /// (mirroring Python's own acceptance of pre-identity checksums); records
    /// written by this runner must match <see cref="LegacyMigration.Checksum"/>.
    /// </summary>
    public async Task<IReadOnlyList<MigrationStatus>> StatusAsync(CancellationToken ct = default)
    {
        var applied = await AppliedRecordsAsync(ct);
        var result = new List<MigrationStatus>(_migrations.Count);
        foreach (var migration in _migrations)
        {
            applied.TryGetValue(migration.Version, out var record);
            if (record is not null &&
                record.TryGetValue("c", out var stored) && stored.IsString &&
                !string.IsNullOrEmpty(stored.AsString) &&
                string.Equals(record.TryGetValue("runner", out var runner) && runner.IsString
                    ? runner.AsString : null, RunnerTag, StringComparison.Ordinal) &&
                !string.Equals(stored.AsString, migration.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationException(
                    $"Applied migration {migration.Version} has been modified");
            }
            DateTime? at = null;
            if (record is not null && record.TryGetValue("at", out var atValue) && atValue.IsBsonDateTime)
                at = atValue.ToUniversalTime();
            long? duration = null;
            if (record is not null && record.TryGetValue("d", out var dValue) && dValue.IsNumeric)
                duration = dValue.ToInt64();
            result.Add(new MigrationStatus(
                migration.Version, migration.Name, migration.Level,
                record is not null, at, duration));
        }
        return result;
    }

    /// <summary>Discovered migrations with no ledger row, in version order.</summary>
    public async Task<IReadOnlyList<LegacyMigration>> PendingAsync(
        string? level = "AUTO", CancellationToken ct = default)
    {
        var applied = await AppliedRecordsAsync(ct);
        var selected = string.IsNullOrWhiteSpace(level) ? null : level.ToUpperInvariant();
        return _migrations
            .Where(m => !applied.ContainsKey(m.Version) &&
                (selected is null or "ALL" || string.Equals(m.Level, selected, StringComparison.Ordinal)))
            .ToArray();
    }

    /// <summary>
    /// Deploy gate: every pending migration regardless of level, without
    /// touching the database. Mirrors <c>manage.py migrate --check</c>.
    /// </summary>
    public async Task<IReadOnlyList<LegacyMigration>> CheckAsync(CancellationToken ct = default)
    {
        await StatusAsync(ct);
        return await PendingAsync("ALL", ct);
    }

    /// <summary>
    /// Report what would run without changing the database: no lock, no
    /// ledger index, no writes. Mirrors <c>--dry-run</c>.
    /// </summary>
    public async Task<IReadOnlyList<LegacyMigration>> DryRunAsync(
        string? level = "AUTO", CancellationToken ct = default)
    {
        await StatusAsync(ct);
        return await PendingAsync(level, ct);
    }

    /// <summary>
    /// Apply pending migrations under the lease. A failure stops the run
    /// without recording the version, so the next start retries it.
    /// Mirrors <c>run_pending</c>.
    /// </summary>
    public async Task<IReadOnlyList<AppliedMigration>> ApplyAsync(
        string? level = "AUTO", CancellationToken ct = default)
    {
        await StatusAsync(ct);
        var migrations = await PendingAsync(level, ct);
        if (migrations.Count == 0) return [];

        var blocking = (await PendingAsync("ALL", ct))
            .Where(m => string.Compare(m.Version, migrations[^1].Version, StringComparison.Ordinal) < 0 &&
                !migrations.Contains(m))
            .ToArray();
        if (blocking.Length > 0)
        {
            throw new MigrationException(
                "Cannot apply migrations out of order; these earlier migrations are still pending and " +
                $"are excluded by --level {level}: " +
                string.Join(", ", blocking.Select(m => $"{m.Version} ({m.Level})")));
        }

        await EnsureMetadataIndexesAsync(ct);
        var holder = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        if (!await AcquireLockAsync(holder, ct))
        {
            throw new MigrationException(
                $"Another instance holds the migration lock. If no migration is running, the previous run left " +
                $"a lease that expires within {(int)LockLease.TotalMinutes} minutes; wait for it or clear the " +
                $"'{LockId}' document in the {LockCollection} collection.");
        }
        try
        {
            // Re-read after taking the lock in case another runner finished first.
            var applied = new List<AppliedMigration>();
            foreach (var migration in await PendingAsync(level, ct))
            {
                _log?.LogInformation("Applying migration {Version} {Name}", migration.Version, migration.Name);
                var started = DateTime.UtcNow;
                await migration.UpAsync(_database, ct);
                if (!await migration.VerifyAsync(_database, ct))
                    throw new MigrationException($"Migration {migration.Version} verification failed");
                var durationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                await RecordAppliedAsync(migration, durationMs, ct);
                applied.Add(new AppliedMigration(migration.Version, migration.Name, durationMs));
                _log?.LogInformation("Applied migration {Version} in {Duration}ms", migration.Version, durationMs);
                // The lease is only renewed between migrations, so a future long
                // run can be taken over mid-way (mirrors the Python runner warning).
                if (durationMs / 1000.0 > LockLease.TotalSeconds * 0.6)
                    _log?.LogWarning(
                        "Migration {Version} took {Duration}ms, more than 60% of the {Lease} lease. " +
                        "The lease is only renewed between migrations, so a future long run can be taken " +
                        "over mid-way; raise the lock lease or split the migration into smaller steps",
                        migration.Version, durationMs, LockLease);
                await RenewLockAsync(holder, ct);
            }
            return applied;
        }
        finally
        {
            await ReleaseLockAsync(holder, ct);
        }
    }

    /// <summary>
    /// Roll back applied migrations newer than <paramref name="targetVersion"/>
    /// in reverse order. Mirrors <c>migrate-rollback --to</c>.
    /// </summary>
    public async Task<IReadOnlyList<LegacyMigration>> RollbackAsync(
        string targetVersion, CancellationToken ct = default)
    {
        var known = _migrations.Select(m => m.Version).ToArray();
        if (!known.Contains(targetVersion, StringComparer.Ordinal))
        {
            throw new MigrationException(
                $"Unknown target version '{targetVersion}'; choose one of: {string.Join(", ", known)}");
        }
        await EnsureMetadataIndexesAsync(ct);
        var holder = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        if (!await AcquireLockAsync(holder, ct))
            throw new MigrationException("Another instance is executing migrations");
        try
        {
            var applied = await AppliedRecordsAsync(ct);
            var targets = _migrations
                .Where(m => applied.ContainsKey(m.Version) &&
                    string.Compare(m.Version, targetVersion, StringComparison.Ordinal) > 0)
                .OrderByDescending(m => m.Version, StringComparer.Ordinal)
                .ToArray();
            foreach (var migration in targets)
            {
                _log?.LogWarning("Rolling back migration {Version} {Name}", migration.Version, migration.Name);
                await migration.DownAsync(_database, ct);
                await _database.GetCollection<BsonDocument>(RecordCollection)
                    .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("v", migration.Version), ct);
                await RenewLockAsync(holder, ct);
            }
            return targets;
        }
        finally
        {
            await ReleaseLockAsync(holder, ct);
        }
    }

    private async Task RecordAppliedAsync(
        LegacyMigration migration, long durationMs, CancellationToken ct)
    {
        var records = _database.GetCollection<BsonDocument>(RecordCollection);
        try
        {
            await records.InsertOneAsync(new BsonDocument
            {
                ["v"] = migration.Version,
                ["n"] = migration.Name,
                ["at"] = DateTime.UtcNow,
                ["d"] = durationMs,
                ["c"] = migration.Checksum,
                ["runner"] = RunnerTag,
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (IsDuplicateKey(ex))
        {
            var existing = await records
                .Find(Builders<BsonDocument>.Filter.Eq("v", migration.Version))
                .FirstOrDefaultAsync(ct);
            if (existing is null ||
                !string.Equals(existing.TryGetValue("c", out var checksum) && checksum.IsString
                    ? checksum.AsString : null, migration.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationException(
                    $"Migration {migration.Version} was concurrently recorded with different content", ex);
            }
        }
    }

    private async Task<bool> AcquireLockAsync(string holder, CancellationToken ct)
    {
        var locks = _database.GetCollection<BsonDocument>(LockCollection);
        var now = DateTime.UtcNow;
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", LockId),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Lte("expires_at", now),
                Builders<BsonDocument>.Filter.Exists("expires_at", false),
                Builders<BsonDocument>.Filter.Eq("holder", holder)));
        var update = Builders<BsonDocument>.Update
            .Set("holder", holder)
            .Set("expires_at", now + LockLease);
        try
        {
            var result = await locks.FindOneAndUpdateAsync(
                filter, update,
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After,
                },
                ct);
            return result is not null &&
                result.TryGetValue("holder", out var current) &&
                current.IsString && current.AsString == holder;
        }
        catch (Exception ex) when (IsDuplicateKey(ex))
        {
            // A non-expired lease belongs to another process.
            return false;
        }
    }

    private async Task RenewLockAsync(string holder, CancellationToken ct)
    {
        var locks = _database.GetCollection<BsonDocument>(LockCollection);
        var result = await locks.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", LockId),
                Builders<BsonDocument>.Filter.Eq("holder", holder)),
            Builders<BsonDocument>.Update.Set("expires_at", DateTime.UtcNow + LockLease),
            cancellationToken: ct);
        if (result.MatchedCount != 1)
        {
            throw new MigrationException(
                "Migration lock lease was lost to another instance; refusing to continue so the " +
                "other runner's writes stay exclusive");
        }
    }

    private async Task ReleaseLockAsync(string holder, CancellationToken ct)
    {
        var locks = _database.GetCollection<BsonDocument>(LockCollection);
        await locks.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", LockId),
                Builders<BsonDocument>.Filter.Eq("holder", holder)),
            Builders<BsonDocument>.Update.Set("expires_at", DateTime.UtcNow),
            cancellationToken: ct);
    }

    internal static bool IsDuplicateKey(Exception ex) => ex switch
    {
        MongoWriteException write => write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoBulkWriteException bulk => bulk.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey),
        MongoCommandException command => command.Code is 11000 or 11001 or 12582,
        _ => false,
    };
}
