using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed class IdentityTagPolicyService(MongoStore store, TeamMemberService members, TeamMutationGate gates,
    IdentityAuditWriter audit, TimeProvider clock)
{
    private static readonly Regex TagCode = new(@"\A[a-z][a-z0-9_]{1,63}\z", RegexOptions.CultureInvariant);
    private static readonly BsonDocument SystemTeam = IdentityPermissions.SystemTagDefinitions("team");
    private static readonly BsonDocument SystemProject = IdentityPermissions.SystemTagDefinitions("project");
    private static readonly HashSet<string> TeamPermissions = SystemTeam["creator"]["permissions"].AsBsonArray.Select(p => p.AsString).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> ProjectPermissions = SystemProject["creator"]["permissions"].AsBsonArray.Select(p => p.AsString).ToHashSet(StringComparer.Ordinal);
    private IMongoCollection<BsonDocument> Policies => store.Collection("identity_tag_policy");

    public async Task<object> GetAsync(BsonDocument team, BsonDocument actor, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        await members.RequireManagerAsync(team, actor, ct);
        return BsonApi.Value(Response(await GetOrCreateAsync(team, ct)))!;
    }

    private async Task<BsonDocument> GetOrCreateAsync(BsonDocument team, CancellationToken ct)
    {
        var key = new BsonDocument("team", team["_id"]);
        var now = clock.GetUtcNow().UtcDateTime;
        return await Policies.FindOneAndUpdateAsync<BsonDocument>(key, new BsonDocument("$setOnInsert", new BsonDocument
        {
            ["team"] = team["_id"],
            ["team_tags"] = new BsonDocument(),
            ["project_tags"] = new BsonDocument(),
            ["version"] = 0,
            ["create_time"] = now,
            ["edit_time"] = now
        }),
            new() { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
    }

    private static BsonDocument Response(BsonDocument policy)
    {
        var team = Definitions(SystemTeam, policy.GetValue("team_tags", new BsonDocument()).AsBsonDocument);
        var project = Definitions(SystemProject, policy.GetValue("project_tags", new BsonDocument()).AsBsonDocument);
        return new BsonDocument { ["version"] = policy.Integer("version"), ["team_tags"] = team, ["project_tags"] = project };
    }

    private static BsonDocument Definitions(BsonDocument system, BsonDocument overrides)
    {
        var result = system.DeepClone().AsBsonDocument;
        foreach (var entry in overrides)
        {
            if (!entry.Value.IsBsonDocument) continue;
            var value = entry.Value.DeepClone().AsBsonDocument;
            if (system.TryGetValue(entry.Name, out var initial))
            {
                var merged = initial.DeepClone().AsBsonDocument;
                foreach (var item in value) merged[item.Name] = item.Value;
                merged["initial_permissions"] = initial["permissions"];
                merged["initial_assignable"] = initial["assignable"];
                value = merged;
            }
            value["code"] = entry.Name;
            value["source"] = system.Contains(entry.Name) ? "team_override" : "team";
            result[entry.Name] = value;
        }
        return result;
    }

    public async Task<object> UpdateAsync(BsonDocument team, BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        var relation = await members.RequireManagerAsync(team, actor, ct);
        IdentityErrors.RequireObject(data);
        var expected = IdentityErrors.ExpectedVersion(data);
        var policy = await GetOrCreateAsync(team, ct);
        if (policy.Integer("version") != expected) throw IdentityErrors.VersionConflict();
        var before = Response(policy);
        var next = policy.DeepClone().AsBsonDocument;
        foreach (var definition in Array(data, "upserts"))
        {
            IdentityErrors.RequireObject(definition);
            var (scope, code) = ScopeAndCode(definition);
            if (!TagCode.IsMatch(code)) throw IdentityErrors.InvalidTag();
            var isSystemTeam = scope == "team" && SystemTeam.Contains(code);
            var isSystemProject = scope == "project" && SystemProject.Contains(code);
            var permissions = PermissionList(definition);
            var allowed = scope == "team" ? TeamPermissions : ProjectPermissions;
            if (permissions.Any(p => !allowed.Contains(p))) throw IdentityErrors.InvalidTag();
            if (!isSystemProject && permissions.Any(p => p is "project:COMPLETE_PROJECT" or "project:MANAGE_MEMBERS")) throw IdentityErrors.ProtectedTag();
            var field = IdentityErrors.Value(definition, "name");
            var name = field.ValueKind == JsonValueKind.Undefined ? code : field.ValueKind == JsonValueKind.String
                ? field.GetString()! : throw IdentityErrors.InvalidRequest("tag name must be a string");
            if (isSystemTeam && relation.Text("base_tag") != "creator") throw ApiError.Forbidden();
            if ((isSystemTeam || isSystemProject) && name != code) throw IdentityErrors.ProtectedTag();
            var assignable = definition.TryGetProperty("assignable", out var assignment) ? IdentityErrors.Truthy(assignment) : true;
            next[scope + "_tags"][code] = new BsonDocument { ["name"] = name, ["permissions"] = new BsonArray(permissions), ["assignable"] = !isSystemTeam && assignable };
        }
        foreach (var removal in Array(data, "removes"))
        {
            IdentityErrors.RequireObject(removal);
            var (scope, code) = ScopeAndCode(removal);
            var system = scope == "team" ? SystemTeam : SystemProject;
            if (system.Contains(code))
            {
                if (scope == "team" && relation.Text("base_tag") != "creator") throw ApiError.Forbidden();
            }
            else if (await InUseAsync(team, scope, code, ct)) throw IdentityErrors.PolicyInUse();
            next[scope + "_tags"].AsBsonDocument.Remove(code);
        }
        var saved = await Policies.FindOneAndUpdateAsync<BsonDocument>(IdentityErrors.VersionFilter(policy, expected), new BsonDocument
        {
            ["$set"] = new BsonDocument { ["team_tags"] = next["team_tags"], ["project_tags"] = next["project_tags"], ["edit_time"] = clock.GetUtcNow().UtcDateTime },
            ["$inc"] = new BsonDocument("version", 1)
        }, new() { ReturnDocument = ReturnDocument.After }, ct) ?? throw IdentityErrors.VersionConflict();
        var after = Response(saved);
        await audit.RecordAsync(actor, "identity_tag_policy_update", team, null, before, after, requestId, CancellationToken.None);
        return BsonApi.Value(after)!;
    }

    private static IEnumerable<JsonElement> Array(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw IdentityErrors.InvalidRequest();
        return value.EnumerateArray().ToArray();
    }

    private static (string Scope, string Code) ScopeAndCode(JsonElement data)
    {
        var scope = IdentityErrors.Value(data, "scope");
        var code = IdentityErrors.Value(data, "code");
        if (scope.ValueKind != JsonValueKind.String || scope.GetString() is not ("team" or "project") || code.ValueKind != JsonValueKind.String)
            throw IdentityErrors.InvalidRequest();
        return (scope.GetString()!, code.GetString()!);
    }

    private static string[] PermissionList(JsonElement data)
    {
        if (!data.TryGetProperty("permissions", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw IdentityErrors.InvalidRequest();
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw IdentityErrors.InvalidRequest();
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }

    private async Task<bool> InUseAsync(BsonDocument team, string scope, string code, CancellationToken ct)
    {
        if (scope == "team")
            return await store.Collection("team_member").Find(new BsonDocument { ["team"] = team["_id"], ["tags"] = code }).AnyAsync(ct);
        BsonDocument[] pipeline = [new("$match", new BsonDocument("tags", code)),
            new("$lookup", new BsonDocument { ["from"] = "project", ["let"] = new BsonDocument("pid", "$project"), ["as"] = "projects",
                ["pipeline"] = new BsonArray {
                    new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$_id", "$$pid" }))),
                    new BsonDocument("$match", new BsonDocument("t", team["_id"])), new BsonDocument("$project", new BsonDocument("_id", 1)) } }),
            new("$match", new BsonDocument("projects.0", new BsonDocument("$exists", true))), new("$limit", 1)];
        return await store.Collection("project_member").Aggregate<BsonDocument>(pipeline).AnyAsync(ct);
    }
}
