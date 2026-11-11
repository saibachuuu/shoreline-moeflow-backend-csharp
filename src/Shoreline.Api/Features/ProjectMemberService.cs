using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

// Item 34, next stage. Project-member lifecycle (add/update/bind/merge/
// list/apply_changes) and the durable operation-claim boundary, ported 1:1
// from Python app/services/project_member.py. Non-creator member additions
// route through the invitation adapter (ProjectInvitationAdapter).
public sealed class ProjectMemberService(MongoStore store, TimeProvider clock, IdentityPermissions permissions,
    IdentityAuditWriter audit, ProjectService projects, ProjectInvitationService invitations,
    InvitationMutationGate invitationGate, ObjectStorage storage, IConfiguration configuration)
{
    private IMongoCollection<BsonDocument> Members => store.Collection("project_member");
    private IMongoCollection<BsonDocument> Operations => store.Collection("identity_operation");

    // ---- helpers ----

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public async Task<BsonDocument> GetAsync(BsonDocument project, string memberId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(memberId, out var key)) throw IdentityErrors.MemberMissing();
        return await Members.Find(new BsonDocument { ["project"] = project["_id"], ["_id"] = key }).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.MemberMissing();
    }

    public async Task<BsonDocument?> ForUserAsync(BsonDocument project, ObjectId userId, CancellationToken ct) =>
        await Members.Find(new BsonDocument { ["project"] = project["_id"], ["user"] = userId }).FirstOrDefaultAsync(ct);

    public async Task<BsonDocument?> ActiveTeamMemberAsync(ObjectId userId, ObjectId teamId, CancellationToken ct) =>
        await store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = teamId, ["user"] = userId, ["status"] = "active"
        }).FirstOrDefaultAsync(ct);

    private static BsonDocument BsonMemberState(BsonDocument member, BsonDocument project) => new()
    {
        ["id"] = member["_id"].ToString(),
        ["project_id"] = project["_id"].ToString(),
        ["user_id"] = member.GetValue("user", BsonNull.Value) is { IsObjectId: true } u ? u.ToString() : BsonNull.Value,
        ["external_id"] = member.GetValue("external_id", BsonNull.Value),
        ["display_name"] = member["display_name"],
        ["tags"] = member["tags"].AsBsonArray.DeepClone(),
        ["status"] = member["status"],
        ["version"] = member.Integer("version")
    };

    private async Task SyncCountAsync(BsonDocument project, CancellationToken ct)
    {
        var count = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        await store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument("m_uc", count)), cancellationToken: ct);
    }

    private async Task<bool> IsManagerAsync(BsonDocument actor, BsonDocument project, CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (snapshot.Has("MANAGE_MEMBERS")) return true;
        var relation = await ActiveTeamMemberAsync(actor["_id"].AsObjectId, project["t"].AsObjectId, ct);
        return relation is not null && relation.Text("base_tag") is "creator" or "admin";
    }

    private async Task<bool> ActiveOperatorAsync(BsonDocument actor, BsonDocument project, CancellationToken ct)
    {
        if (await ActiveTeamMemberAsync(actor["_id"].AsObjectId, project["t"].AsObjectId, ct) is not null) return true;
        var member = await ForUserAsync(project, actor["_id"].AsObjectId, ct);
        return member is not null && member.Text("status") == "active";
    }

    private async Task RequireOperatorAsync(BsonDocument actor, BsonDocument project, CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!snapshot.Has("ACCESS")) throw ApiError.Forbidden();
        if (!IdentityPermissions.CanProject(snapshot, project, "ACCESS") || !await ActiveOperatorAsync(actor, project, ct))
            throw ApiError.Forbidden();
    }

    private async Task<bool> QualificationCheckDisabledAsync(BsonDocument project, CancellationToken ct)
    {
        var team = await projects.GetTeamAsync(project["t"].ToString()!, ct);
        return team.Text("wqm", "qualified") == "open";
    }

    private async Task<bool> IsTeamCreatorAsync(BsonDocument actor, BsonDocument project, CancellationToken ct)
    {
        var team = await projects.GetTeamAsync(project["t"].ToString()!, ct);
        return await projects.IsTeamCreatorAsync(actor, team, ct);
    }

    private async Task<HashSet<string>> LockedProjectTagsAsync(BsonDocument project, CancellationToken ct)
    {
        if (!project.Flag("ple")) return [];
        var policy = await store.Collection("identity_tag_policy")
            .Find(new BsonDocument("team", project["t"])).FirstOrDefaultAsync(ct);
        var builtIn = IdentityPermissions.SystemTagDefinitions("project").Names.ToHashSet(StringComparer.Ordinal);
        var locked = new HashSet<string>(StringComparer.Ordinal) { "admin" };
        if (policy is not null && policy.TryGetValue("project_tags", out var tags) && tags.IsBsonDocument)
            foreach (var element in tags.AsBsonDocument)
                if (!builtIn.Contains(element.Name))
                    locked.Add(element.Name);
        foreach (var tag in project.GetValue("plt", new BsonArray()).AsBsonArray)
            if (tag.IsString) locked.Add(tag.AsString);
        return locked;
    }

    private async Task EnsureTagsNotLockedAsync(BsonDocument project, BsonDocument actor, IEnumerable<string> tags, CancellationToken ct)
    {
        var locked = await LockedProjectTagsAsync(project, ct);
        if (locked.Count == 0 || !locked.Intersect(tags).Any()) return;
        if (!await IsTeamCreatorAsync(actor, project, ct))
            throw new ApiError("NoPermissionError", 103, "职位已被团队创建者锁定，仅团队创建者可以修改");
    }

    private async Task EnsureMemberNotLockedAsync(BsonDocument project, BsonDocument actor, BsonDocument member, CancellationToken ct)
    {
        var locked = await LockedProjectTagsAsync(project, ct);
        if (locked.Count == 0 || !locked.Intersect(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString)).Any())
            return;
        if (!await IsTeamCreatorAsync(actor, project, ct))
            throw new ApiError("NoPermissionError", 103, "该成员持有已被锁定的职位，仅团队创建者可以移除");
    }

    internal async Task<string[]> ValidateTagsAsync(BsonDocument actor, BsonDocument project, BsonDocument? target,
        JsonElement tags, bool external, bool allowCreator, CancellationToken ct)
    {
        var normalized = IdentityPermissions.NormalizeTags(tags);
        await EnsureTagsNotLockedAsync(project, actor, normalized, ct);
        return await permissions.ValidateProjectTagsAsync(actor, project, target, tags, external, allowCreator, ct);
    }

    private static string DisplayName(JsonElement? value, string? fallback)
    {
        var text = value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? fallback
            : value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        if (text is null) throw IdentityErrors.InvalidRequest("display_name must be a string");
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw IdentityErrors.InvalidRequest("display_name is required");
        if (trimmed.Length > 140) throw IdentityErrors.InvalidRequest("display_name is too long");
        return trimmed;
    }

    private async Task<string> AddUserDisplayNameAsync(BsonDocument project, BsonDocument user, JsonElement? displayName, CancellationToken ct)
    {
        var provided = displayName is { ValueKind: JsonValueKind.String } ? displayName.Value.GetString() : null;
        if (!string.IsNullOrWhiteSpace(provided) && provided.Trim() != user.Text("n", ""))
            return DisplayName(displayName, fallback: null);
        var team = await projects.GetTeamAsync(project["t"].ToString()!, ct);
        return DisplayName(null, await projects.DefaultDisplayNameAsync(team, user, ct));
    }

    private static void CheckAddState(BsonDocument project, bool external)
    {
        var status = project.Integer("st");
        if (status == 1 /* CLEARED */) throw IdentityErrors.StateConflict();
        if (!external && status != 0 /* NORMAL */) throw IdentityErrors.StateConflict();
    }

    private async Task<BsonDocument?> FindUserAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId) || !ObjectId.TryParse(userId, out var key)) return null;
        return await store.Collection("user").Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct);
    }

    private static bool DuplicateKey(MongoWriteException error) =>
        error.Message.Contains("11000", StringComparison.Ordinal);

    private static string? ExternalIdForOperation(ObjectId projectId, string operationId)
    {
        var name = $"moeflow:project-member:{projectId}:{operationId}";
        var ns = new byte[]
        {
            0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8
        };
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(ns.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var b = hash[..16];
        b[6] = (byte)((b[6] & 0x0f) | 0x50);
        b[8] = (byte)((b[8] & 0x3f) | 0x80);
        static string Hex(byte[] bytes, int offset, int count) =>
            Convert.ToHexString(bytes, offset, count).ToLowerInvariant();
        return $"{Hex(b, 0, 4)}-{Hex(b, 4, 2)}-{Hex(b, 6, 2)}-{Hex(b, 8, 2)}-{Hex(b, 10, 6)}";
    }

    /// <summary>Version-fenced save (Python save(save_condition={version})); returns the stored row.</summary>
    private async Task<BsonDocument?> SaveMemberAsync(BsonDocument member, BsonDocument changes, int expectedVersion, CancellationToken ct)
    {
        var set = new BsonDocument(changes) { ["edit_time"] = Now() };
        return await Members.FindOneAndUpdateAsync(IdentityErrors.VersionFilter(member, expectedVersion),
            new BsonDocument { ["$set"] = set, ["$inc"] = new BsonDocument("version", 1) },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct);
    }

    /// <summary>Unconditional save used by _bind_merge_into_user (python save() without a version condition).</summary>
    private async Task<BsonDocument?> SaveMemberUnconditionalAsync(BsonDocument member, BsonDocument changes, CancellationToken ct)
    {
        var set = new BsonDocument(changes) { ["edit_time"] = Now() };
        return await Members.FindOneAndUpdateAsync(new BsonDocument("_id", member["_id"]),
            new BsonDocument { ["$set"] = set, ["$inc"] = new BsonDocument("version", 1) },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct);
    }

    /// <summary>Best-effort merge/transfer rollback: restore snapshot fields, version included, fenced on expected+1.</summary>
    private async Task RollbackMemberAsync(ObjectId memberId, BsonDocument snapshot, int storedAfterVersion, CancellationToken ct)
    {
        var filter = new BsonDocument { ["_id"] = memberId, ["version"] = storedAfterVersion };
        var update = new BsonDocument { ["$set"] = new BsonDocument(snapshot) };
        await Members.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    /// <summary>Adapter entry point: ProjectInvitationAdapter.create_or_reuse.
    /// Persists the pending projection after the legacy Invitation call.</summary>
    private async Task<BsonDocument> AdapterInvitedAsync(BsonDocument project, BsonDocument actor, BsonDocument user, string[] tags,
        string displayName, string requestId, CancellationToken ct) =>
        await invitations.CreateOrReuseAsync(project, actor, user, tags.Select(t => (Flag: "", Value: t)).ToArray(), displayName, "", requestId, ct);

    /// <summary>Python ``_add_user`` self path: when the operator adds
    /// themselves, flips their pending invitations to ALLOW.</summary>
    private async Task AcceptPendingInvitationsAsync(BsonDocument project, BsonDocument user, CancellationToken ct) =>
        await invitations.AcceptPendingInvitationsAsync(project, user["_id"].AsObjectId, ct);

    // ---- add ----

    public async Task<BsonDocument> AddAsync(BsonDocument project, BsonDocument actor, JsonElement payload,
        string? operationId, string requestId, CancellationToken ct)
    {
        await RequireOperatorAsync(actor, project, ct);
        if (payload.ValueKind != JsonValueKind.Object) throw IdentityErrors.InvalidRequest();
        if (payload.TryGetProperty("member_id", out var memberIdProperty)
            && memberIdProperty.ValueKind is JsonValueKind.String)
            return await RestoreMemberAsync(project, actor, memberIdProperty.GetString()!, payload, requestId, ct);
        var userId = payload.TryGetProperty("user_id", out var userProperty)
            && userProperty.ValueKind == JsonValueKind.String ? userProperty.GetString() : null;
        var displayName = payload.TryGetProperty("display_name", out var nameProperty) ? nameProperty : default(JsonElement?);
        if ((userId is null || userId.Length == 0) && displayName is null)
            throw IdentityErrors.InvalidRequest("user_id or display_name is required");
        var tags = payload.TryGetProperty("tags", out var tagsProperty) ? tagsProperty : JsonSerializer.SerializeToElement(Array.Empty<string>());
        if (userId is not null && userId.Length > 0)
        {
            var user = await FindUserAsync(userId, ct) ?? throw IdentityErrors.UserMissing();
            return await AddUserAsync(project, actor, user, displayName, tags, requestId, operationId, ct);
        }
        var externalName = DisplayName(displayName, fallback: null);
        return await AddExternalAsync(project, actor, externalName, tags, requestId, operationId, ct);
    }

    private async Task<BsonDocument> RestoreMemberAsync(BsonDocument project, BsonDocument actor, string memberId,
        JsonElement payload, string requestId, CancellationToken ct)
    {
        var member = await GetAsync(project, memberId, ct);
        if (member.Text("status") != "removed") throw IdentityErrors.AlreadyExists();
        if (!await IsManagerAsync(actor, project, ct)) throw ApiError.Forbidden();
        await EnsureMemberNotLockedAsync(project, actor, member, ct);
        if (!payload.TryGetProperty("expected_member_version", out var expectedProperty)
            || expectedProperty.ValueKind != JsonValueKind.Number || !expectedProperty.TryGetInt32(out var expected))
            throw IdentityErrors.InvalidRequest("expected_member_version is required");
        if (member.Integer("version") != expected) throw IdentityErrors.MemberVersionConflict();
        string? userId = null;
        if (payload.TryGetProperty("user_id", out var userProperty) && userProperty.ValueKind is JsonValueKind.String)
        {
            userId = userProperty.GetString();
            var memberUser = member.GetValue("user", BsonNull.Value) is { IsObjectId: true } u ? u.ToString() : null;
            if (memberUser is null || memberUser != userId)
                throw IdentityErrors.InvalidRequest("user_id does not match the member");
        }
        else if (member.GetValue("user", BsonNull.Value) is { IsObjectId: true } existingUser)
            userId = existingUser.ToString();
        else if (payload.TryGetProperty("external_id", out var externalProperty) && externalProperty.ValueKind is JsonValueKind.String)
        {
            if (externalProperty.GetString() != member.Text("external_id"))
                throw IdentityErrors.InvalidRequest("external_id does not match the member");
        }
        CheckAddState(project, member.GetValue("user", BsonNull.Value).IsObjectId == false);
        var active = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        if (active >= project.Integer("u", 100000)) throw IdentityErrors.Capacity();
        var displayName = payload.TryGetProperty("display_name", out var nameProperty)
            ? DisplayName(nameProperty, fallback: null)
            : DisplayName(null, member.Text("display_name"));
        var tagsElement = payload.TryGetProperty("tags", out var tagsProperty)
            ? tagsProperty : JsonSerializer.SerializeToElement(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray());
        BsonDocument? target = null;
        if (userId is not null)
            target = await FindUserAsync(userId, ct);
        var tags = await ValidateTagsAsync(actor, project, target, tagsElement,
            external: member.GetValue("user", BsonNull.Value).IsObjectId == false, allowCreator: false, ct);
        var before = BsonMemberState(member, project);
        var changes = new BsonDocument
        {
            ["display_name"] = displayName,
            ["dns"] = SearchText.Normalize(displayName),
            ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t).ToArray()),
            ["status"] = "active",
            ["removed_time"] = BsonNull.Value
        };
        var saved = await SaveMemberAsync(member, changes, expected, ct) ?? throw IdentityErrors.MemberVersionConflict();
        await SyncCountAsync(project, ct);
        await audit.RecordProjectAsync(actor, "project_member_restore", project, saved, before,
            BsonMemberState(saved, project), requestId, ct);
        return saved;
    }

    private async Task<BsonDocument> AddExternalAsync(BsonDocument project, BsonDocument actor, string displayName,
        JsonElement tags, string requestId, string? operationId, CancellationToken ct)
    {
        if (!await IsManagerAsync(actor, project, ct) && IdentityPermissions.NormalizeTags(tags).Length > 0)
            throw ApiError.Forbidden();
        var normalized = await ValidateTagsAsync(actor, project, null, tags, external: true, allowCreator: false, ct);
        var operationExternalId = operationId is null ? null : ExternalIdForOperation(project["_id"].AsObjectId, operationId);
        if (operationExternalId is not null)
        {
            var existing = await Members.Find(new BsonDocument
            {
                ["project"] = project["_id"], ["external_id"] = operationExternalId
            }).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                await SyncCountAsync(project, ct);
                return existing;
            }
        }
        CheckAddState(project, external: true);
        var active = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        if (active >= project.Integer("u", 100000)) throw IdentityErrors.Capacity();
        var now = Now();
        var externalId = operationExternalId ?? Guid.NewGuid().ToString();
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["external_id"] = externalId,
            ["ik"] = $"{project["_id"]}:e:{externalId}",
            ["display_name"] = displayName,
            ["dns"] = SearchText.Normalize(displayName),
            ["tags"] = new BsonArray(normalized.Select(t => (BsonValue)t).ToArray()),
            ["status"] = "active",
            ["create_time"] = now,
            ["edit_time"] = now,
            ["version"] = 0
        };
        try
        {
            await Members.InsertOneAsync(member, cancellationToken: ct);
        }
        catch (MongoWriteException error) when (DuplicateKey(error))
        {
            var existing = await Members.Find(new BsonDocument
            {
                ["project"] = project["_id"], ["external_id"] = operationExternalId
            }).FirstOrDefaultAsync(ct);
            if (existing is not null) return existing;
            throw IdentityErrors.AlreadyExists();
        }
        await SyncCountAsync(project, ct);
        await audit.RecordProjectAsync(actor, "project_external_member_add", project, member,
            new BsonDocument(), BsonMemberState(member, project), requestId, ct);
        return member;
    }

    private async Task<BsonDocument> AddUserAsync(BsonDocument project, BsonDocument actor, BsonDocument user,
        JsonElement? displayName, JsonElement tags, string requestId, string? operationId, CancellationToken ct)
    {
        // Share the invitation gate per project so adapter create/reuse and
        // identity adds cannot race into duplicate pending invitations or
        // conflicting terminal projections. The operation claim stays outside.
        using var lease = await invitationGate.EnterAsync(project["_id"].AsObjectId, ct);
        project = await store.Collection("project").Find(new BsonDocument("_id", project["_id"]))
            .FirstOrDefaultAsync(ct) ?? project;
        var existing = await ForUserAsync(project, user["_id"].AsObjectId, ct);
        var teamRelation = await ActiveTeamMemberAsync(actor["_id"].AsObjectId, project["t"].AsObjectId, ct);
        var teamCreator = teamRelation is not null && teamRelation.Text("base_tag") == "creator";
        var manager = await IsManagerAsync(actor, project, ct);
        var canOpen = await QualificationCheckDisabledAsync(project, ct);
        var userId = user["_id"].AsObjectId;
        var tagsPresent = tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0;

        if (existing is not null && existing.Text("status") is "active" or "invited")
        {
            if (operationId is not null && existing.Text("status") == "invited")
            {
                CheckAddState(project, external: false);
                if (tagsPresent && !manager && actor["_id"] != user["_id"] && !canOpen) throw ApiError.Forbidden();
                var normalized = await ValidateTagsAsync(actor, project, user, tags, external: false, allowCreator: false, ct);
                var resolvedName = await AddUserDisplayNameAsync(project, user, displayName, ct);
                if (teamCreator || actor["_id"] == user["_id"])
                {
                    var activateBefore = BsonMemberState(existing, project);
                    var activateVersion = (int)existing.Integer("version");
                    var changes = new BsonDocument
                    {
                        ["status"] = "active",
                        ["tags"] = new BsonArray(normalized.Select(t => (BsonValue)t).ToArray()),
                        ["display_name"] = resolvedName,
                        ["dns"] = SearchText.Normalize(resolvedName),
                        ["removed_time"] = BsonNull.Value
                    };
                    var saved = await SaveMemberAsync(existing, changes, activateVersion, ct)
                        ?? throw IdentityErrors.MemberVersionConflict();
                    await SyncCountAsync(project, ct);
                    await audit.RecordProjectAsync(actor, "project_member_add", project, saved, activateBefore,
                        BsonMemberState(saved, project), requestId, ct);
                    // Python _add_user self path: also flips PENDING invitations
                    // of this project to ALLOW.
                    if (actor["_id"] == user["_id"]) await invitations.AcceptPendingInvitationsAsync(project, user["_id"].AsObjectId, ct);
                    return saved;
                }
                return await AdapterInvitedAsync(project, actor, user, normalized, resolvedName, requestId, ct);
            }
            throw IdentityErrors.AlreadyExists();
        }

        CheckAddState(project, external: false);
        if (tagsPresent && !manager && actor["_id"] != user["_id"] && !canOpen) throw ApiError.Forbidden();
        var validatedTags = await ValidateTagsAsync(actor, project, user, tags, external: false, allowCreator: false, ct);
        var active = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        if (active >= project.Integer("u", 100000)) throw IdentityErrors.Capacity();
        var resolved = await AddUserDisplayNameAsync(project, user, displayName, ct);

        if (existing is null)
        {
            if (teamCreator || actor["_id"] == user["_id"])
            {
                var now = Now();
                var fresh = new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["project"] = project["_id"],
                    ["user"] = user["_id"],
                    ["ik"] = $"{project["_id"]}:u:{user["_id"]}",
                    ["display_name"] = resolved,
                    ["dns"] = SearchText.Normalize(resolved),
                    ["tags"] = new BsonArray(validatedTags.Select(t => (BsonValue)t).ToArray()),
                    ["status"] = "active",
                    ["create_time"] = now,
                    ["edit_time"] = now,
                    ["version"] = 0
                };
                await Members.InsertOneAsync(fresh, cancellationToken: ct);
                await SyncCountAsync(project, ct);
                await audit.RecordProjectAsync(actor, "project_member_add", project, fresh,
                    new BsonDocument(), BsonMemberState(fresh, project), requestId, ct);
                if (actor["_id"] == user["_id"]) await invitations.AcceptPendingInvitationsAsync(project, user["_id"].AsObjectId, ct);
                return fresh;
            }
            return await AdapterInvitedAsync(project, actor, user, validatedTags, resolved, requestId, ct);
        }

        var before = BsonMemberState(existing, project);
        var previousVersion = (int)existing.Integer("version");
        var reuse = new BsonDocument
        {
            ["user"] = user["_id"],
            ["external_id"] = BsonNull.Value,
            ["display_name"] = resolved,
            ["dns"] = SearchText.Normalize(resolved),
            ["tags"] = new BsonArray(validatedTags.Select(t => (BsonValue)t).ToArray())
        };
        if (teamCreator || actor["_id"] == user["_id"])
        {
            reuse["status"] = "active";
            reuse["removed_time"] = BsonNull.Value;
            var saved = await SaveMemberAsync(existing, reuse, previousVersion, ct)
                ?? throw IdentityErrors.MemberVersionConflict();
            await SyncCountAsync(project, ct);
            await audit.RecordProjectAsync(actor, "project_member_add", project, saved, before,
                BsonMemberState(saved, project), requestId, ct);
            if (actor["_id"] == user["_id"]) await invitations.AcceptPendingInvitationsAsync(project, user["_id"].AsObjectId, ct);
            return saved;
        }
        return await AdapterInvitedAsync(project, actor, user, validatedTags, resolved, requestId, ct);
    }

    // ---- update ----

    public async Task<BsonDocument> UpdateAsync(BsonDocument project, BsonDocument actor, JsonElement payload, string requestId, CancellationToken ct)
    {
        await RequireOperatorAsync(actor, project, ct);
        if (payload.ValueKind != JsonValueKind.Object) throw IdentityErrors.InvalidRequest();
        var memberId = payload.TryGetProperty("member_id", out var memberIdProperty)
            && memberIdProperty.ValueKind == JsonValueKind.String ? memberIdProperty.GetString() : null;
        var member = await GetAsync(project, memberId ?? "", ct);
        if (!payload.TryGetProperty("expected_member_version", out var expectedProperty)
            || expectedProperty.ValueKind != JsonValueKind.Number || !expectedProperty.TryGetInt32(out var expected))
            throw IdentityErrors.InvalidRequest("expected_member_version is required");
        if (member.Integer("version") != expected) throw IdentityErrors.MemberVersionConflict();
        if (!payload.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Object
            || !changes.EnumerateObject().Any())
            throw IdentityErrors.InvalidRequest("changes is required");
        foreach (var property in changes.EnumerateObject())
            if (property.Name is not ("display_name" or "tags" or "status"))
                throw IdentityErrors.InvalidRequest("unsupported member fields");
        if (changes.TryGetProperty("status", out var statusProperty)
            && (statusProperty.ValueKind != JsonValueKind.String || statusProperty.GetString() != "removed"))
            throw IdentityErrors.InvalidRequest("only status=removed is supported");
        var removing = changes.TryGetProperty("status", out _);
        var memberUser = member.GetValue("user", BsonNull.Value) is { IsObjectId: true } u ? u.AsObjectId : (ObjectId?)null;
        if (removing) await EnsureMemberNotLockedAsync(project, actor, member, ct);
        var manager = await IsManagerAsync(actor, project, ct);
        var canOpen = await QualificationCheckDisabledAsync(project, ct);
        if (memberUser != actor["_id"].AsObjectId && !manager)
        {
            var tagsOnly = changes.EnumerateObject().All(p => p.Name == "tags");
            if (!(canOpen && changes.TryGetProperty("tags", out _) && tagsOnly)) throw ApiError.Forbidden();
        }
        if (memberUser is null && !manager) throw ApiError.Forbidden();
        if (memberUser is not null && memberUser == actor["_id"] && member.Text("status") != "active") throw ApiError.Forbidden();
        var isSelf = memberUser is not null && memberUser == actor["_id"].AsObjectId;
        var isOwner = memberUser is not null
            && project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou && ou.AsObjectId == memberUser.Value;
        if (isOwner)
        {
            if (removing) throw IdentityErrors.AlreadyOwner();
            if (changes.TryGetProperty("tags", out var ownerTags)
                && !IdentityPermissions.NormalizeTags(
                    ownerTags.ValueKind is JsonValueKind.Null ? JsonSerializer.SerializeToElement(Array.Empty<string>()) : ownerTags)
                    .Contains("creator"))
                throw IdentityErrors.AlreadyOwner();
        }
        var before = BsonMemberState(member, project);
        var update = new BsonDocument();
        if (changes.TryGetProperty("display_name", out var nameProperty))
        {
            var displayName = DisplayName(nameProperty, fallback: null);
            update["display_name"] = displayName;
            update["dns"] = SearchText.Normalize(displayName);
        }
        if (changes.TryGetProperty("tags", out var tagsProperty))
        {
            var tagsElement = tagsProperty.ValueKind is JsonValueKind.Null
                ? JsonSerializer.SerializeToElement(Array.Empty<string>()) : tagsProperty;
            var tags = await ValidateTagsAsync(actor, project,
                memberUser is null ? null : await FindUserAsync(memberUser.Value.ToString(), ct) ?? throw IdentityErrors.UserMissing(),
                tagsElement, external: memberUser is null, allowCreator: isOwner, ct);
            var beforeTags = before["tags"].AsBsonArray.Select(v => v.AsString).ToArray();
            if (isSelf)
            {
                var ownIdentity = beforeTags.Where(t => t is "creator" or "admin").ToHashSet(StringComparer.Ordinal);
                if (ownIdentity.Count > 0 && !ownIdentity.IsSubsetOf(tags)) throw IdentityErrors.ProtectedTag();
            }
            if (memberUser is not null && !isSelf)
            {
                var removedIdentity = beforeTags
                    .Where(t => t is "creator" or "admin" && !tags.Contains(t)).ToArray();
                if (removedIdentity.Length > 0 && !isOwner) throw IdentityErrors.ProtectedTag();
            }
            update["tags"] = new BsonArray(tags.Select(t => (BsonValue)t).ToArray());
        }
        if (removing)
        {
            update["status"] = "removed";
            update["removed_time"] = Now();
        }
        var saved = await SaveMemberAsync(member, update, expected, ct) ?? throw IdentityErrors.MemberVersionConflict();
        await SyncCountAsync(project, ct);
        await audit.RecordProjectAsync(actor, "project_member_update", project, saved, before,
            BsonMemberState(saved, project), requestId, ct);
        return saved;
    }

    // ---- bind ----

    public async Task<(BsonDocument Member, ObjectId AuditId)> BindMemberAsync(BsonDocument project, BsonDocument actor,
        string memberId, JsonElement payload, string requestId, CancellationToken ct)
    {
        await RequireOperatorAsync(actor, project, ct);
        if (!await IsManagerAsync(actor, project, ct)) throw ApiError.Forbidden();
        var member = await GetAsync(project, memberId, ct);
        if (!payload.TryGetProperty("expected_version", out var expectedProperty)
            || expectedProperty.ValueKind != JsonValueKind.Number || !expectedProperty.TryGetInt32(out var expected))
            throw IdentityErrors.InvalidRequest("expected_version is required");
        if (member.Integer("version") != expected) throw IdentityErrors.MemberVersionConflict();
        if (member.Text("status") is not ("active" or "removed")
            || member.GetValue("user", BsonNull.Value).IsObjectId
            || string.IsNullOrEmpty(member.Text("external_id")))
            throw IdentityErrors.InvalidRequest("only an external member can bind");
        var user = await FindUserAsync(
            payload.TryGetProperty("user_id", out var userProperty) && userProperty.ValueKind == JsonValueKind.String
                ? userProperty.GetString() : null, ct) ?? throw IdentityErrors.UserMissing();
        if (await ActiveTeamMemberAsync(user["_id"].AsObjectId, project["t"].AsObjectId, ct) is null)
            throw ApiError.Forbidden();
        var other = await ForUserAsync(project, user["_id"].AsObjectId, ct);
        if (other is not null)
            return await BindMergeIntoUserAsync(project, actor, member, other, user, requestId, ct);
        var tags = await ValidateTagsAsync(actor, project, user,
            JsonSerializer.SerializeToElement(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray()),
            external: false, allowCreator: false, ct);
        var before = BsonMemberState(member, project);
        var oldExternalId = member.Text("external_id");
        var changes = new BsonDocument
        {
            ["user"] = user["_id"],
            ["external_id"] = BsonNull.Value,
            ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t).ToArray()),
            ["status"] = "active",
            ["removed_time"] = BsonNull.Value
        };
        BsonDocument saved;
        try
        {
            saved = await SaveMemberAsync(member, changes, expected, ct) ?? throw IdentityErrors.MemberVersionConflict();
        }
        catch (MongoWriteException error) when (DuplicateKey(error))
        {
            throw IdentityErrors.MergeRequired();
        }
        await SyncCountAsync(project, ct);
        var id = await audit.RecordProjectAsync(actor, "project_external_member_bind", project, saved,
            new BsonDocument(before) { ["external_id"] = oldExternalId }, BsonMemberState(saved, project), requestId, ct);
        return (saved, id);
    }

    private async Task<(BsonDocument Member, ObjectId AuditId)> BindMergeIntoUserAsync(BsonDocument project, BsonDocument actor,
        BsonDocument source, BsonDocument target, BsonDocument user, string requestId, CancellationToken ct)
    {
        if (source.Text("status") is not ("active" or "removed") || source.GetValue("user", BsonNull.Value).IsObjectId
            || string.IsNullOrEmpty(source.Text("external_id")))
            throw IdentityErrors.InvalidRequest("only an external member can bind");
        if (target.Text("status") is not ("active" or "invited") || !target.GetValue("user", BsonNull.Value).IsObjectId)
            throw IdentityErrors.InvalidRequest("target must be a registered project member");
        if (await ActiveTeamMemberAsync(target["user"].AsObjectId, project["t"].AsObjectId, ct) is null)
            throw ApiError.Forbidden();
        var merged = target.GetValue("tags", new BsonArray()).AsBsonArray
            .Select(v => v.AsString).Concat(source.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString))
            .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var isOwnerTarget = project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou
            && ou.AsObjectId == target["user"].AsObjectId;
        var tags = await ValidateTagsAsync(actor, project, user,
            JsonSerializer.SerializeToElement(merged), external: false, allowCreator: isOwnerTarget, ct);
        if (isOwnerTarget && !tags.Contains("creator")) throw IdentityErrors.AlreadyOwner();
        var beforeSource = BsonMemberState(source, project);
        var beforeTarget = BsonMemberState(target, project);
        var changes = new BsonDocument
        {
            ["display_name"] = source["display_name"],
            ["dns"] = source.GetValue("dns", BsonNull.Value),
            ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t).ToArray())
        };
        var saved = await SaveMemberUnconditionalAsync(target, changes, ct) ?? throw IdentityErrors.AlreadyExists();
        await Members.DeleteOneAsync(new BsonDocument("_id", source["_id"]), cancellationToken: ct);
        await SyncCountAsync(project, ct);
        var before = new BsonDocument { ["source"] = beforeSource, ["target"] = beforeTarget };
        var after = new BsonDocument
        {
            ["source"] = new BsonDocument(beforeSource) { ["status"] = "deleted" },
            ["target"] = BsonMemberState(saved, project)
        };
        var id = await audit.RecordProjectAsync(actor, "project_external_member_bind_merge", project, saved, before, after, requestId, ct);
        return (saved, id);
    }

    // ---- hard delete ----

    public async Task<ObjectId> HardDeleteExternalAsync(BsonDocument project, BsonDocument actor,
        string memberId, string requestId, CancellationToken ct)
    {
        await RequireOperatorAsync(actor, project, ct);
        var isProjectOwner = project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou
            && ou.AsObjectId == actor["_id"].AsObjectId;
        if (!isProjectOwner && !await IsTeamCreatorAsync(actor, project, ct)) throw ApiError.Forbidden();
        var member = await GetAsync(project, memberId, ct);
        if (member.GetValue("user", BsonNull.Value).IsObjectId || string.IsNullOrEmpty(member.Text("external_id")))
            throw IdentityErrors.InvalidRequest("only an external member can be hard-deleted");
        var before = BsonMemberState(member, project);
        await Members.DeleteOneAsync(new BsonDocument("_id", member["_id"]), cancellationToken: ct);
        await SyncCountAsync(project, ct);
        return await audit.RecordProjectAsync(actor, "project_external_member_hard_delete", project, member,
            before, new BsonDocument(before) { ["status"] = "deleted" }, requestId, ct);
    }

    // ---- merge ----

    public async Task<(BsonDocument Target, BsonDocument Source, ObjectId AuditId)> MergeAsync(BsonDocument project,
        BsonDocument actor, string sourceMemberId, JsonElement payload, string requestId, CancellationToken ct)
    {
        await RequireOperatorAsync(actor, project, ct);
        if (!await IsManagerAsync(actor, project, ct)) throw ApiError.Forbidden();
        if (payload.ValueKind != JsonValueKind.Object) throw IdentityErrors.InvalidRequest();
        var source = await GetAsync(project, sourceMemberId, ct);
        var targetMemberId = payload.TryGetProperty("target_member_id", out var targetProperty)
            && targetProperty.ValueKind == JsonValueKind.String ? targetProperty.GetString() : null;
        var target = await GetAsync(project, targetMemberId ?? "", ct);
        if (source["_id"] == target["_id"]) throw IdentityErrors.InvalidRequest("source and target must differ");
        if (!payload.TryGetProperty("expected_source_version", out var sourceVersionProperty)
            || sourceVersionProperty.ValueKind != JsonValueKind.Number || !sourceVersionProperty.TryGetInt32(out var expectedSource)
            || !payload.TryGetProperty("expected_target_version", out var targetVersionProperty)
            || targetVersionProperty.ValueKind != JsonValueKind.Number || !targetVersionProperty.TryGetInt32(out var expectedTarget))
            throw IdentityErrors.InvalidRequest("expected_source_version and expected_target_version are required");
        if (source.Integer("version") != expectedSource || target.Integer("version") != expectedTarget)
            throw IdentityErrors.MemberVersionConflict();
        if (source.Text("status") != "active" || source.GetValue("user", BsonNull.Value).IsObjectId
            || string.IsNullOrEmpty(source.Text("external_id")))
            throw IdentityErrors.InvalidRequest("source must be an active external member");
        if (target.Text("status") is not ("active" or "invited") || !target.GetValue("user", BsonNull.Value).IsObjectId)
            throw IdentityErrors.InvalidRequest("target must be a registered project member");
        if (await ActiveTeamMemberAsync(target["user"].AsObjectId, project["t"].AsObjectId, ct) is null)
            throw ApiError.Forbidden();
        var displayName = payload.TryGetProperty("display_name", out var nameProperty)
            ? DisplayName(nameProperty.ValueKind is JsonValueKind.Null ? null : nameProperty, fallback: target.Text("display_name"))
            : DisplayName(null, target.Text("display_name"));
        if (!payload.TryGetProperty("tags", out var tagsProperty) || tagsProperty.ValueKind != JsonValueKind.Array)
            throw IdentityErrors.InvalidRequest("tags must be an array");
        var targetUser = await FindUserAsync(target["user"].ToString(), ct)
            ?? throw IdentityErrors.UserMissing();
        var tags = await ValidateTagsAsync(actor, project, targetUser, tagsProperty, external: false, allowCreator: false, ct);
        if (project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } owner
            && owner.AsObjectId == target["user"].AsObjectId && !tags.Contains("creator"))
            throw IdentityErrors.AlreadyOwner();
        var beforeSource = BsonMemberState(source, project);
        var beforeTarget = BsonMemberState(target, project);
        var sourceSnapshot = new BsonDocument
        {
            ["status"] = source["status"],
            ["version"] = source.Integer("version"),
            ["edit_time"] = source["edit_time"],
            ["removed_time"] = source.GetValue("removed_time", BsonNull.Value)
        };
        var targetSnapshot = new BsonDocument
        {
            ["display_name"] = target["display_name"],
            ["tags"] = target["tags"].AsBsonArray.DeepClone(),
            ["version"] = target.Integer("version"),
            ["edit_time"] = target["edit_time"],
            ["removed_time"] = target.GetValue("removed_time", BsonNull.Value)
        };
        var targetSaved = false;
        var sourceSaved = false;
        BsonDocument savedTarget;
        BsonDocument savedSource;
        try
        {
            var targetChanges = new BsonDocument
            {
                ["display_name"] = displayName,
                ["dns"] = SearchText.Normalize(displayName),
                ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t).ToArray())
            };
            savedTarget = await SaveMemberAsync(target, targetChanges, expectedTarget, ct)
                ?? throw IdentityErrors.MemberVersionConflict();
            targetSaved = true;
            var sourceChanges = new BsonDocument
            {
                ["status"] = "removed",
                ["removed_time"] = Now()
            };
            savedSource = await SaveMemberAsync(source, sourceChanges, expectedSource, ct)
                ?? throw IdentityErrors.MemberVersionConflict();
            sourceSaved = true;
        }
        catch
        {
            if (sourceSaved) await RollbackMemberAsync(source["_id"].AsObjectId, sourceSnapshot, expectedSource + 1, ct);
            if (targetSaved) await RollbackMemberAsync(target["_id"].AsObjectId, targetSnapshot, expectedTarget + 1, ct);
            throw;
        }
        await SyncCountAsync(project, ct);
        var before = new BsonDocument { ["source"] = beforeSource, ["target"] = beforeTarget };
        var after = new BsonDocument
        {
            ["source"] = BsonMemberState(savedSource, project),
            ["target"] = BsonMemberState(savedTarget, project)
        };
        var id = await audit.RecordProjectAsync(actor, "project_external_member_merge", project, savedTarget, before, after, requestId, ct);
        return (savedTarget, savedSource, id);
    }

    // ---- list / to_api ----

    public async Task<object[]> ListMembersAsync(BsonDocument project, BsonDocument actor,
        string? status, string? tag, string? word, CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!snapshot.Has("ACCESS")) throw ApiError.Forbidden();
        var statuses = string.IsNullOrEmpty(status)
            ? new[] { "active" }
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (statuses.Length == 0) statuses = ["active"];
        var rows = await Members.Find(new BsonDocument
        {
            ["project"] = project["_id"],
            ["status"] = new BsonDocument("$in", new BsonArray(statuses.Select(s => (BsonValue)s).ToArray()))
        }).ToListAsync(ct);
        if (!string.IsNullOrEmpty(tag))
            rows = rows.Where(m => m.GetValue("tags", new BsonArray()).AsBsonArray
                .Any(v => v.IsString && v.AsString == tag)).ToList();
        var needle = SearchText.Normalize(word ?? "");
        var memberUserIds = rows
            .Where(m => m.GetValue("user", BsonNull.Value).IsObjectId)
            .Select(m => m["user"].AsObjectId).Distinct().ToArray();
        var memberUsers = memberUserIds.Length == 0 ? new Dictionary<ObjectId, BsonDocument>()
            : (await store.Collection("user").Find(new BsonDocument("_id",
                new BsonDocument("$in", new BsonArray(memberUserIds.Select(id => (BsonValue)id).ToArray())))).ToListAsync(ct))
                .ToDictionary(u => u["_id"].AsObjectId);
        if (needle.Length > 0)
        {
            var teamMembers = await store.Collection("team_member").Find(new BsonDocument
            {
                ["team"] = project["t"], ["status"] = "active"
            }).ToListAsync(ct);
            var relations = teamMembers
                .Where(m => m.GetValue("user", BsonNull.Value).IsObjectId)
                .ToDictionary(m => m["user"].AsObjectId, m => m);
            var users = memberUsers;
            rows = rows.Where(member =>
            {
                var values = new List<string> { member.Text("display_name") };
                if (member.GetValue("user", BsonNull.Value) is { IsObjectId: true } userId)
                {
                    if (users.TryGetValue(userId.AsObjectId, out var user))
                    {
                        values.Add(user.Text("n"));
                        values.AddRange(user.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString));
                    }
                    if (relations.TryGetValue(userId.AsObjectId, out var relation))
                        values.AddRange(relation.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString));
                }
                return values.Any(v => SearchText.Normalize(v).Contains(needle, StringComparison.Ordinal));
            }).ToList();
        }
        rows.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a.Text("display_name"), b.Text("display_name"), StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a["_id"].ToString(), b["_id"].ToString(), StringComparison.Ordinal);
        });
        // app/services/project_member.py:1050-1085 resolves each member's user
        // and derives is_owner; passing null here made every row look external.
        var result = new List<object>(rows.Count);
        foreach (var m in rows)
        {
            var user = m.GetValue("user", BsonNull.Value).IsObjectId
                && memberUsers.TryGetValue(m["user"].AsObjectId, out var found) ? found : null;
            result.Add(MemberToApi(m, project, user, includePermissions: false,
                avatar: user is null ? null : await AvatarUrlAsync(user)));
        }
        return result.ToArray();
    }

    public async Task<object> MemberToApiAsync(BsonDocument member, BsonDocument project, CancellationToken ct)
    {
        BsonDocument? user = null;
        PermissionSnapshot? snapshot = null;
        if (member.GetValue("user", BsonNull.Value) is { IsObjectId: true } userId)
        {
            user = await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct);
            if (user is not null) snapshot = await permissions.ProjectSnapshotAsync(user, project, ct);
        }
        return MemberToApi(member, project, user, includePermissions: true, snapshot,
            user is null ? null : await AvatarUrlAsync(user));
    }

    private async Task<string?> AvatarUrlAsync(BsonDocument user)
    {
        var name = user.Text("a");
        if (name.Length == 0) return configuration["Storage:DefaultUserAvatar"];
        return await storage.SignGetAsync(AvatarStoragePrefixes.User(configuration) + name,
            ObjectStorage.DefaultSignedUrlValidity);
    }

    private static Dictionary<string, object?> MemberToApi(BsonDocument member, BsonDocument project, BsonDocument? user,
        bool includePermissions, PermissionSnapshot? snapshot = null, string? avatar = null)
    {
        var tags = member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray();
        var owner = user is not null
            && project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou
            && ou.AsObjectId == user["_id"].AsObjectId
            && member.Text("status", "active") == "active" && tags.Contains("creator");
        string? userId = user is null ? null : user["_id"].ToString();
        Dictionary<string, object?>? userView = user is null ? null : new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["name"] = user.Text("n"),
            ["avatar"] = avatar,
            ["has_avatar"] = user.Text("a").Length != 0,
            ["aliases"] = user.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray()
        };
        return new Dictionary<string, object?>
        {
            ["id"] = member["_id"].ToString(),
            ["project_id"] = project["_id"].ToString(),
            ["user_id"] = userId,
            ["external_id"] = member.GetValue("external_id", BsonNull.Value).IsString ? member.Text("external_id") : null,
            ["user"] = userView,
            ["display_name"] = member.Text("display_name"),
            ["tags"] = tags,
            ["effective_permissions"] = includePermissions && snapshot is not null
                ? snapshot.EffectivePermissions.Order(StringComparer.Ordinal).ToArray() : [],
            ["status"] = member.Text("status", "active"),
            ["version"] = member.Integer("version"),
            ["is_owner"] = owner,
            ["create_time"] = member.Timestamp("create_time"),
            ["edit_time"] = member.Timestamp("edit_time")
        };
    }

    // ---- operation claim + apply_changes ----

    private static BsonDocument ExceptionData(ApiError error) => new()
    {
        ["code"] = error.IdentityCode ?? (error.Error == "NoPermissionError" ? "NO_PERMISSION" : "INVALID_REQUEST"),
        ["error_code"] = error.Code,
        ["message"] = error.Message,
        ["http_status"] = error.Status
    };

    private static BsonDocument ExceptionData(Exception error) => new()
    {
        ["code"] = "INVALID_REQUEST",
        ["error_code"] = BsonNull.Value,
        ["message"] = error.Message,
        ["http_status"] = 400
    };

    private async Task<(BsonDocument Stored, bool Claimed, string? Token)> ClaimOperationAsync(
        BsonDocument project, string operationId, CancellationToken ct)
    {
        var now = Now();
        var lease = now.AddSeconds(300);
        var token = Guid.NewGuid().ToString();
        var stored = await Operations.Find(new BsonDocument
        {
            ["project"] = project["_id"], ["operation_id"] = operationId
        }).FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            stored = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["project"] = project["_id"],
                ["operation_id"] = operationId,
                ["status"] = "processing",
                ["error_code"] = BsonNull.Value,
                ["result"] = new BsonDocument(),
                ["claim_token"] = token,
                ["lease_expires_at"] = lease,
                ["edit_time"] = now,
                ["create_time"] = now
            };
            try
            {
                await Operations.InsertOneAsync(stored, cancellationToken: ct);
                return (stored, true, token);
            }
            catch (MongoWriteException error) when (DuplicateKey(error))
            {
                stored = await Operations.Find(new BsonDocument
                {
                    ["project"] = project["_id"], ["operation_id"] = operationId
                }).FirstOrDefaultAsync(ct);
                if (stored is null) throw IdentityErrors.InProgress();
            }
        }
        if (stored.Text("status") == "succeeded") return (stored, false, null);
        if (stored.Text("status") == "processing")
        {
            var leaseExpires = stored.GetValue("lease_expires_at", BsonNull.Value) is BsonDateTime l
                ? (DateTime?)l.ToUniversalTime() : null;
            var touched = stored.GetValue("edit_time", stored.GetValue("create_time", BsonNull.Value)) is BsonDateTime t
                ? t.ToUniversalTime() : (DateTime?)null;
            var stale = (leaseExpires is not null && leaseExpires.Value <= now)
                || (leaseExpires is null && touched is not null && touched.Value <= now.AddSeconds(-300));
            if (!stale) throw IdentityErrors.InProgress();
            var filter = new BsonDocument
            {
                ["_id"] = stored["_id"],
                ["status"] = "processing",
                ["edit_time"] = stored.GetValue("edit_time", BsonNull.Value),
                ["lease_expires_at"] = stored.GetValue("lease_expires_at", BsonNull.Value),
                ["claim_token"] = stored.GetValue("claim_token", BsonNull.Value)
            };
            var update = new BsonDocument("$set", new BsonDocument
            {
                ["status"] = "processing",
                ["error_code"] = BsonNull.Value,
                ["result"] = new BsonDocument(),
                ["edit_time"] = now,
                ["lease_expires_at"] = lease,
                ["claim_token"] = token
            });
            var result = await Operations.UpdateOneAsync(filter, update, cancellationToken: ct);
            if (result.ModifiedCount != 1)
            {
                var current = await Operations.Find(new BsonDocument("_id", stored["_id"])).FirstOrDefaultAsync(ct);
                if (current is not null && current.Text("status") == "succeeded") return (current, false, null);
                throw IdentityErrors.InProgress();
            }
            stored = await Operations.Find(new BsonDocument("_id", stored["_id"])).FirstOrDefaultAsync(ct)
                ?? throw IdentityErrors.InProgress();
            return (stored, true, token);
        }
        var reclaim = await Operations.UpdateOneAsync(
            new BsonDocument { ["_id"] = stored["_id"], ["status"] = "failed" },
            new BsonDocument("$set", new BsonDocument
            {
                ["status"] = "processing",
                ["error_code"] = BsonNull.Value,
                ["result"] = new BsonDocument(),
                ["edit_time"] = now,
                ["lease_expires_at"] = lease,
                ["claim_token"] = token
            }), cancellationToken: ct);
        if (reclaim.ModifiedCount != 1)
        {
            var current = await Operations.Find(new BsonDocument("_id", stored["_id"])).FirstOrDefaultAsync(ct);
            if (current is not null && current.Text("status") == "succeeded") return (current, false, null);
            throw IdentityErrors.InProgress();
        }
        stored = await Operations.Find(new BsonDocument("_id", stored["_id"])).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.InProgress();
        return (stored, true, token);
    }

    private async Task<BsonDocument> FinishOperationAsync(BsonDocument stored, string token, string status,
        BsonDocument result, string? errorCode, CancellationToken ct)
    {
        var updated = await Operations.UpdateOneAsync(
            new BsonDocument { ["_id"] = stored["_id"], ["status"] = "processing", ["claim_token"] = token },
            new BsonDocument("$set", new BsonDocument
            {
                ["status"] = status,
                ["error_code"] = errorCode is null ? BsonNull.Value : errorCode,
                ["result"] = result,
                ["edit_time"] = Now(),
                ["lease_expires_at"] = BsonNull.Value,
                ["claim_token"] = BsonNull.Value
            }), cancellationToken: ct);
        if (updated.ModifiedCount == 1) return result;
        var current = await Operations.Find(new BsonDocument("_id", stored["_id"])).FirstOrDefaultAsync(ct);
        if (current is not null && current.Text("status") is "succeeded" or "failed")
            return current.GetValue("result", new BsonDocument()).AsBsonDocument;
        throw IdentityErrors.InProgress();
    }

    private static Dictionary<string, object?> OperationResult(BsonDocument result)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var element in result)
            dict[element.Name] = element.Value switch
            {
                BsonString s => s.Value,
                BsonBoolean b => b.Value,
                BsonInt32 i => i.Value,
                BsonInt64 l => l.Value,
                BsonDouble d => d.Value,
                BsonNull => null,
                BsonDateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff"),
                BsonArray a => a.Select(v => v switch
                {
                    BsonString s => (object?)s.Value,
                    BsonInt32 i => i.Value,
                    BsonBoolean b => b.Value,
                    _ => v.ToString()
                }).ToArray(),
                BsonDocument d => OperationResult(d),
                _ => element.Value.ToString()
            };
        return dict;
    }

    public async Task<(BsonDocument Body, int HttpStatus)> ApplyChangesAsync(BsonDocument project, BsonDocument actor,
        JsonElement payload, string requestId, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            throw IdentityErrors.InvalidRequest("operations must be an array");
        await RequireOperatorAsync(actor, project, ct);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object) throw IdentityErrors.InvalidRequest();
            if (!operation.TryGetProperty("operation_id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idProperty.GetString()))
                throw IdentityErrors.InvalidRequest("operation_id is required");
            if (!operationIds.Add(idProperty.GetString()!))
                throw IdentityErrors.InvalidRequest("duplicate operation_id");
        }
        var results = new List<Dictionary<string, object?>>();
        var body = new BsonDocument();
        var resultRows = new BsonArray();
        foreach (var operation in operations.EnumerateArray())
        {
            var operationId = operation.GetProperty("operation_id").GetString()!;
            var (stored, claimed, token) = await ClaimOperationAsync(project, operationId, ct);
            if (!claimed)
            {
                var replayed = stored.GetValue("result", new BsonDocument()).AsBsonDocument;
                results.Add(OperationResult(replayed));
                resultRows.Add(replayed);
                continue;
            }
            try
            {
                var action = operation.TryGetProperty("action", out var actionProperty)
                    && actionProperty.ValueKind == JsonValueKind.String ? actionProperty.GetString() : null;
                BsonDocument member;
                if (action == "add")
                    member = await AddAsync(project, actor, operation, operationId, requestId, ct);
                else if (action == "update")
                    member = await UpdateAsync(project, actor, operation, requestId, ct);
                else
                    throw IdentityErrors.InvalidRequest("unsupported member action");
                var opResult = new BsonDocument
                {
                    ["operation_id"] = operationId,
                    ["status"] = "succeeded",
                    ["member_id"] = member["_id"].ToString(),
                    ["member_status"] = member.Text("status"),
                    ["member_version"] = member.Integer("version")
                };
                opResult = await FinishOperationAsync(stored, token!, "succeeded", opResult, null, ct);
                results.Add(OperationResult(opResult));
                resultRows.Add(opResult);
            }
            catch (ApiError error)
            {
                var details = ExceptionData(error);
                var failed = new BsonDocument
                {
                    ["operation_id"] = operationId,
                    ["status"] = "failed"
                };
                foreach (var element in details) failed.Add(element);
                failed = await FinishOperationAsync(stored, token!, "failed", failed,
                    details["code"] is { IsString: true } code ? code.AsString : null, ct);
                if (failed.Text("status") == "succeeded")
                {
                    results.Add(OperationResult(failed));
                    resultRows.Add(failed);
                    continue;
                }
                body["results"] = resultRows;
                body["failed"] = failed;
                body["stopped"] = true;
                body["http_status"] = details["http_status"];
                return (body, details["http_status"].AsInt32);
            }
            catch (Exception error)
            {
                var details = ExceptionData(error);
                var failed = new BsonDocument
                {
                    ["operation_id"] = operationId,
                    ["status"] = "failed"
                };
                foreach (var element in details) failed.Add(element);
                failed = await FinishOperationAsync(stored, token!, "failed", failed,
                    details["code"] is { IsString: true } code2 ? code2.AsString : null, ct);
                if (failed.Text("status") == "succeeded")
                {
                    results.Add(OperationResult(failed));
                    resultRows.Add(failed);
                    continue;
                }
                body["results"] = resultRows;
                body["failed"] = failed;
                body["stopped"] = true;
                body["http_status"] = details["http_status"];
                return (body, details["http_status"].AsInt32);
            }
        }
        body["results"] = resultRows;
        body["failed"] = BsonNull.Value;
        body["stopped"] = false;
        return (body, 200);
    }
}
