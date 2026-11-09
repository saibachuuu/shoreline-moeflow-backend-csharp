using System.Linq;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Invitation lifecycle service: the legacy User.invite semantics for team
// groups, list/update/accept/deny/delete over the invitation collection.
// The project adapter paths live in ProjectInvitationService; this service
// mirrors app/apis/invitation.py endpoint behavior over the same store.
public sealed class InvitationService(
    MongoStore store,
    IdentityPermissions permissions,
    ProjectInvitationService invitations,
    ProjectMemberService members,
    ProjectService projects,
    TeamService teams,
    TeamMemberService teamMembers,
    InvitationMutationGate gate,
    LegacyTypes legacy,
    UserView userView,
    TeamLifecycleLock lifecycle)
{
    private IMongoCollection<BsonDocument> Invitations => store.Collection("invitation");
    private IMongoCollection<BsonDocument> TeamMembers => store.Collection("team_member");

    public const string ProjectScope = "project";
    public const string TeamScope = "team";

    // Order by (status asc, -id) — Python Invitation.get order_by.
    private static readonly SortDefinition<BsonDocument> InvitationSort =
        Builders<BsonDocument>.Sort.Ascending("s").Descending("_id");

    private static BsonDocument GroupFilter(string scope, ObjectId groupId) => new()
    {
        ["g"] = ProjectInvitationService.GenericRef(
            scope == ProjectScope ? "Project" : "Team",
            scope == ProjectScope ? "project" : "team", groupId)
    };

    private static BsonDocument StatusFilter(IEnumerable<int> statuses)
    {
        var set = statuses.Distinct().ToArray();
        return set.Length == 0 ? new BsonDocument()
            : set.Length == 1 ? new BsonDocument("s", set[0])
            : new BsonDocument("s", new BsonDocument("$in", new BsonArray(set.Select(v => (BsonValue)v))));
    }

    // ---- group loading (fetch_group equivalent) ----

    public async Task<(string Scope, BsonDocument Group)> FetchGroupAsync(string groupType, string groupId, CancellationToken ct) => groupType switch
    {
        "project" or "projects" => (ProjectScope, await projects.GetProjectAsync(groupId, ct)),
        "team" or "teams" => (TeamScope, await projects.GetTeamAsync(groupId, ct)),
        _ => throw IdentityErrors.GroupTypeNotSupport()
    };

    internal async Task<bool> CanInviteAsync(BsonDocument actor, string scope, BsonDocument group, CancellationToken ct) =>
        scope == ProjectScope
            ? (await permissions.ProjectSnapshotAsync(actor, group, ct)).Has("INVITE_USER")
            : (await permissions.TeamSnapshotAsync(actor, group, ct)).Has("team:INVITE_USER");

    internal async Task RequireCanInviteAsync(BsonDocument actor, string scope, BsonDocument group, CancellationToken ct)
    {
        if (!await CanInviteAsync(actor, scope, group, ct)) throw ApiError.Forbidden();
    }

    // Python: self.get_role(group) derived from the operator's tags/base_tag.
    private static readonly Dictionary<string, int> ProjectLevelByCode = new()
    {
        ["creator"] = 500, ["admin"] = 400, ["coordinator"] = 300, ["proofreader"] = 200,
        ["translator"] = 200, ["picture_editor"] = 200, ["supporter"] = 100
    };

    internal async Task<int> SelfRoleLevelAsync(BsonDocument actor, string scope, BsonDocument group, CancellationToken ct)
    {
        if (scope == ProjectScope)
        {
            // Python User.get_role(project): active project relation first,
            // then active team creator/admin as automatic project admin.
            var code = await invitations.GetLegacyProjectRoleCodeAsync(group, actor["_id"].AsObjectId, ct)
                ?? throw ApiError.Forbidden();
            return ProjectLevelByCode[code];
        }
        var teamMember = await TeamActiveMemberAsync(actor["_id"].AsObjectId, group["_id"].AsObjectId, ct);
        var baseTag = teamMember?.Text("base_tag") ?? "member";
        return baseTag switch
        {
            "creator" => 500, "admin" => 400, "senior" => 300, "member" => 200, _ => 100
        };
    }

    internal async Task<BsonDocument?> TeamActiveMemberAsync(ObjectId userId, ObjectId teamId, CancellationToken ct) =>
        await TeamMembers.Find(new BsonDocument
        {
            ["team"] = teamId, ["user"] = userId, ["status"] = "active"
        }).FirstOrDefaultAsync(ct);

    internal static int RoleLevel(BsonDocument role) => (int)role.Integer("m_l", 0);

    private async Task<BsonDocument> FetchRoleByIdAsync(string scope, ObjectId roleId, CancellationToken ct)
    {
        var collection = scope == ProjectScope ? "project_role" : "team_role";
        return await store.Collection(collection).Find(new BsonDocument("_id", roleId)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("RoleNotExistError", 102, "角色不存在");
    }

    private async Task<BsonDocument> RoleOfInvitationAsync(string scope, BsonDocument invitation, CancellationToken ct)
    {
        var id = ProjectInvitationService.RefId(invitation.GetValue("r", BsonNull.Value))
            ?? throw new ApiError("RoleNotExistError", 102, "角色不存在");
        return await FetchRoleByIdAsync(scope, id, ct);
    }

    // ---- collection queries ----

    public async Task<BsonDocument?> FindPendingInvitationAsync(string scope, BsonDocument group, ObjectId userId, CancellationToken ct)
    {
        var filter = GroupFilter(scope, group["_id"].AsObjectId);
        filter["u"] = userId;
        filter["s"] = ProjectInvitationService.PendingStatus;
        return await Invitations.Find(filter).Sort(InvitationSort).FirstOrDefaultAsync(ct);
    }

    public async Task<List<BsonDocument>> ListAsync(string scope, BsonDocument group, IEnumerable<int> statuses,
        int skip, int limit, CancellationToken ct)
    {
        var filter = GroupFilter(scope, group["_id"].AsObjectId);
        foreach (var pair in StatusFilter(statuses)) filter[pair.Name] = pair.Value;
        return await Invitations.Find(filter).Sort(InvitationSort).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    public async Task<List<BsonDocument>> MeListAsync(BsonDocument actor, IEnumerable<int> statuses,
        int skip, int limit, CancellationToken ct)
    {
        var filter = new BsonDocument { ["u"] = actor["_id"] };
        foreach (var pair in StatusFilter(statuses)) filter[pair.Name] = pair.Value;
        return await Invitations.Find(filter).Sort(InvitationSort).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    public async Task<long> ListCountAsync(string scope, BsonDocument group, IEnumerable<int> statuses, CancellationToken ct)
    {
        var filter = GroupFilter(scope, group["_id"].AsObjectId);
        foreach (var pair in StatusFilter(statuses)) filter[pair.Name] = pair.Value;
        return await Invitations.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public async Task<long> MeListCountAsync(BsonDocument actor, IEnumerable<int> statuses, CancellationToken ct)
    {
        var filter = new BsonDocument { ["u"] = actor["_id"] };
        foreach (var pair in StatusFilter(statuses)) filter[pair.Name] = pair.Value;
        return await Invitations.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    // ---- creation (POST /invitations) ----

    private static async Task<BsonDocument> FetchTargetUserAsync(MongoStore store, JsonElement body, CancellationToken ct)
    {
        if (!body.TryGetProperty("user_id", out var userIdElement) ||
            userIdElement.ValueKind != JsonValueKind.String || !ObjectId.TryParse(userIdElement.GetString(), out var userId))
            throw ApiError.Field("user_id", "expected an ObjectId");
        // Python's legacy invite API raises UserNotExistError (1005) when the
        // well-formed user id has no document; the modern identity API keeps 5105.
        return await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");
    }

    private static ObjectId ParseRoleId(string groupType, JsonElement body)
    {
        if (!body.TryGetProperty("role_id", out var element) || element.ValueKind != JsonValueKind.String)
            throw IdentityErrors.InvalidRequest("role_id is required");
        // Python: role_cls.objects(id=role_id).first() + RoleNotExistError when absent.
        if (!ObjectId.TryParse(element.GetString(), out var id))
            throw new ApiError("RoleNotExistError", 102, "角色不存在");
        return id;
    }

    private static void ValidateTags(JsonElement body)
    {
        if (!body.TryGetProperty("tags", out var tags) || tags.ValueKind == JsonValueKind.Null) return;
        if (tags.ValueKind != JsonValueKind.Array || tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
            throw ApiError.Field("tags", "expected an array of strings");
    }

    private static string ValidateCreate(string scope, JsonElement body)
    {
        if (!body.TryGetProperty("user_id", out var user) || user.ValueKind != JsonValueKind.String ||
            !ObjectId.TryParse(user.GetString(), out _))
            throw ApiError.Field("user_id", "expected an ObjectId");
        if (!body.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            message.GetString()!.EnumerateRunes().Count() > 140)
            throw ApiError.Field("message", "expected a string of at most 140 characters");
        ValidateTags(body);
        var hasRole = body.TryGetProperty("role_id", out var role) && role.ValueKind != JsonValueKind.Null;
        if (hasRole && (role.ValueKind != JsonValueKind.String || !ObjectId.TryParse(role.GetString(), out _)))
            throw ApiError.Field("role_id", "expected an ObjectId");
        if (!hasRole && (scope == TeamScope || !body.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() == 0))
            throw ApiError.Field(scope == TeamScope ? "role_id" : "tags", "required");
        return message.GetString()!;
    }

    /// <summary>Legacy ``User.invite`` semantics for both group kinds,
    /// returning the Python response shape (message + optional invitation).
    /// Team-manager matching covers the project-team direct join for
    /// projects (via <see cref="ProjectInvitationService.JoinProjectAsync"/>).</summary>
    public async Task<BsonDocument> InviteAsync(BsonDocument actor, string scope, BsonDocument group,
        BsonDocument user, BsonDocument role, string message, HttpContext ctx, CancellationToken ct)
    {
        var userId = user["_id"].AsObjectId;
        var memberCollection = store.Collection(scope == ProjectScope ? "project_member" : "team_member");
        var groupField = scope == ProjectScope ? "project" : "team";
        var activeCount = await memberCollection.CountDocumentsAsync(new BsonDocument
        {
            [groupField] = group["_id"], ["status"] = "active"
        }, cancellationToken: ct);
        if (activeCount >= (int)group.Integer("u", 100000)) throw IdentityErrors.TargetIsFull();
        if (!await CanInviteAsync(actor, scope, group, ct)) throw ApiError.Forbidden();

        var roleCode = role.Text("m_o");
        var roleLevel = RoleLevel(role);
        var selfLevel = await SelfRoleLevelAsync(actor, scope, group, ct);
        if (selfLevel <= roleLevel) throw ApiError.Forbidden();

        if (scope == ProjectScope
                ? await invitations.MemberAsync(group, userId, ct) is { } m && m.Text("status") == "active"
                : await TeamActiveMemberAsync(userId, group["_id"].AsObjectId, ct) is { })
            throw IdentityErrors.UserAlreadyJoined(scope == ProjectScope ? group.Text("n") : group.Text("n"));

        if (await FindPendingInvitationAsync(scope, group, userId, ct) is not null)
            throw IdentityErrors.InvitationAlreadyExist();

        var roleRef = ProjectInvitationService.GenericRef(
            scope == ProjectScope ? "ProjectRole" : "TeamRole",
            scope == ProjectScope ? "project_role" : "team_role", role["_id"].AsObjectId);

        // Python User.invite: a pending application wins over the direct team
        // join and is approved with the invited role.
        if (await invitations.FindPendingApplicationAsync(scope, group, userId, ct) is { } application)
        {
            if (scope == ProjectScope)
                await invitations.AllowPendingApplicationAsync(group, application, actor, roleCode, "", ct);
            else
            {
                await JoinTeamAsync(group, user, roleCode, "", ct);
                application["s"] = ProjectInvitationService.AllowStatus;
                application["o"] = actor["_id"];
                await store.Collection("application").UpdateOneAsync(new BsonDocument("_id", application["_id"]),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["s"] = ProjectInvitationService.AllowStatus, ["o"] = actor["_id"]
                    }), cancellationToken: ct);
            }
            return new BsonDocument { ["message"] = "此用户已有申请，已直接加入" };
        }

        if (scope == ProjectScope)
        {
            if (await invitations.ActiveTeamMemberAsync(userId, group["t"].AsObjectId, ct) is not null)
            {
                await invitations.JoinProjectAsync(group, user, roleCode, ct);
                return new BsonDocument { ["message"] = "此用户是项目所在团队成员，已直接加入" };
            }
            var invitation = await invitations.CreatePendingInvitationAsync(group, actor, userId, roleRef, message, ct);
            return new BsonDocument
            {
                ["message"] = "邀请成功，请等待用户确认",
                ["invitation"] = await ToApiAsync(invitation, ctx, ct)
            };
        }

        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["u"] = userId,
            ["o"] = actor["_id"],
            ["g"] = ProjectInvitationService.GenericRef("Team", "team", group["_id"].AsObjectId),
            ["r"] = roleRef,
            ["s"] = ProjectInvitationService.PendingStatus,
            ["m"] = message ?? "",
            ["c"] = BsonDateTime.Create(DateTime.UtcNow)
        };
        try
        {
            await Invitations.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException)
        {
            var existing = await FindPendingInvitationAsync(scope, group, userId, ct);
            if (existing is not null) throw IdentityErrors.InvitationAlreadyExist();
            throw;
        }
        return new BsonDocument
        {
            ["message"] = "邀请成功，请等待用户确认",
            ["invitation"] = await ToApiAsync(doc, ctx, ct)
        };
    }

    /// <summary>POST entry point. The project identity branch (with tags)
    /// requires INVITE_USER, validates tags source="invitation" against
    /// IdentityAssignment with source "invitation", locks and routes into
    /// the adapter; everything else follows the legacy invite() flow.</summary>
    public async Task<BsonDocument> PostInviteAsync(BsonDocument actor, string groupType, string groupId,
        JsonElement body, HttpContext ctx, string requestId, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("expected an object");
        var (scope, group) = await FetchGroupAsync(groupType, groupId, ct);
        using var held = await gate.EnterAsync(group["_id"].AsObjectId, ct);
        (_, group) = await FetchGroupAsync(groupType, groupId, ct);
        await RequireCanInviteAsync(actor, scope, group, ct);

        var message = ValidateCreate(scope, body);

        var user = await FetchTargetUserAsync(store, body, ct);
        if (body.TryGetProperty("role_id", out var suppliedRole) && suppliedRole.ValueKind != JsonValueKind.Null)
            await FetchRoleByIdAsync(scope, ParseRoleId(scope, body), ct);

        if (scope == ProjectScope && body.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var normalized = await members.ValidateTagsAsync(actor, group, user, tags, external: false, allowCreator: false, ct);
            // Python's invitation schema excludes unknown fields and never
            // forwards display_name; the projection uses the team alias.
            var member = await invitations.CreateOrReuseAsync(group, actor, user,
                normalized.Select(t => (Flag: "", Value: t)).ToArray(), "", message, requestId, ct);
            if (member.Text("status") == "active")
                return new BsonDocument
                {
                    ["message"] = "此用户是项目所在团队成员，已直接加入",
                    ["project"] = await GroupViewAsync(scope, group, actor, ctx, ct)
                };
            var existing = await invitations.FindPendingInvitationAsync(group, user["_id"].AsObjectId, ct);
            return new BsonDocument
            {
                ["message"] = "邀请成功，请等待用户确认",
                ["invitation"] = await ToApiAsync(existing ?? throw IdentityErrors.InvitationNotExist(), ctx, ct)
            };
        }

        var role = await FetchRoleByIdAsync(scope, ParseRoleId(scope, body), ct);
        return await InviteAsync(actor, scope, group, user, role, message, ctx, ct);
    }

    // ---- update (PUT /invitations/{id}) ----

    public async Task<BsonDocument> PutInvitationAsync(BsonDocument actor, string invitationId, JsonElement body,
        HttpContext ctx, string requestId, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("expected an object");
        var invitation = await invitations.GetInvitationAsync(invitationId, ct);
        using var held = await gate.EnterAsync(ProjectInvitationService.InvitationGroupId(invitation) ?? throw IdentityErrors.InvitationNotExist(), ct);
        invitation = await invitations.GetInvitationAsync(invitationId, ct);
        var scope = InvitationGroupScope(invitation) ?? throw IdentityErrors.InvitationNotExist();
        if ((int)invitation.Integer("s") != ProjectInvitationService.PendingStatus)
            throw IdentityErrors.InvitationFinished();
        var group = await LoadGroupAsync(scope, invitation, ct);
        await RequireCanInviteAsync(actor, scope, group, ct);

        // Python ChangeInvitationSchema validates role_id as an optional string
        // before either the tags or the role branch runs. A non-string value is
        // a schema error even when tags are present; a valid string is resolved
        // later so a missing role stays 102 after the current-role check.
        if (body.TryGetProperty("role_id", out var roleIdValue) &&
            roleIdValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw ApiError.Field("role_id", "expected a string");

        ValidateTags(body);

        if (scope == ProjectScope && body.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var target = await FetchUserDocAsync(invitation["u"].AsObjectId, ct);
            var normalized = await members.ValidateTagsAsync(actor, group, target, tags, external: false, allowCreator: false, ct);
            await invitations.UpdatePendingTagsAsync(invitation, normalized.Select(t => (Flag: "", Value: t)).ToArray(), actor, requestId, ct);
            return new BsonDocument { ["message"] = "修改成功" };
        }

        var selfLevel = await SelfRoleLevelAsync(actor, scope, group, ct);
        var currentRole = await RoleOfInvitationAsync(scope, invitation, ct);
        if (selfLevel <= RoleLevel(currentRole)) throw ApiError.Forbidden();

        // Python fetches the requested role after the current-role level
        // check; a missing/invalid role reference is RoleNotExistError (102).
        if (!body.TryGetProperty("role_id", out var requestedRole) || requestedRole.ValueKind == JsonValueKind.Null)
            throw new ApiError("RoleNotExistError", 102, "角色不存在");

        var newRole = await FetchRoleByIdAsync(scope, ParseRoleId(scope, body), ct);
        if (selfLevel <= RoleLevel(newRole)) throw ApiError.Forbidden();

        var roleRef = ProjectInvitationService.GenericRef(
            scope == ProjectScope ? "ProjectRole" : "TeamRole",
            scope == ProjectScope ? "project_role" : "team_role", newRole["_id"].AsObjectId);
        var changes = new BsonDocument("r", roleRef);
        var update = new BsonDocument { ["$set"] = changes };
        var result = await Invitations.UpdateOneAsync(new BsonDocument("_id", invitation["_id"]), update, cancellationToken: ct);
        if (result.MatchedCount == 0) throw IdentityErrors.InvitationNotExist();
        return new BsonDocument { ["message"] = "修改成功" };
    }

    // ---- accept / deny (PATCH /invitations/{id}) ----

    public async Task<BsonDocument> PatchInvitationAsync(BsonDocument actor, string invitationId, bool allow,
        HttpContext ctx, string requestId, CancellationToken ct)
    {
        var invitation = await invitations.GetInvitationAsync(invitationId, ct);
        using var held = await gate.EnterAsync(ProjectInvitationService.InvitationGroupId(invitation) ?? throw IdentityErrors.InvitationNotExist(), ct);
        invitation = await invitations.GetInvitationAsync(invitationId, ct);
        if (actor["_id"].AsObjectId != invitation["u"].AsObjectId) throw ApiError.Forbidden();
        var scope = InvitationGroupScope(invitation) ?? throw IdentityErrors.InvitationNotExist();
        if ((int)invitation.Integer("s") != ProjectInvitationService.PendingStatus)
            throw IdentityErrors.InvitationFinished();

        if (allow)
        {
            var group = await AcceptInvitationCoreAsync(invitation, requestId, ct);
            return new BsonDocument
            {
                ["message"] = "已接受",
                ["group"] = await GroupViewAsync(scope, group, actor, ctx, ct)
            };
        }

        // Python deny() only transitions the status and removes the
        // projection; the invited role is never loaded on this path.
        await Invitations.UpdateOneAsync(new BsonDocument("_id", invitation["_id"]),
            new BsonDocument("$set", new BsonDocument("s", ProjectInvitationService.DenyStatus)), cancellationToken: ct);
        if (scope == ProjectScope)
        {
            var group = await LoadGroupAsync(scope, invitation, ct);
            await invitations.ProjectRemovedAsync(invitation, requestId, ct);
        }
        return new BsonDocument("message", "已拒绝");
    }

    /// <summary>Shared invitation accept kernel (Python Invitation.allow).
    /// The caller must already hold the invitation mutation gate and must not
    /// re-enter it.</summary>
    internal async Task<BsonDocument> AcceptInvitationCoreAsync(BsonDocument invitation, string requestId,
        CancellationToken ct)
    {
        var scope = InvitationGroupScope(invitation) ?? throw IdentityErrors.InvitationNotExist();
        if ((int)invitation.Integer("s") != ProjectInvitationService.PendingStatus)
            throw IdentityErrors.InvitationFinished();
        var group = await LoadGroupAsync(scope, invitation, ct);
        var user = await FetchUserDocAsync(invitation["u"].AsObjectId, ct);
        var role = await RoleOfInvitationAsync(scope, invitation, ct);
        string[]? capturedTags = null;
        string? capturedDisplay = null;
        if (scope == ProjectScope)
        {
            var member = await invitations.MemberAsync(group, user["_id"].AsObjectId, ct);
            if (member is not null)
            {
                capturedTags = ProjectInvitationService.MemberTags(member);
                capturedDisplay = member.Text("display_name");
            }
            await invitations.JoinProjectAsync(group, user, role.Text("m_o"), ct);
        }
        else
            await JoinTeamAsync(group, user, role.Text("m_o"), requestId, ct);
        await Invitations.UpdateOneAsync(new BsonDocument("_id", invitation["_id"]),
            new BsonDocument("$set", new BsonDocument("s", ProjectInvitationService.AllowStatus)), cancellationToken: ct);
        if (scope == ProjectScope)
            // Python allow() calls project_active without an operator, so the
            // projection change is attributed to the original inviter.
            await invitations.ProjectActiveAsync(invitation,
                capturedTags?.Select(t => (Flag: "", Value: t)).ToArray(), capturedDisplay, null, requestId, ct);
        return group;
    }

    // ---- delete (DELETE /invitations/{id}) ----

    public async Task<BsonDocument> DeleteInvitationAsync(BsonDocument actor, string invitationId,
        string requestId, CancellationToken ct)
    {
        var invitation = await invitations.GetInvitationAsync(invitationId, ct);
        using var held = await gate.EnterAsync(ProjectInvitationService.InvitationGroupId(invitation) ?? throw IdentityErrors.InvitationNotExist(), ct);
        invitation = await invitations.GetInvitationAsync(invitationId, ct);
        var scope = InvitationGroupScope(invitation) ?? throw IdentityErrors.InvitationNotExist();
        var group = await LoadGroupAsync(scope, invitation, ct);
        await RequireCanInviteAsync(actor, scope, group, ct);
        var role = await RoleOfInvitationAsync(scope, invitation, ct);
        var selfLevel = await SelfRoleLevelAsync(actor, scope, group, ct);
        if (selfLevel <= RoleLevel(role)) throw ApiError.Forbidden();

        if (scope == ProjectScope && (int)invitation.Integer("s") == ProjectInvitationService.PendingStatus)
            await invitations.ProjectRemovedAsync(invitation, requestId, ct);
        await Invitations.DeleteOneAsync(new BsonDocument("_id", invitation["_id"]), cancellationToken: ct);
        return new BsonDocument { ["message"] = "删除成功" };
    }

    // ---- join helpers (User.join equivalents) ----

    /// <summary>Python join_team: creates/updates the team member with a
    /// capability-checked slot. Capacity breaches raise TargetIsFullError.</summary>
    internal async Task<BsonDocument> JoinTeamAsync(BsonDocument team, BsonDocument user, string roleCode, string requestId, CancellationToken ct)
    {
        // K1: every current caller (PostInviteAsync, PatchInvitationAsync via
        // AcceptInvitationCoreAsync) holds the invitation mutation gate for this
        // team id. The guard is checked inside that gate before the member row
        // or base_tag is written; JoinTrustedAsync re-enters the team gate.
        await lifecycle.EnsureWritableAsync(team["_id"].AsObjectId, ct);
        var userId = user["_id"].AsObjectId;
        var member = await TeamActiveMemberAsync(userId, team["_id"].AsObjectId, ct);
        if (member is not null)
        {
            var requested = roleCode;
            if (requested is "creator" or "admin" or "member" && member.Text("base_tag") != requested)
            {
                await TeamMembers.UpdateOneAsync(new BsonDocument("_id", member["_id"]),
                    new BsonDocument { ["$set"] = new BsonDocument("base_tag", requested), ["$inc"] = new BsonDocument("version", 1) },
                    cancellationToken: ct);
            }
            return member;
        }
        var activeCount = await TeamMembers.CountDocumentsAsync(new BsonDocument
        {
            ["team"] = team["_id"], ["status"] = "active"
        }, cancellationToken: ct);
        if (activeCount >= (int)team.Integer("u", 100000)) throw IdentityErrors.TargetIsFull();
        var baseTag = roleCode is "creator" or "admin" or "member" ? roleCode : "member";
        try
        {
            return await teamMembers.JoinTrustedAsync(team, user, baseTag, requestId, ct);
        }
        catch (ApiError error) when (error.Code == 5107)
        {
            throw IdentityErrors.TargetIsFull();
        }
    }

    // ---- to_api ----

    private static string? InvitationGroupScope(BsonDocument invitation) =>
        invitation.GetValue("g") is { IsBsonDocument: true } value
            ? value.AsBsonDocument.Text("_cls") switch { "Project" => ProjectScope, "Team" => TeamScope, _ => null }
            : null;

    private Task<BsonDocument> LoadGroupAsync(string scope, BsonDocument invitation, CancellationToken ct) =>
        scope == ProjectScope
            ? projects.GetProjectAsync(ProjectInvitationService.InvitationGroupId(invitation)!.Value.ToString()!, ct)
            : projects.GetTeamAsync(ProjectInvitationService.InvitationGroupId(invitation)!.Value.ToString()!, ct);

    private async Task<BsonDocument> FetchUserDocAsync(ObjectId userId, CancellationToken ct) =>
        await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");

    private static bool Chinese(HttpContext context) =>
        context is not null && context.Request.Headers.AcceptLanguage.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private async Task<BsonValue> GroupViewAsync(string scope, BsonDocument group, BsonDocument actor,
        HttpContext ctx, CancellationToken ct)
    {
        group = (await FetchGroupAsync(scope, group["_id"].ToString()!, ct)).Group;
        if (scope == ProjectScope)
            return BsonDocument.Parse(JsonSerializer.Serialize(await projects.ViewAsync(group, actor, Chinese(ctx), ct)));
        return BsonDocument.Parse(JsonSerializer.Serialize(await teams.ViewAsync(group, actor, Chinese(ctx), ct)));
    }

    private async Task<BsonValue> GroupPublicViewAsync(string scope, BsonDocument group,
        HttpContext ctx, CancellationToken ct)
    {
        if (scope == ProjectScope)
            return BsonDocument.Parse(JsonSerializer.Serialize(await projects.ViewAsync(group, null, Chinese(ctx), ct)));
        return BsonDocument.Parse(JsonSerializer.Serialize(await teams.ViewAsync(group, null, Chinese(ctx), ct)));
    }

    public async Task<BsonDocument> ToApiAsync(BsonDocument invitation, HttpContext ctx, CancellationToken ct)
    {
        var scope = InvitationGroupScope(invitation) ?? throw IdentityErrors.InvitationNotExist();
        var user = await FetchUserDocAsync(invitation["u"].AsObjectId, ct);
        var operatorDoc = ProjectInvitationService.InvitationOperatorId(invitation) is { } operatorId
            ? await FetchUserDocAsync(operatorId, ct) : null;
        var role = await RoleOfInvitationAsync(scope, invitation, ct);

        // tags: for projects, the current projected member tags; legacy-only
        // invitations fall back to the role mapping.
        BsonValue tags = BsonNull.Value;
        if (scope == ProjectScope)
        {
            var member = await invitations.MemberAsync(
                await projects.GetProjectAsync(ProjectInvitationService.InvitationGroupId(invitation)!.Value.ToString()!, ct),
                user["_id"].AsObjectId, ct);
            var list = member is null
                ? System.Linq.Enumerable.ToArray(ProjectInvitationService.RoleTagMap.TryGetValue(role.Text("m_o"), out var map) ? map : System.Array.Empty<string>())
                : System.Linq.Enumerable.ToArray(ProjectInvitationService.MemberTags(member));
            tags = new BsonArray(list.Select(t => (BsonValue)t));
        }

        var group = await LoadGroupAsync(scope, invitation, ct);
        return new BsonDocument
        {
            ["id"] = invitation["_id"].ToString(),
            ["user"] = BsonDocument.Parse(JsonSerializer.Serialize(await userView.ToApiAsync(user, ctx))) ,
            ["group"] = await GroupPublicViewAsync(scope, group, ctx, ct),
            ["group_type"] = scope,
            ["role"] = BsonDocument.Parse(JsonSerializer.Serialize(legacy.RoleView(scope, role, Chinese(ctx)))),
            ["tags"] = tags,
            ["operator"] = operatorDoc is null ? BsonNull.Value
                : BsonDocument.Parse(JsonSerializer.Serialize(await userView.ToApiAsync(operatorDoc, ctx))),
            ["create_time"] = invitation.GetValue("c", BsonNull.Value) is BsonDateTime created
                ? created.ToString() : invitation.Text("c"),
            ["status"] = (int)invitation.Integer("s")
        };
    }
}
