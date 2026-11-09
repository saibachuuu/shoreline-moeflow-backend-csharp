using System.Collections.Frozen;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

public sealed partial class IdentityPermissions
{
    private static readonly FrozenSet<string> WorkerTags = new[] {
        "raw_provider", "scanner", "cropper", "cleaner", "translator", "proofreader", "typesetter"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static ApiError InvalidTag() => new("InvalidIdentityTagError", 5101, "项目身份标签无效或不可分配", 422)
        { IdentityCode = "INVALID_IDENTITY_TAG" };
    private static ApiError ProtectedTag() => new("ProtectedIdentityTagError", 5116, "系统身份标签受保护", 422)
        { IdentityCode = "PROTECTED_IDENTITY_TAG" };
    private static ApiError QualificationRequired() => new("TeamQualificationRequiredError", 5102, "目标用户没有对应的团队工作人员资格", 422)
        { IdentityCode = "TEAM_QUALIFICATION_REQUIRED" };
    private static ApiError InvalidTagRequest() => new("InvalidIdentityRequestError", 5100, "身份标签请求参数错误")
        { IdentityCode = "INVALID_REQUEST" };

    public static string[] NormalizeTags(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Null) return [];
        if (tags.ValueKind != JsonValueKind.Array) throw InvalidTagRequest();
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String) throw InvalidTagRequest();
            var text = tag.GetString()!.Trim();
            if (text.Length > 0) result.Add(text);
        }
        return result.ToArray();
    }

    private sealed record AssignmentContext(BsonDocument? Team, BsonDocument? OperatorRelation,
        BsonDocument? OperatorMember, BsonDocument? TargetRelation, BsonDocument? Policy);

    private async Task<AssignmentContext> AssignmentContextAsync(BsonDocument actor, BsonDocument project, BsonDocument? target, CancellationToken ct)
    {
        var teamTask = store.Collection("team").Find(new BsonDocument("_id", project["t"])).FirstOrDefaultAsync(ct);
        var relationsTask = store.Collection("team_member").Find(new BsonDocument { ["team"] = project["t"],
            ["user"] = new BsonDocument("$in", new BsonArray(target is null ? new[] { actor["_id"] } : new[] { actor["_id"], target["_id"] })),
            ["status"] = "active" }).ToListAsync(ct);
        var memberTask = store.Collection("project_member").Find(new BsonDocument { ["project"] = project["_id"], ["user"] = actor["_id"] }).FirstOrDefaultAsync(ct);
        var policyTask = store.Collection("identity_tag_policy").Find(new BsonDocument("team", project["t"])).FirstOrDefaultAsync(ct);
        await Task.WhenAll(teamTask, relationsTask, memberTask, policyTask);
        var relations = await relationsTask;
        return new(await teamTask, relations.FirstOrDefault(r => r["user"] == actor["_id"]), await memberTask,
            target is null ? null : relations.FirstOrDefault(r => r["user"] == target["_id"]), await policyTask);
    }

    private static bool Assignable(BsonDocument definition) => !definition.TryGetValue("assignable", out var value) || !value.IsBoolean || value.AsBoolean;

    private HashSet<string> AssignableProjectTags(BsonDocument actor, BsonDocument project, BsonDocument? target, AssignmentContext context)
    {
        var manager = ProjectSnapshot(actor, project, context.OperatorRelation, context.OperatorMember, context.Policy).Has("MANAGE_MEMBERS") ||
            context.OperatorRelation?.Text("base_tag") is "creator" or "admin";
        if (!manager && context.Team?.Text("wqm", "qualified") != "open" && Id(actor) != Id(target)) return [];
        var result = WorkerTags.ToHashSet(StringComparer.Ordinal);
        foreach (var definition in Document(context.Policy, "project_tags") ?? new BsonDocument())
            if (definition.Value.IsBsonDocument && (manager || definition.Name is not ("admin" or "creator")) && Assignable(definition.Value.AsBsonDocument))
                result.Add(definition.Name);
        if (manager) result.Add("admin");
        return result;
    }

    public async Task<FrozenSet<string>> AssignableProjectTagsAsync(BsonDocument actor, BsonDocument project, BsonDocument? target, CancellationToken ct = default) =>
        AssignableProjectTags(actor, project, target, await AssignmentContextAsync(actor, project, target, ct)).ToFrozenSet(StringComparer.Ordinal);

    // This checks tag eligibility only. A member mutation must separately check
    // its action permission, project state and optimistic version at commit.
    public async Task<string[]> ValidateProjectTagsAsync(BsonDocument actor, BsonDocument project, BsonDocument? target,
        JsonElement tags, bool external = false, bool allowCreator = false, CancellationToken ct = default)
    {
        var normalized = NormalizeTags(tags);
        var context = await AssignmentContextAsync(actor, project, target, ct);
        var definitions = Document(context.Policy, "project_tags") ?? new BsonDocument();
        if (normalized.Any(tag => !ProjectDefaults.ContainsKey(tag) && !definitions.Contains(tag))) throw InvalidTag();
        if (external)
        {
            if (normalized.Any(tag => !WorkerTags.Contains(tag))) throw InvalidTag();
            return normalized;
        }
        if (target is null) throw InvalidTag();
        if (normalized.Contains("creator") && !allowCreator) throw ProtectedTag();
        foreach (var tag in normalized)
            if (tag is not ("creator" or "admin") && Document(definitions, tag) is { } definition && !Assignable(definition)) throw InvalidTag();
        var snapshot = ProjectSnapshot(actor, project, context.OperatorRelation, context.OperatorMember, context.Policy);
        var projectCreator = context.OperatorMember?.Text("status", "active") == "active" && Strings(context.OperatorMember, "tags").Contains("creator");
        var privileged = snapshot.Has("MANAGE_MEMBERS") || context.OperatorRelation?.Text("base_tag") is "creator" or "admin" || projectCreator;
        if (normalized.Contains("admin") && !privileged) throw InvalidTag();
        var assignable = AssignableProjectTags(actor, project, target, context);
        if (allowCreator) assignable.Add("creator");
        if (normalized.Any(tag => !assignable.Contains(tag))) throw InvalidTag();
        var workers = normalized.Where(WorkerTags.Contains).ToArray();
        if (workers.Length == 0) return normalized;
        var privilegedSelf = Id(actor) == Id(target) && snapshot.Has("MANAGE_MEMBERS");
        if (context.TargetRelation is null && !privilegedSelf) throw QualificationRequired();
        if (context.Team?.Text("wqm", "qualified") != "open" && !privilegedSelf &&
            workers.Any(tag => !Strings(context.TargetRelation, "worker_qualifications").Contains(tag))) throw QualificationRequired();
        return normalized;
    }

    public async Task<string[]> ValidateTeamTagsAsync(BsonDocument team, JsonElement tags, CancellationToken ct = default)
    {
        var normalized = NormalizeTags(tags);
        var policy = await store.Collection("identity_tag_policy").Find(new BsonDocument("team", team["_id"])).FirstOrDefaultAsync(ct);
        var definitions = Document(policy, "team_tags") ?? new BsonDocument();
        if (normalized.Any(tag => !definitions.Contains(tag))) throw InvalidTag();
        if (normalized.Any(tag => Document(definitions, tag) is { } definition && !Assignable(definition))) throw ProtectedTag();
        return normalized;
    }
}
