using System.Globalization;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

// Port of app/models/presence.py. Collection/field/index names stay compatible
// with documents written by the legacy Python backend. That is a data-format
// contract only; it does not mean both backends should write this collection
// concurrently.
public sealed class PresenceService(MongoStore store, IConfiguration configuration,
    ObjectStorage storage, TimeProvider clock)
{
    public const int PresenceTimeoutSeconds = 45;
    private const int MaxActionCodePoints = 64;
    private static readonly TimeSpan AvatarValidity = TimeSpan.FromHours(1);

    private IMongoCollection<BsonDocument> Presences => store.Collection("project_presence");
    private IMongoCollection<BsonDocument> Users => store.Collection("user");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Members => store.Collection("project_member");

    // Called explicitly during startup. Reads/writes never create indexes.
    public async Task InitializeAsync(CancellationToken ct)
    {
        await Presences.Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project"),
                new CreateIndexOptions { Name = "project_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("team"),
                new CreateIndexOptions { Name = "team_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("user"),
                new CreateIndexOptions { Name = "user_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("expire_at"),
                new CreateIndexOptions { Name = "expire_at_1", ExpireAfter = TimeSpan.Zero }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("user"),
                new CreateIndexOptions { Name = "project_1_user_1", Unique = true }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("expire_at"),
                new CreateIndexOptions { Name = "team_1_expire_at_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("expire_at"),
                new CreateIndexOptions { Name = "project_1_expire_at_1" })
        ], ct);
    }

    public async Task HeartbeatAsync(BsonDocument project, BsonDocument user, string? action, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var update = new BsonDocument("$set", new BsonDocument
        {
            // Python _id documents store project.team as a plain ObjectId in "t".
            ["team"] = project.GetValue("t", BsonNull.Value),
            ["action"] = NormalizeAction(action),
            ["last_heartbeat"] = now,
            ["expire_at"] = now.AddSeconds(PresenceTimeoutSeconds)
        });
        await Presences.UpdateOneAsync(OwnerFilter(project, user), update,
            new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task LeaveAsync(BsonDocument project, BsonDocument user, CancellationToken ct) =>
        await Presences.DeleteManyAsync(OwnerFilter(project, user), ct);

    public async Task<List<Dictionary<string, object?>>> ActiveUsersForProjectAsync(BsonDocument project, CancellationToken ct)
    {
        var projectId = IdValue(project);
        if (projectId is null) return [];

        var now = clock.GetUtcNow().UtcDateTime;
        var presences = await Presences.Find(new BsonDocument
        {
            ["project"] = projectId.Value,
            ["expire_at"] = new BsonDocument("$gt", now)
        }).ToListAsync(ct);
        if (presences.Count == 0) return [];

        var userIds = DistinctUserIds(presences);
        var users = await LoadUsersAsync(userIds, ct);
        var memberNames = await LoadMemberNamesAsync(new[] { projectId.Value }, userIds, ct);

        var result = new List<Dictionary<string, object?>>(presences.Count);
        foreach (var presence in presences)
        {
            var userId = IdValue(presence, "user");
            if (userId is null || !users.TryGetValue(userId.Value, out var user)) continue;
            memberNames.TryGetValue((projectId.Value, userId.Value), out var memberName);
            result.Add(await ViewAsync(userId.Value, user, memberName, presence));
        }
        return result;
    }

    public async Task<Dictionary<string, Dictionary<string, object?>>> ActivePresenceForTeamAsync(
        BsonDocument team, CancellationToken ct)
    {
        var teamId = IdValue(team);
        var result = new Dictionary<string, Dictionary<string, object?>>();
        if (teamId is null) return result;

        var now = clock.GetUtcNow().UtcDateTime;
        var presences = await Presences.Find(new BsonDocument
        {
            ["team"] = teamId.Value,
            ["expire_at"] = new BsonDocument("$gt", now)
        }).ToListAsync(ct);
        if (presences.Count == 0) return result;

        var projectIds = presences.Select(p => IdValue(p, "project"))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var userIds = DistinctUserIds(presences);
        var users = await LoadUsersAsync(userIds, ct);
        var existingProjects = await LoadExistingProjectIdsAsync(projectIds, ct);
        var memberNames = await LoadMemberNamesAsync(projectIds, userIds, ct);

        var groups = new Dictionary<string, TeamGroup>();
        foreach (var presence in presences)
        {
            var projectId = IdValue(presence, "project");
            var userId = IdValue(presence, "user");
            if (projectId is null || userId is null) continue;
            if (!existingProjects.Contains(projectId.Value)) continue;
            if (!users.TryGetValue(userId.Value, out var user)) continue;

            var key = projectId.Value.ToString();
            if (!groups.TryGetValue(key, out var group))
            {
                group = new TeamGroup(key);
                groups[key] = group;
            }
            memberNames.TryGetValue((projectId.Value, userId.Value), out var memberName);
            group.Users.Add(await ViewAsync(userId.Value, user, memberName, presence));
        }

        foreach (var (key, group) in groups)
        {
            group.Item["user_count"] = group.Users.Count;
            result[key] = group.Item;
        }
        return result;
    }

    private static BsonDocument OwnerFilter(BsonDocument project, BsonDocument user) => new()
    {
        ["project"] = project["_id"],
        ["user"] = user["_id"]
    };

    private static string NormalizeAction(string? action)
    {
        if (string.IsNullOrEmpty(action)) return "working";
        var builder = new StringBuilder(MaxActionCodePoints);
        var count = 0;
        foreach (var rune in action.EnumerateRunes())
        {
            if (count == MaxActionCodePoints) break;
            builder.Append(rune.ToString());
            count++;
        }
        return builder.Length == 0 ? "working" : builder.ToString();
    }

    private async Task<Dictionary<string, object?>> ViewAsync(ObjectId userId, BsonDocument user,
        string? memberName, BsonDocument presence)
    {
        var action = presence.Text("action");
        return new Dictionary<string, object?>
        {
            ["id"] = userId.ToString(),
            ["name"] = ResolveName(user, memberName),
            ["avatar"] = await AvatarAsync(user),
            ["action"] = action.Length == 0 ? "working" : action,
            ["last_heartbeat"] = LastHeartbeat(presence)
        };
    }

    private static string ResolveName(BsonDocument user, string? memberName)
    {
        if (!string.IsNullOrEmpty(memberName)) return memberName;
        var displayName = user.Text("ddn");
        return displayName.Length == 0 ? user.Text("n") : displayName;
    }

    private async Task<string> AvatarAsync(BsonDocument user)
    {
        var avatar = user.Text("a");
        if (avatar.Length == 0) return configuration["Storage:DefaultUserAvatar"] ?? "";
        var key = AvatarStoragePrefixes.User(configuration) + avatar;
        return await storage.SignGetAsync(key, AvatarValidity);
    }

    private static string LastHeartbeat(BsonDocument presence)
    {
        if (!presence.TryGetValue("last_heartbeat", out var value) || !value.IsBsonDateTime) return "";
        var timestamp = value.ToUniversalTime();
        // Python datetime.isoformat() omits fractions for whole seconds and uses
        // six digits otherwise (BSON Date keeps milliseconds).
        return timestamp.Ticks % TimeSpan.TicksPerSecond == 0
            ? timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<ObjectId, BsonDocument>> LoadUsersAsync(
        IReadOnlyCollection<ObjectId> ids, CancellationToken ct)
    {
        var result = new Dictionary<ObjectId, BsonDocument>();
        if (ids.Count == 0) return result;
        var rows = await Users.Find(new BsonDocument("_id",
            new BsonDocument("$in", InValues(ids)))).ToListAsync(ct);
        foreach (var row in rows)
        {
            var id = IdValue(row);
            if (id is not null) result[id.Value] = row;
        }
        return result;
    }

    private async Task<HashSet<ObjectId>> LoadExistingProjectIdsAsync(
        IReadOnlyCollection<ObjectId> ids, CancellationToken ct)
    {
        var result = new HashSet<ObjectId>();
        if (ids.Count == 0) return result;
        var rows = await Projects.Find(new BsonDocument("_id",
                new BsonDocument("$in", InValues(ids))))
            .Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var id = IdValue(row);
            if (id is not null) result.Add(id.Value);
        }
        return result;
    }

    // One query for the whole page; callers never fall back to per-presence lookups.
    private async Task<Dictionary<(ObjectId Project, ObjectId User), string>> LoadMemberNamesAsync(
        IReadOnlyCollection<ObjectId> projectIds, IReadOnlyCollection<ObjectId> userIds, CancellationToken ct)
    {
        var result = new Dictionary<(ObjectId Project, ObjectId User), string>();
        if (projectIds.Count == 0 || userIds.Count == 0) return result;
        var rows = await Members.Find(new BsonDocument
        {
            ["project"] = new BsonDocument("$in", InValues(projectIds)),
            ["user"] = new BsonDocument("$in", InValues(userIds)),
            ["status"] = "active"
        }).ToListAsync(ct);
        foreach (var row in rows)
        {
            var projectId = IdValue(row, "project");
            var userId = IdValue(row, "user");
            if (projectId is null || userId is null) continue;
            result[(projectId.Value, userId.Value)] = row.Text("display_name");
        }
        return result;
    }

    private static List<ObjectId> DistinctUserIds(IEnumerable<BsonDocument> presences) =>
        presences.Select(presence => IdValue(presence, "user"))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

    private static BsonArray InValues(IEnumerable<ObjectId> ids) =>
        new(ids.Select(id => (BsonValue)id));

    private static ObjectId? IdValue(BsonDocument document, string key = "_id") =>
        document.TryGetValue(key, out var value) && value.IsObjectId ? value.AsObjectId : null;

    private sealed class TeamGroup
    {
        public List<Dictionary<string, object?>> Users { get; } = [];

        public Dictionary<string, object?> Item { get; }

        public TeamGroup(string projectId)
        {
            Item = new Dictionary<string, object?>
            {
                ["project_id"] = projectId,
                ["user_count"] = 0,
                ["users"] = Users
            };
        }
    }
}
