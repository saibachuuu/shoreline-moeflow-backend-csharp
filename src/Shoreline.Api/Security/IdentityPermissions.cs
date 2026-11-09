using System.Collections.Frozen;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

public sealed record PermissionSnapshot(string? ProjectMemberId, string? UserId, string? ProjectId, string? TeamId,
    IReadOnlyList<string> SourceTags, FrozenSet<string> EffectivePermissions,
    FrozenDictionary<string, IReadOnlyList<string>> PermissionSources, bool IsOwner, DateTime GeneratedAt)
{
    public bool Has(string permission) => EffectivePermissions.Contains(IdentityPermissions.Normalize(permission));
    public bool Has(int permission) => Has(IdentityPermissions.Normalize(permission));
    public object ToApi() => new
    {
        project_member_id = ProjectMemberId,
        user_id = UserId,
        project_id = ProjectId,
        team_id = TeamId,
        source_tags = SourceTags,
        effective_permissions = EffectivePermissions.Order(StringComparer.Ordinal).ToArray(),
        permission_sources = PermissionSources,
        is_owner = IsOwner,
        generated_at = GeneratedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)
    };
}

// Runtime authority for identity permissions. Site administrator status is not
// a project or team membership and never bypasses these checks.
public sealed partial class IdentityPermissions(MongoStore store, TimeProvider clock)
{
    private const string ProjectNames = "ACCESS CHANGE COMPLETE_PROJECT ADD_FILE MOVE_FILE RENAME_FILE DELETE_FILE OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL ADD_TRA DELETE_TRA PROOFREAD_TRA CHECK_TRA ADD_TARGET CHANGE_TARGET DELETE_TARGET CHECK_USER INVITE_USER CHANGE_USER_REMARK CHANGE_USER_ROLE DELETE_USER MANAGE_MEMBERS";
    private const string TeamNames = "ACCESS DELETE CHANGE CREATE_ROLE DELETE_ROLE CHECK_USER INVITE_USER DELETE_USER CHANGE_USER_ROLE CHANGE_USER_REMARK CREATE_PROJECT CREATE_PROJECT_SET CHANGE_PROJECT_SET DELETE_PROJECT_SET USE_OCR_QUOTA USE_MT_QUOTA INSIGHT ACCESS_TERM_BANK CREATE_TERM_BANK CHANGE_TERM_BANK DELETE_TERM_BANK CREATE_TERM CHANGE_TERM DELETE_TERM";
    private const string FileNames = "ACCESS ADD_FILE MOVE_FILE RENAME_FILE DELETE_FILE";
    private const string TranslationNames = "OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL ADD_TRA DELETE_TRA";
    private static string[] Codes(string prefix, string names) => names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => prefix + n).ToArray();
    private static readonly FrozenDictionary<string, string[]> ProjectDefaults = new Dictionary<string, string[]>
    {
        ["creator"] = Codes("project:", ProjectNames),
        ["admin"] = Codes("project:", ProjectNames),
        ["raw_provider"] = Codes("project:", FileNames),
        ["scanner"] = Codes("project:", FileNames),
        ["cropper"] = Codes("project:", FileNames),
        ["cleaner"] = Codes("project:", FileNames),
        ["translator"] = Codes("project:", FileNames + " " + TranslationNames),
        ["proofreader"] = Codes("project:", FileNames + " " + TranslationNames + " PROOFREAD_TRA CHECK_TRA"),
        ["typesetter"] = Codes("project:", FileNames + " OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL")
    }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string[]> TeamDefaults = new Dictionary<string, string[]>
    {
        ["creator"] = Codes("team:", TeamNames),
        ["admin"] = Codes("team:", TeamNames).Where(p => p != "team:DELETE").ToArray(),
        ["member"] = ["team:ACCESS"]
    }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenSet<string> NormalOnly = Codes("project:",
        "ADD_FILE MOVE_FILE RENAME_FILE DELETE_FILE OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL ADD_TRA DELETE_TRA PROOFREAD_TRA CHECK_TRA ADD_TARGET CHANGE_TARGET DELETE_TARGET").ToFrozenSet(StringComparer.Ordinal);

    public static BsonDocument SystemTagDefinitions(string scope)
    {
        var defaults = scope switch { "team" => TeamDefaults, "project" => ProjectDefaults, _ => throw new ArgumentException("Invalid tag scope", nameof(scope)) };
        return new BsonDocument(defaults.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new BsonElement(p.Key,
            new BsonDocument
            {
                ["name"] = p.Key,
                ["code"] = p.Key,
                ["permissions"] = new BsonArray(p.Value.Order(StringComparer.Ordinal)),
                ["assignable"] = scope == "project" && p.Key != "creator",
                ["source"] = "site"
            })));
    }

    public static bool IsWorkerTag(string tag) => WorkerTags.Contains(tag);

    public static string Normalize(string permission) => permission.StartsWith("project:", StringComparison.Ordinal) ||
        permission.StartsWith("team:", StringComparison.Ordinal) ? permission : "project:" + permission;

    public static string Normalize(int permission) => permission switch
    {
        1 => "project:ACCESS",
        5 => "project:DELETE",
        10 => "project:CHANGE",
        101 => "project:CHECK_USER",
        105 => "project:INVITE_USER",
        110 => "project:DELETE_USER",
        115 => "project:CHANGE_USER_ROLE",
        120 => "project:CHANGE_USER_REMARK",
        1010 => "project:COMPLETE_PROJECT",
        1020 => "project:ADD_FILE",
        1030 => "project:MOVE_FILE",
        1040 => "project:RENAME_FILE",
        1050 => "project:DELETE_FILE",
        1060 => "project:OUTPUT_TRA",
        1080 => "project:ADD_LABEL",
        1090 => "project:MOVE_LABEL",
        1100 => "project:DELETE_LABEL",
        1110 => "project:ADD_TRA",
        1120 => "project:DELETE_TRA",
        1130 => "project:PROOFREAD_TRA",
        1140 => "project:CHECK_TRA",
        1150 => "project:ADD_TARGET",
        1160 => "project:CHANGE_TARGET",
        1170 => "project:DELETE_TARGET",
        _ => permission.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static string? Id(BsonDocument? doc, string key = "_id") => doc is not null && doc.TryGetValue(key, out var value) && value.IsObjectId ? value.AsObjectId.ToString() : null;
    private static string[] Strings(BsonDocument? doc, string key) => doc is not null && doc.TryGetValue(key, out var value) && value.IsBsonArray
        ? value.AsBsonArray.Where(v => v.IsString).Select(v => v.AsString).ToArray() : [];
    private static BsonDocument? Document(BsonDocument? doc, string key) => doc is not null && doc.TryGetValue(key, out var value) && value.IsBsonDocument ? value.AsBsonDocument : null;
    private static string[]? TagPermissions(BsonDocument? policy, string scope, string tag)
    {
        var definition = Document(Document(policy, scope + "_tags"), tag);
        if (definition is not null && definition.Contains("permissions")) return Strings(definition, "permissions");
        var defaults = scope == "team" ? TeamDefaults : ProjectDefaults;
        return defaults.GetValueOrDefault(tag);
    }
    private static void Add(Dictionary<string, List<string>> sources, string permission, string source)
    {
        if (!sources.TryGetValue(permission, out var list)) sources[permission] = list = [];
        list.Add(source);
    }
    private PermissionSnapshot Snapshot(BsonDocument? user, BsonDocument? project, BsonDocument? team,
        BsonDocument? member, string[] tags, Dictionary<string, List<string>> sources, bool owner = false) => new(
        Id(member), Id(user), Id(project), project is null ? Id(team) : Id(project, "t"), Array.AsReadOnly(tags),
        sources.Keys.Where(p => project is null || p.StartsWith("project:", StringComparison.Ordinal)).ToFrozenSet(StringComparer.Ordinal),
        sources.ToFrozenDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.AsReadOnly(), StringComparer.Ordinal), owner, clock.GetUtcNow().UtcDateTime);

    public async Task<PermissionSnapshot> TeamSnapshotAsync(BsonDocument? user, BsonDocument? team, CancellationToken ct = default)
    {
        if (team is null) return Snapshot(user, null, null, null, [], new(StringComparer.Ordinal));
        return (await TeamSnapshotsAsync(user, [team], ct))[team["_id"].AsObjectId];
    }

    public async Task<IReadOnlyDictionary<ObjectId, PermissionSnapshot>> TeamSnapshotsAsync(BsonDocument? user,
        IReadOnlyList<BsonDocument> teams, CancellationToken ct = default)
    {
        var relations = new Dictionary<ObjectId, BsonDocument>();
        var policies = new Dictionary<ObjectId, BsonDocument>();
        if (user is not null && teams.Count != 0)
        {
            var ids = new BsonDocument("$in", new BsonArray(teams.Select(team => team["_id"])));
            var memberTask = store.Collection("team_member").Find(new BsonDocument { ["team"] = ids, ["user"] = user["_id"], ["status"] = "active" }).ToListAsync(ct);
            var policyTask = store.Collection("identity_tag_policy").Find(new BsonDocument("team", ids)).ToListAsync(ct);
            await Task.WhenAll(memberTask, policyTask);
            relations = (await memberTask).ToDictionary(member => member["team"].AsObjectId);
            policies = (await policyTask).ToDictionary(policy => policy["team"].AsObjectId);
        }
        return teams.ToDictionary(team => team["_id"].AsObjectId, team => TeamSnapshot(user, team,
            relations.GetValueOrDefault(team["_id"].AsObjectId), policies.GetValueOrDefault(team["_id"].AsObjectId)));
    }

    private PermissionSnapshot TeamSnapshot(BsonDocument? user, BsonDocument team, BsonDocument? relation, BsonDocument? policy)
    {
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (relation is null) return Snapshot(user, null, team, null, [], sources);
        var baseTag = relation.Text("base_tag", "member");
        foreach (var permission in TagPermissions(policy, "team", baseTag) ?? []) Add(sources, permission, "team_base:" + baseTag);
        foreach (var tag in Strings(relation, "tags").Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Order(StringComparer.Ordinal))
            foreach (var permission in TagPermissions(policy, "team", tag) ?? []) Add(sources, permission, "team_tag:" + tag);
        return Snapshot(user, null, team, null, [baseTag, .. Strings(relation, "tags")], sources);
    }

    public async Task<PermissionSnapshot> ProjectSnapshotAsync(BsonDocument? user, BsonDocument project, CancellationToken ct = default) =>
        (await ProjectSnapshotsAsync(user, [project], ct))[project["_id"].AsObjectId];

    // Exactly three membership/policy queries per page, rather than per row.
    public async Task<IReadOnlyDictionary<ObjectId, PermissionSnapshot>> ProjectSnapshotsAsync(BsonDocument? user,
        IReadOnlyList<BsonDocument> projects, CancellationToken ct = default)
    {
        var members = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        var relations = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        var policies = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        if (user is not null && projects.Count > 0)
        {
            var projectIds = new BsonArray(projects.Select(p => p["_id"]));
            var teamIds = new BsonArray(projects.Select(p => p["t"]).Distinct());
            var memberTask = store.Collection("project_member").Find(new BsonDocument { ["user"] = user["_id"], ["project"] = new BsonDocument("$in", projectIds) }).ToListAsync(ct);
            var relationTask = store.Collection("team_member").Find(new BsonDocument { ["user"] = user["_id"], ["team"] = new BsonDocument("$in", teamIds), ["status"] = "active" }).ToListAsync(ct);
            var policyTask = store.Collection("identity_tag_policy").Find(new BsonDocument("team", new BsonDocument("$in", teamIds))).ToListAsync(ct);
            await Task.WhenAll(memberTask, relationTask, policyTask);
            members = (await memberTask).ToDictionary(p => p["project"].ToString()!, StringComparer.Ordinal);
            relations = (await relationTask).ToDictionary(p => p["team"].ToString()!, StringComparer.Ordinal);
            policies = (await policyTask).ToDictionary(p => p["team"].ToString()!, StringComparer.Ordinal);
        }
        return projects.ToDictionary(p => p["_id"].AsObjectId, p => ProjectSnapshot(user, p,
            relations.GetValueOrDefault(p["t"].ToString()!), members.GetValueOrDefault(p["_id"].ToString()!), policies.GetValueOrDefault(p["t"].ToString()!)));
    }

    private PermissionSnapshot ProjectSnapshot(BsonDocument? user, BsonDocument project, BsonDocument? relation, BsonDocument? member, BsonDocument? policy)
    {
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tags = Strings(member, "tags");
        if (user is null) return Snapshot(null, project, null, null, [], sources);
        if (relation is not null && member?.Text("status", "active") != "removed")
        {
            Add(sources, "project:ACCESS", "team_access");
            foreach (var tag in Strings(relation, "tags").Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Order(StringComparer.Ordinal))
                foreach (var permission in TagPermissions(policy, "team", tag) ?? []) Add(sources, permission, "team_tag:" + tag);
            var baseTag = relation.Text("base_tag", "member");
            if (baseTag is "creator" or "admin")
                foreach (var permission in ProjectDefaults[baseTag]) Add(sources, permission, "team_inherited:" + baseTag);
        }
        if (member is not null && member.Text("status", "active") is "active" or "invited")
        {
            Add(sources, "project:ACCESS", "project_member:" + member.Text("status", "active"));
            if (member.Text("status", "active") == "active")
                foreach (var tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Order(StringComparer.Ordinal))
                    foreach (var permission in TagPermissions(policy, "project", tag) ?? []) Add(sources, permission, "project_tag:" + tag);
        }
        // Team permission sources are retained for compatibility in the audit
        // snapshot, but must not become effective project permissions.
        var snapshot = Snapshot(user, project, null, member, tags, sources,
            Id(project, "ou") == Id(user) && member?.Text("status", "active") == "active" && tags.Contains("creator"));
        return snapshot;
    }

    public static bool CanProject(PermissionSnapshot snapshot, BsonDocument project, string permission)
    {
        var code = Normalize(permission);
        var status = project.GetValue("st", 0);
        return code.StartsWith("project:", StringComparison.Ordinal) && snapshot.Has(code) &&
            (!NormalOnly.Contains(code) || status.IsInt32 && status.AsInt32 == 0);
    }
    public async Task RequireProjectAsync(BsonDocument user, BsonDocument project, string permission, CancellationToken ct = default)
    {
        if (!CanProject(await ProjectSnapshotAsync(user, project, ct), project, permission)) throw ApiError.Forbidden();
    }

    public async Task<bool> CanProjectActionAsync(BsonDocument? user, BsonDocument project, string action, CancellationToken ct = default)
    {
        if (user is null) return false;
        if (action is "complete" or "reopen") return (await ProjectSnapshotAsync(user, project, ct)).Has("COMPLETE_PROJECT");
        if (action != "clear") return false;
        return await store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = project["t"],
            ["user"] = user["_id"],
            ["status"] = "active",
            ["base_tag"] = "creator"
        }).AnyAsync(ct);
    }
}
