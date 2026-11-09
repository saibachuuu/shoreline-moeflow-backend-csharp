using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Project-member projection for the existing invitation lifecycle.
// Ported from Python app/services/project_invitation.py.
public sealed class ProjectInvitationService(
    MongoStore store,
    IdentityPermissions permissions,
    IdentityAuditWriter audit)
{
    public const int PendingStatus = 1;
    public const int AllowStatus = 2;
    public const int DenyStatus = 3;

    private IMongoCollection<BsonDocument> Invitations => store.Collection("invitation");
    private IMongoCollection<BsonDocument> Members => store.Collection("project_member");

    public static BsonDocument GenericRef(string cls, string collection, ObjectId id) => new()
    {
        ["_cls"] = cls,
        ["_ref"] = new BsonDocument { ["$ref"] = collection, ["$id"] = id }
    };

    // A GenericReferenceField persists as {"_cls": ..., "_ref": DBRef}; the
    // value of _ref may also be the DBRef document itself ({"$ref", "$id"}).
    public static ObjectId? RefId(BsonValue? value)
    {
        var candidate = value?.IsBsonDocument is true && value.AsBsonDocument.TryGetValue("_ref", out var b) ? b : value;
        if (candidate is BsonObjectId oid) return oid.Value;
        if (candidate is not BsonDocument doc) return null;
        if (doc.TryGetValue("$id", out var id) && id is BsonObjectId nested) return nested.Value;
        if (doc.TryGetValue("$oid", out var ext) && ObjectId.TryParse(ext.ToString(), out var parsed)) return parsed;
        return null;
    }

    public static ObjectId? InvitationOperatorId(BsonDocument invitation) => RefId(invitation.GetValue("o", BsonNull.Value));
    public static ObjectId? InvitationGroupId(BsonDocument invitation) => RefId(invitation.GetValue("g", BsonNull.Value));

    // Python: Invitation.objects(user=..., group=..., status=PENDING).order_by("-id").first()
    public async Task<BsonDocument?> FindPendingInvitationAsync(BsonDocument project, ObjectId userId, CancellationToken ct)
    {
        if (project is null) return null;
        return await Invitations.Find(new BsonDocument
        {
            ["u"] = userId,
            ["g"] = GenericRef("Project", "project", project["_id"].AsObjectId),
            ["s"] = PendingStatus
        }).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(ct);
    }

    public async Task<BsonDocument> GetInvitationAsync(string id, CancellationToken ct)
    {
        if (ObjectId.TryParse(id, out var oid))
        {
            var doc = await Invitations.Find(new BsonDocument("_id", oid)).FirstOrDefaultAsync(ct);
            if (doc is not null) return doc;
        }
        throw IdentityErrors.InvitationNotExist();
    }

    public async Task<BsonDocument> CreatePendingInvitationAsync(BsonDocument project, BsonDocument operatorUser,
        ObjectId userId, BsonValue roleRef, string message, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["u"] = userId,
            ["o"] = operatorUser["_id"],
            ["g"] = GenericRef("Project", "project", project["_id"].AsObjectId),
            ["r"] = roleRef,
            ["s"] = PendingStatus,
            ["m"] = message ?? "",
            ["c"] = now
        };
        try
        {
            await Invitations.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException)
        {
            var existing = await FindPendingInvitationAsync(project, userId, ct);
            if (existing is not null) return existing;
            throw;
        }
        return doc;
    }

    // Python ProjectMemberService.team_default_display_name — the user's
    // per-team alias, falling back to the site (current) name.
    internal static string TeamDefaultDisplayName(BsonDocument? teamMember, BsonDocument user)
    {
        if (teamMember is not null && teamMember.TryGetValue("default_display_name", out var name) && name is BsonString alias && !string.IsNullOrWhiteSpace(alias.Value))
            return alias.Value;
        return user.Text("n");
    }

    internal async Task<BsonDocument?> ActiveTeamMemberAsync(ObjectId userId, ObjectId teamId, CancellationToken ct) =>
        await store.Collection("team_member").Find(new BsonDocument
        {
            ["user"] = userId, ["team"] = teamId, ["status"] = "active"
        }).FirstOrDefaultAsync(ct);

    internal async Task<BsonDocument?> MemberAsync(BsonDocument project, ObjectId userId, CancellationToken ct) =>
        await Members.Find(new BsonDocument
        {
            ["project"] = project["_id"], ["user"] = userId
        }).FirstOrDefaultAsync(ct);

    internal static BsonDocument MemberState(BsonDocument member, BsonDocument project) => new()
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

    internal async Task SyncCountAsync(BsonDocument project, CancellationToken ct)
    {
        var count = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        await store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument("m_uc", count)), cancellationToken: ct);
    }

    /// <summary>
    /// The legacy ``_ensure_projection``. A fresh projection without a
    /// display_name falls back to the user's team default display name.
    /// </summary>
    internal async Task<BsonDocument> EnsureProjectionAsync(BsonDocument project, ObjectId userId,
        (string Flag, string Value)[] tags, string? displayName, string status,
        BsonDocument? operatorUser, string requestId, CancellationToken ct)
    {
        var member = await MemberAsync(project, userId, ct);
        var before = member is null ? new BsonDocument() : MemberState(member, project);
        var previousVersion = member is null ? (int?)null : (int)member.Integer("version");
        var desiredName = string.IsNullOrEmpty(displayName) ? (member is null ? await TeamDefaultNameAsync(project, userId, ct) : member.Text("display_name")) : displayName;
        if (member is null)
        {
            var fresh = NewMember(project, userId, desiredName, tags, status);
            try
            {
                await Members.InsertOneAsync(fresh, cancellationToken: ct);
                member = fresh;
            }
            catch (MongoWriteException)
            {
                var existing = await MemberAsync(project, userId, ct);
                if (existing is null) throw;
                return existing;
            }
        }
        else
        {
            var tagsSet = System.Linq.Enumerable.ToList(tags.Select(t => t.Value));
            var memberTags = System.Linq.Enumerable.ToList(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString));
            if (status is "invited" or "removed" && member.Text("status") == "active") return member;
            if (member.Text("status") == status && member.Text("display_name") == desiredName &&
                memberTags.SequenceEqual(tagsSet))
            {
                await SyncCountAsync(project, ct);
                return member;
            }
            var now = DateTime.UtcNow;
            var changes = new BsonDocument
            {
                ["display_name"] = desiredName,
                ["dns"] = SearchText.Normalize(desiredName),
                ["tags"] = new BsonArray(tagsSet.Select(t => (BsonValue)t)),
                ["status"] = status,
                ["removed_time"] = status == "removed" ? (BsonValue)BsonDateTime.Create(now) : BsonNull.Value,
                ["edit_time"] = BsonDateTime.Create(now),
                ["version"] = previousVersion!.Value + 1
            };
            var updated = await SaveMemberAsync(member, changes, previousVersion.Value, ct);
            if (updated is null) return (await MemberAsync(project, userId, ct)) ?? member;
            member = updated;
        }
        await SyncCountAsync(project, ct);
        var after = MemberState(member, project);
        if (before != after && operatorUser is not null)
            await audit.RecordProjectAsync(operatorUser, $"project_invitation_projection_{status}", project, member, before, after, requestId, ct);
        return member;
    }

    private static BsonDocument NewMember(BsonDocument project, ObjectId userId, string displayName,
        (string Flag, string Value)[] tags, string status)
    {
        var now = DateTime.UtcNow;
        return new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = userId,
            ["ik"] = $"{project["_id"]}:u:{userId}",
            ["display_name"] = displayName,
            ["dns"] = SearchText.Normalize(displayName),
            ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t.Value)),
            ["status"] = status,
            ["removed_time"] = status == "removed" ? (BsonValue)BsonDateTime.Create(now) : BsonNull.Value,
            ["create_time"] = BsonDateTime.Create(now),
            ["edit_time"] = BsonDateTime.Create(now),
            ["version"] = 0
        };
    }

    private async Task<BsonDocument?> SaveMemberAsync(BsonDocument member, BsonDocument changes, int version, CancellationToken ct)
    {
        var filter = new BsonDocument("_id", member["_id"]);
        if (version == 0) filter["$or"] = new BsonArray
        {
            new BsonDocument("version", 0),
            new BsonDocument("version", new BsonDocument("$exists", false))
        };
        else filter["version"] = version;
        var result = await Members.UpdateOneAsync(filter, new BsonDocument("$set", changes), cancellationToken: ct);
        if (result.ModifiedCount == 0 && result.MatchedCount == 0) return null;
        var document = new BsonDocument(member);
        foreach (var item in changes) document[item.Name] = item.Value;
        return document;
    }

    private async Task<string> TeamDefaultNameAsync(BsonDocument project, ObjectId userId, CancellationToken ct)
    {
        var user = await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct);
        if (user is null) return "";
        return TeamDefaultDisplayName(await ActiveTeamMemberAsync(userId, project["t"].AsObjectId, ct), user);
    }

    /// <summary>
    /// ``update_pending_tags``: pending/removed projections are re-anchored to
    /// the requested positions through ``_ensure_projection``; an active
    /// member only changes tags (audited as
    /// ``project_invitation_position_update``).
    /// </summary>
    public async Task<BsonDocument> UpdatePendingTagsAsync(BsonDocument invitation, (string Flag, string Value)[] tags,
        BsonDocument? operatorUser, string requestId, CancellationToken ct)
    {
        var projectId = InvitationGroupId(invitation) ?? throw IdentityErrors.InvitationNotExist();
        var userId = invitation["u"].AsObjectId;
        var project = await store.Collection("project").Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.InvitationNotExist();
        var member = await MemberAsync(project, userId, ct);
        var before = member is null ? new BsonDocument() : MemberState(member, project);
        if (member is null || member.Text("status") is "invited" or "removed")
            return await EnsureProjectionAsync(project, userId, tags, member?.Text("display_name"), "invited",
                operatorUser ?? await OperatorOfAsync(invitation, ct), requestId, ct);
        var tagsSet = tags.Select(t => t.Value).ToList();
        var memberTags = member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToList();
        if (memberTags.SequenceEqual(tagsSet))
        {
            await SyncCountAsync(project, ct);
            return member;
        }
        var previousVersion = (int)member.Integer("version");
        var now = DateTime.UtcNow;
        var changes = new BsonDocument
        {
            ["tags"] = new BsonArray(tagsSet.Select(t => (BsonValue)t)),
            ["edit_time"] = BsonDateTime.Create(now),
            ["version"] = previousVersion + 1
        };
        var updated = await SaveMemberAsync(member, changes, previousVersion, ct);
        if (updated is null) return (await MemberAsync(project, userId, ct)) ?? member;
        await SyncCountAsync(project, ct);
        var actor = operatorUser ?? await OperatorOfAsync(invitation, ct);
        if (actor is not null)
            await audit.RecordProjectAsync(actor, "project_invitation_position_update", project, updated, before, MemberState(updated, project), requestId, ct);
        return updated;
    }

    private async Task<BsonDocument?> OperatorOfAsync(BsonDocument invitation, CancellationToken ct)
    {
        var id = InvitationOperatorId(invitation);
        return id is null ? null : await store.Collection("user").Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
    }

    // Python project_pending/project_active use the live member's tags when
    // none are provided. project_removed never downgrades an active row.
    public async Task<BsonDocument> ProjectPendingAsync(BsonDocument invitation, (string Flag, string Value)[]? tags,
        string? displayName, BsonDocument? operatorUser, string requestId, CancellationToken ct) =>
        await EnsureProjectionAsync(await ProjectOfAsync(invitation, ct), invitation["u"].AsObjectId,
            tags ?? await MemberTagsAsync(invitation, ct), displayName, "invited",
            operatorUser ?? await OperatorOfAsync(invitation, ct), requestId, ct);

    public async Task<BsonDocument> ProjectActiveAsync(BsonDocument invitation, (string Flag, string Value)[]? tags,
        string? displayName, BsonDocument? operatorUser, string requestId, CancellationToken ct) =>
        await EnsureProjectionAsync(await ProjectOfAsync(invitation, ct), invitation["u"].AsObjectId,
            tags ?? await MemberTagsAsync(invitation, ct), displayName, "active",
            operatorUser ?? await OperatorOfAsync(invitation, ct), requestId, ct);

    public async Task<BsonDocument> ProjectRemovedAsync(BsonDocument invitation, string requestId, CancellationToken ct)
    {
        var project = await ProjectOfAsync(invitation, ct);
        var userId = invitation["u"].AsObjectId;
        var member = await MemberAsync(project, userId, ct);
        if (member is null)
        {
            var user = await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct);
            return await EnsureProjectionAsync(project, userId, [], user?.Text("n") ?? "", "removed",
                await OperatorOfAsync(invitation, ct), requestId, ct);
        }
        if (member.Text("status") is "removed" or "active") return member;
        return await EnsureProjectionAsync(project, userId, await MemberTagsOfAsync(member, ct),
            member.Text("display_name"), "removed", await OperatorOfAsync(invitation, ct), requestId, ct);
    }

    private async Task<BsonDocument> ProjectOfAsync(BsonDocument invitation, CancellationToken ct)
    {
        var projectId = InvitationGroupId(invitation) ?? throw IdentityErrors.InvitationNotExist();
        return await store.Collection("project").Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.InvitationNotExist();
    }

    private async Task<(string Flag, string Value)[]> MemberTagsAsync(BsonDocument invitation, CancellationToken ct)
    {
        var member = await MemberAsync(await ProjectOfAsync(invitation, ct), invitation["u"].AsObjectId, ct);
        return member is null ? [] : await MemberTagsOfAsync(member, ct);
    }

    private static Task<(string Flag, string Value)[]> MemberTagsOfAsync(BsonDocument member, CancellationToken ct) =>
        Task.FromResult(System.Linq.Enumerable.ToArray(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => (Flag: "", Value: v.AsString))));

    /// <summary>
    /// ``create_or_reuse``: calls the legacy invitation entry point and
    /// projects its result. The legacy role is only a compatibility argument;
    /// identity permissions remain the authorization source of truth.
    /// </summary>
    public async Task<BsonDocument> CreateOrReuseAsync(BsonDocument project, BsonDocument operatorUser,
        BsonDocument user, (string Flag, string Value)[] tags, string displayName,
        string message, string requestId, CancellationToken ct)
    {
        var existing = await MemberAsync(project, user["_id"].AsObjectId, ct);
        if (existing is not null && existing.Text("status") == "active") return existing;

        var invitation = await FindPendingInvitationAsync(project, user["_id"].AsObjectId, ct);
        if (invitation is null)
        {
            var translatorRef = GenericRef("ProjectRole", "project_role", await RoleIdAsync("project", "translator", ct));
            try
            {
                await InviteProjectAsync(project, operatorUser, user, translatorRef, message, ct);
            }
            catch (ApiError error) when (error.Code == 103)
            {
                invitation = await CreatePendingInvitationAsync(project, operatorUser, user["_id"].AsObjectId, translatorRef, message, ct);
            }
            catch (ApiError error) when (error.Code == 5004)
            {
                invitation = await FindPendingInvitationAsync(project, user["_id"].AsObjectId, ct);
            }
            invitation ??= await FindPendingInvitationAsync(project, user["_id"].AsObjectId, ct);
        }

        var member = await MemberAsync(project, user["_id"].AsObjectId, ct);
        if (member is not null && member.Text("status") == "active")
            return await EnsureProjectionAsync(project, user["_id"].AsObjectId, tags, displayName, "active", operatorUser, requestId, ct);
        if (invitation is null) throw new InvalidOperationException("project invitation projection was not created");
        return await ProjectPendingAsync(invitation, tags, displayName, operatorUser, requestId, ct);
    }

    private async Task<ObjectId> RoleIdAsync(string scope, string code, CancellationToken ct)
    {
        var collection = scope == "project" ? "project_role" : "team_role";
        var role = await store.Collection(collection).Find(new BsonDocument("m_o", code)).FirstOrDefaultAsync(ct);
        return role is null ? throw new ApiError("RoleNotExistError", 102, "角色不存在") : role["_id"].AsObjectId;
    }

    /// <summary>Role system code → level (Python system role definitions).</summary>
    internal static readonly IReadOnlyDictionary<string, int> RoleLevels = new Dictionary<string, int>
    {
        ["creator"] = 500, ["admin"] = 400, ["coordinator"] = 300,
        ["senior"] = 300, ["proofreader"] = 200, ["translator"] = 200,
        ["picture_editor"] = 200, ["member"] = 200, ["supporter"] = 100, ["beginner"] = 100
    };

    /// <summary>Role system code → member tags (Python join_project tag_map /
    /// _role_code_to_tags for the adapter compatibility path).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> RoleTagMap = new Dictionary<string, string[]>
    {
        ["creator"] = ["creator"], ["admin"] = ["admin"],
        ["coordinator"] = ["proofreader"], ["proofreader"] = ["proofreader"],
        ["translator"] = ["translator"], ["picture_editor"] = ["typesetter"],
        ["supporter"] = ["translator"]
    };

    /// <summary>Resolve the operator's legacy role code from the active
    /// project member's tags (Python User.get_project_relation): the first
    /// recognized tag in persisted order wins, ``typesetter`` maps to
    /// picture_editor, unknown tags fall back to translator. The legacy map
    /// has no coordinator entry.</summary>
    internal static string RoleCodeFromTags(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "creator": return "creator";
                case "admin": return "admin";
                case "proofreader": return "proofreader";
                case "translator": return "translator";
                case "typesetter": return "picture_editor";
            }
        }
        return "translator";
    }

    /// <summary>Python ``User.get_role(project)``: only an active project
    /// member yields a legacy role from its persisted tags; otherwise an
    /// active team creator/admin is an automatic project admin; anything
    /// else has no legacy project role.</summary>
    internal async Task<string?> GetLegacyProjectRoleCodeAsync(BsonDocument project, ObjectId userId, CancellationToken ct)
    {
        var member = await MemberAsync(project, userId, ct);
        if (member is not null && member.Text("status") == "active")
            return RoleCodeFromTags(MemberTags(member));
        var teamMember = await ActiveTeamMemberAsync(userId, project["t"].AsObjectId, ct);
        return teamMember?.Text("base_tag") is "creator" or "admin" ? "admin" : null;
    }

    /// <summary>Legacy ``User.invite`` for project groups (minus the pending
    /// application branch, which lands in the application stage):
    /// is_full → INVITE_USER → self role level → already joined (5003) →
    /// pending invitation (5004) → project-team member direct join →
    /// create a pending Invitation. Returns the invitation document, or
    /// null when the user was already a teammate (direct join).</summary>
    internal async Task<BsonDocument?> InviteProjectAsync(BsonDocument project, BsonDocument operatorUser,
        BsonDocument user, BsonValue roleRef, string message, CancellationToken ct)
    {
        var userId = user["_id"].AsObjectId;
        var activeCount = await Members.CountDocumentsAsync(
            new BsonDocument { ["project"] = project["_id"], ["status"] = "active" }, cancellationToken: ct);
        if (activeCount >= project.Integer("u", 100000)) throw IdentityErrors.TargetIsFull();
        if (!(await permissions.ProjectSnapshotAsync(operatorUser, project, ct)).Has("INVITE_USER"))
            throw ApiError.Forbidden();

        var roleCode = await RoleCodeOfRefAsync(roleRef, "project", ct);
        var roleLevel = RoleLevel(roleCode);
        var selfCode = await GetLegacyProjectRoleCodeAsync(project, operatorUser["_id"].AsObjectId, ct)
            ?? throw ApiError.Forbidden();
        var selfLevel = RoleLevel(selfCode);
        if (selfLevel <= roleLevel) throw ApiError.Forbidden();

        if (await MemberAsync(project, userId, ct) is { } existing && existing.Text("status") == "active")
            throw IdentityErrors.UserAlreadyJoined(project.Text("n"));

        if (await FindPendingInvitationAsync(project, userId, ct) is not null)
            throw IdentityErrors.InvitationAlreadyExist();

        // TODO(application stage): a pending Application is allowed here
        // (Python invite() branch); the application collection only exists
        // once the application stage lands.
        var teamMember = await ActiveTeamMemberAsync(userId, project["t"].AsObjectId, ct);
        if (teamMember is not null)
        {
            await JoinProjectAsync(project, user, roleCode, ct);
            return null;
        }

        return await CreatePendingInvitationAsync(project, operatorUser, userId, roleRef, message, ct);
    }

    internal static int RoleLevel(string roleCode) => RoleLevels.TryGetValue(roleCode, out var level) ? level : 200;

    internal async Task<string> RoleCodeOfRefAsync(BsonValue? roleRef, string scope, CancellationToken ct)
    {
        var id = RefId(roleRef);
        if (id is null) return "translator";
        var collection = scope == "project" ? "project_role" : "team_role";
        var role = await store.Collection(collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return role?.Text("m_o") ?? "translator";
    }

    internal static string[] MemberTags(BsonDocument member) =>
        System.Linq.Enumerable.ToArray(member.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString));

    /// <summary>Python ``user.join`` join semantics for projects: create or
    /// reactivate the member with the role-mapped tags. No audit here; the
    /// adapter's projection step records the identity change.</summary>
    internal async Task<BsonDocument> JoinProjectAsync(BsonDocument project, BsonDocument user, string roleCode, CancellationToken ct)
    {
        var userId = user["_id"].AsObjectId;
        var isOwner = project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ownerId &&
            ownerId.AsObjectId == userId;
        if (isOwner) roleCode = "creator";
        var member = await MemberAsync(project, userId, ct);
        var tags = RoleTagMap.TryGetValue(roleCode, out var map) ? map : [];
        if (member is null)
        {
            var fresh = NewMember(project, userId, await TeamDefaultNameAsync(project, userId, ct),
                System.Linq.Enumerable.ToArray(tags.Select(t => (Flag: "", Value: t))), "active");
            try { await Members.InsertOneAsync(fresh, cancellationToken: ct); }
            catch (MongoWriteException)
            {
                var existing = await MemberAsync(project, userId, ct);
                if (existing is not null) return existing;
                throw;
            }
            await SyncCountAsync(project, ct);
            return fresh;
        }
        if (member.Text("status") == "active")
        {
            // Python join_project: other active members are left untouched,
            // but the owner is always repaired to the single creator tag.
            if (!isOwner) return member;
            var currentTags = MemberTags(member);
            if (currentTags.Length == 1 && currentTags[0] == "creator") return member;
            var ownerVersion = (int)member.Integer("version");
            var ownerChanges = new BsonDocument
            {
                ["tags"] = new BsonArray { "creator" },
                ["edit_time"] = BsonDateTime.Create(DateTime.UtcNow),
                ["version"] = ownerVersion + 1
            };
            var repaired = await SaveMemberAsync(member, ownerChanges, ownerVersion, ct);
            return repaired ?? (await MemberAsync(project, userId, ct)) ?? member;
        }
        var previousVersion = (int)member.Integer("version");
        var now = DateTime.UtcNow;
        var changes = new BsonDocument
        {
            ["tags"] = new BsonArray(tags.Select(t => (BsonValue)t)),
            ["status"] = "active",
            ["removed_time"] = BsonNull.Value,
            ["edit_time"] = BsonDateTime.Create(now),
            ["version"] = previousVersion + 1
        };
        var updated = await SaveMemberAsync(member, changes, previousVersion, ct);
        if (updated is null) return (await MemberAsync(project, userId, ct)) ?? member;
        await SyncCountAsync(project, ct);
        return updated;
    }

    /// <summary>Python ``_add_user`` self path: flips this user's pending
    /// invitations of this project to ALLOW.</summary>
    internal async Task AcceptPendingInvitationsAsync(BsonDocument project, ObjectId userId, CancellationToken ct) =>
        await Invitations.UpdateManyAsync(new BsonDocument
        {
            ["u"] = userId,
            ["g"] = GenericRef("Project", "project", project["_id"].AsObjectId),
            ["s"] = PendingStatus
        }, new BsonDocument("$set", new BsonDocument("s", AllowStatus)), cancellationToken: ct);
}
