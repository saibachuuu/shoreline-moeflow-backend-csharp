using System.Globalization;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>
/// m0006: backfill normalized search fields and create list-query indexes.
/// </summary>
public sealed class M0006SearchProjections : LegacyMigration
{
    public const string ProjectListIndex = "project_team_set_status_edit_v1";
    public const string ProjectTeamListIndex = "project_team_status_edit_v1";
    public const string ProjectNameIndex = "project_team_set_status_name_search_v1";
    public const string UserNameIndex = "user_name_search_v1";
    public const string UserAliasesIndex = "user_aliases_search_v1";
    public const string TeamAliasIndex = "team_member_alias_search_v1";
    public const string MemberNameIndex = "project_member_display_name_search_v1";

    public static readonly IReadOnlyList<(string Collection, string Name, BsonDocument Keys)> Indexes =
    [
        ("project", ProjectListIndex, MigrationBson.Keys(("t", 1), ("ps", 1), ("st", 1), ("et", -1))),
        ("project", ProjectTeamListIndex, MigrationBson.Keys(("t", 1), ("st", 1), ("et", -1))),
        ("project", ProjectNameIndex, MigrationBson.Keys(("t", 1), ("ps", 1), ("st", 1), ("ns", 1))),
        ("user", UserNameIndex, MigrationBson.Keys(("ns", 1))),
        ("user", UserAliasesIndex, MigrationBson.Keys(("asrch", 1))),
        ("team_member", TeamAliasIndex, MigrationBson.Keys(("team", 1), ("status", 1), ("asrch", 1))),
        ("project_member", MemberNameIndex, MigrationBson.Keys(("project", 1), ("status", 1), ("dns", 1))),
    ];

    private const int BackfillBatchSize = 500;

    public override string Version => "0006";
    public override string Name => "search_projections";
    public override string Description => "Backfill searchable text and add project/member query indexes";
    protected override string ChecksumSpec =>
        string.Join(";", Indexes.Select(i => $"{i.Collection}:{i.Name}:{i.Keys}")) +
        ";projections=ns(user,project),asrch(user,team_member),dns(project_member)";

    /// <summary>
    /// Python <c>unicodedata.normalize("NFC", s).strip().casefold()</c>.
    /// .NET has no casefold; <c>ToLowerInvariant</c> matches it for the Latin
    /// and CJK text this platform stores and differs only on exotic cases
    /// (e.g. German ß → "ss" under casefold), documented in docs/migrations.md.
    /// </summary>
    public static string SearchText(string? value)
    {
        if (value is null) return string.Empty;
        return value.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
    }

