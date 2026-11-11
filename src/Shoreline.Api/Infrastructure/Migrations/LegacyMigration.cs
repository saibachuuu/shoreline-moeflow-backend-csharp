using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>
/// A versioned schema migration. Mirrors the contract of one module under
/// Python's <c>app/migrations/versions/</c>: <c>VERSION</c>, <c>NAME</c>,
/// <c>DESCRIPTION</c>, <c>LEVEL</c>, <c>up</c>, <c>down</c> and <c>verify</c>.
/// </summary>
public abstract class LegacyMigration
{
    public abstract string Version { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>All legacy migrations ship at level AUTO.</summary>
    public virtual string Level => "AUTO";

    /// <summary>
    /// Canonical spec fragment folded into <see cref="Checksum"/> so an edit
    /// to what the migration does invalidates previously recorded rows.
    /// Index/field migrations override this with their key specifications.
    /// </summary>
    protected virtual string ChecksumSpec => string.Empty;

    /// <summary>
    /// Content hash of the migration semantics. Stored in the version ledger
    /// and compared on every status check, mirroring the Python runner's
    /// AST checksum without depending on Python source text.
    /// </summary>
    public string Checksum
    {
        get
        {
            var payload = string.Join("\n",
                "shoreline-legacy-migration/v1", Version, Name, Description, Level, ChecksumSpec);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }
    }

    public abstract Task UpAsync(IMongoDatabase database, CancellationToken ct);

    /// <summary>
    /// Reverts the migration. Irreversible migrations throw, matching the
    /// Python runner's <c>IrreversibleMigration</c>.
    /// </summary>
    public virtual Task DownAsync(IMongoDatabase database, CancellationToken ct) =>
        throw new MigrationException(
            $"Migration {Version} ({Name}) is irreversible; restore the database and object-storage snapshots");

    /// <summary>Returns true when the migration's postconditions hold.</summary>
    public virtual Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct) =>
        Task.FromResult(true);
}

/// <summary>
/// Fatal migration error. Mirrors Python's <c>MigrationError</c>: the run
/// stops, the failed version is not recorded, and the process surfaces the
/// message instead of booting against a half-migrated database.
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message) : base(message) { }
    public MigrationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Status row for one discovered migration.</summary>
public sealed record MigrationStatus(
    string Version,
    string Name,
    string Level,
    bool Applied,
    DateTime? AppliedAt,
    long? DurationMs);

/// <summary>One successfully applied migration.</summary>
public sealed record AppliedMigration(string Version, string Name, long DurationMs);

/// <summary>Shared BSON helpers for the migration implementations.</summary>
internal static class MigrationBson
{
    public static IMongoCollection<BsonDocument> Collection(IMongoDatabase database, string name) =>
        database.GetCollection<BsonDocument>(name);

    public static async Task<List<BsonDocument>> ListIndexesSafeAsync(
        IMongoCollection<BsonDocument> collection, CancellationToken ct)
    {
        try
        {
            return await (await collection.Indexes.ListAsync(ct)).ToListAsync(ct);
        }
        catch (MongoCommandException ex) when (ex.Code is 26)
        {
            // NamespaceNotFound: the collection was never created.
            return [];
        }
    }

    public static bool SameKeys(BsonDocument actual, BsonDocument expected) =>
        actual.Equals(expected);

    public static BsonDocument Keys(params (string Field, int Direction)[] fields)
    {
        var doc = new BsonDocument();
        foreach (var (field, direction) in fields) doc[field] = direction;
        return doc;
    }

    public static async Task EnsureNamedIndexAsync(
        IMongoDatabase database,
        string collectionName,
        BsonDocument keys,
        string indexName,
        bool unique,
        BsonDocument? partialFilter,
        CancellationToken ct,
        Func<BsonDocument, bool>? acceptExisting = null)
    {
        var collection = Collection(database, collectionName);
        var catalog = await ListIndexesSafeAsync(collection, ct);
        foreach (var index in catalog)
        {
            if (index.GetValue("name", "").AsString != indexName) continue;
            if (!index.GetValue("key", BsonNull.Value).Equals(keys)) continue;
            if (index.GetValue("unique", false).ToBoolean() != unique) continue;
            var currentPartial = index.TryGetValue("partialFilterExpression", out var partial)
                ? partial : null;
            if (acceptExisting is not null)
            {
                if (acceptExisting(index)) return;
            }
            else if (EqualsBson(currentPartial, partialFilter))
            {
                return;
            }
        }

        var toDrop = catalog
            .Where(index => index.GetValue("name", "").AsString == indexName ||
                index.GetValue("key", BsonNull.Value).Equals(keys))
            .Select(index => index.GetValue("name", "").AsString)
            .Where(name => name is not "_id_" and not "")
            .Distinct()
            .ToArray();
        foreach (var name in toDrop)
        {
            try
            {
                await collection.Indexes.DropOneAsync(name, ct);
            }
            catch (MongoCommandException ex) when (ex.Code is 26 or 27)
            {
                // Raced with another initializer; the catalog re-check below covers it.
            }
        }

        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = indexName,
            Background = true,
            Unique = unique,
        };
        if (partialFilter is not null)
            options.PartialFilterExpression = partialFilter;
        try
        {
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(keys, options), cancellationToken: ct);
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // IndexOptionsConflict / IndexKeySpecsConflict: another initializer won
            // the race with an equivalent spec; re-read and accept it or fail.
            var current = await ListIndexesSafeAsync(collection, ct);
            var named = current.FirstOrDefault(index =>
                index.GetValue("name", "").AsString == indexName);
            if (named is null || !named.GetValue("key", BsonNull.Value).Equals(keys))
                throw;
        }
    }

    private static bool EqualsBson(BsonValue? left, BsonValue? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }
}