    public static BsonArray AliasProjection(BsonValue? values)
    {
        var result = new BsonArray();
        if (values is BsonArray array)
        {
            foreach (var item in array)
            {
                var normalized = SearchText(item.IsString ? item.AsString : null);
                if (normalized.Length > 0) result.Add(normalized);
            }
        }
        return result;
    }

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var projects = MigrationBson.Collection(database, "project");
        await WriteBatchesAsync(projects,
            await projects.Find(FilterDefinition<BsonDocument>.Empty)
                .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("_id").Include("n"))
                .ToListAsync(ct),
            doc => new BsonDocument("$set", new BsonDocument("ns", SearchText(GetString(doc, "n")))),
            ct);

        var users = MigrationBson.Collection(database, "user");
        await WriteBatchesAsync(users,
            await users.Find(FilterDefinition<BsonDocument>.Empty)
                .Project<BsonDocument>(Builders<BsonDocument>.Projection
                    .Include("_id").Include("n").Include("aliases"))
                .ToListAsync(ct),
            doc => new BsonDocument("$set", new BsonDocument
            {
                ["ns"] = SearchText(GetString(doc, "n")),
                ["asrch"] = AliasProjection(doc.TryGetValue("aliases", out var aliases) ? aliases : null),
            }),
            ct);

        var teamMembers = MigrationBson.Collection(database, "team_member");
        await WriteBatchesAsync(teamMembers,
            await teamMembers.Find(FilterDefinition<BsonDocument>.Empty)
                .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("_id").Include("aliases"))
                .ToListAsync(ct),
            doc => new BsonDocument("$set", new BsonDocument
            {
                ["asrch"] = AliasProjection(doc.TryGetValue("aliases", out var aliases) ? aliases : null),
            }),
            ct);

        var projectMembers = MigrationBson.Collection(database, "project_member");
        await WriteBatchesAsync(projectMembers,
            await projectMembers.Find(FilterDefinition<BsonDocument>.Empty)
                .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("_id").Include("display_name"))
                .ToListAsync(ct),
            doc => new BsonDocument("$set", new BsonDocument("dns", SearchText(GetString(doc, "display_name")))),
            ct);

        foreach (var (collection, name, keys) in Indexes)
            await MigrationBson.EnsureNamedIndexAsync(database, collection, keys, name,
                unique: false, partialFilter: null, ct);
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var catalogs = new Dictionary<string, List<BsonDocument>>();
        foreach (var collection in new[] { "project", "user", "team_member", "project_member" })
            catalogs[collection] = await MigrationBson.ListIndexesSafeAsync(
                MigrationBson.Collection(database, collection), ct);
        foreach (var (collection, name, keys) in Indexes)
        {
            var index = catalogs[collection].FirstOrDefault(i =>
                i.GetValue("name", "").AsString == name);
            if (index is null || !index.GetValue("key", BsonNull.Value).Equals(keys))
                return false;
        }

        var projects = MigrationBson.Collection(database, "project");
        var badProject = await projects.Find(
            FilterDefinition<BsonDocument>.Empty).Project<BsonDocument>(
                Builders<BsonDocument>.Projection.Include("n").Include("ns"))
            .ToListAsync(ct);
        if (badProject.Any(doc => GetStored(doc, "ns") != SearchText(GetString(doc, "n"))))
            return false;

        var members = MigrationBson.Collection(database, "project_member");
        var badMember = await members.Find(
            FilterDefinition<BsonDocument>.Empty).Project<BsonDocument>(
                Builders<BsonDocument>.Projection.Include("display_name").Include("dns"))
            .ToListAsync(ct);
        if (badMember.Any(doc => GetStored(doc, "dns") != SearchText(GetString(doc, "display_name"))))
            return false;

        var users = MigrationBson.Collection(database, "user");
        var badUser = await users.Find(
            FilterDefinition<BsonDocument>.Empty).Project<BsonDocument>(
                Builders<BsonDocument>.Projection.Include("n").Include("ns").Include("aliases").Include("asrch"))
            .ToListAsync(ct);
        if (badUser.Any(doc =>
                GetStored(doc, "ns") != SearchText(GetString(doc, "n")) ||
                !AliasEquals(doc.TryGetValue("aliases", out var aliases) ? aliases : null,
                    doc.TryGetValue("asrch", out var projected) ? projected : null)))
            return false;

        var teamMembers = MigrationBson.Collection(database, "team_member");
        var badTeamMember = await teamMembers.Find(
            FilterDefinition<BsonDocument>.Empty).Project<BsonDocument>(
                Builders<BsonDocument>.Projection.Include("aliases").Include("asrch"))
            .ToListAsync(ct);
        return !badTeamMember.Any(doc =>
            !AliasEquals(doc.TryGetValue("aliases", out var aliases) ? aliases : null,
                doc.TryGetValue("asrch", out var projected) ? projected : null));
    }

    private static string? GetString(BsonDocument doc, string key) =>
        doc.TryGetValue(key, out var value) && value.IsString ? value.AsString : null;

    private static string? GetStored(BsonDocument doc, string key) =>
        doc.TryGetValue(key, out var value) && value.IsString ? value.AsString : null;

    private static bool AliasEquals(BsonValue? values, BsonValue? projected)
    {
        var expected = AliasProjection(values);
        if (projected is not BsonArray actual || actual.Count != expected.Count) return false;
        for (var i = 0; i < expected.Count; i++)
            if (!expected[i].Equals(actual[i])) return false;
        return true;
    }

    private static async Task WriteBatchesAsync(
        IMongoCollection<BsonDocument> collection,
        List<BsonDocument> documents,
        Func<BsonDocument, BsonDocument> update,
        CancellationToken ct)
    {
        var operations = new List<WriteModel<BsonDocument>>(BackfillBatchSize);
        async Task FlushAsync()
        {
            if (operations.Count == 0) return;
            await collection.BulkWriteAsync(operations, new BulkWriteOptions { IsOrdered = false }, ct);
            operations.Clear();
        }
        foreach (var doc in documents)
        {
            operations.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]), update(doc)));
            if (operations.Count >= BackfillBatchSize) await FlushAsync();
        }
        await FlushAsync();
    }
}
