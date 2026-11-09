using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Application lifecycle, ported from Python app/apis/application.py,
// app/models/application.py and User.apply/User.join:
// GET/POST /v1/{group_type}/{group_id}/applications,
// PATCH/DELETE /v1/applications/{id}, GET /v1/user/related-applications,
// and the small public-info endpoint used by the apply page.
public sealed class ApplicationService(
    MongoStore store,
    IdentityPermissions permissions,
    ProjectInvitationService invitations,
    ProjectService projects,
    TeamService teams,
    TeamMemberService teamMembers,
    InvitationService invitationService,
    LegacyTypes legacy,
    UserView userView,
    InvitationMutationGate gate)
{
    private const int ApplyNone = 1;
    private const int ApplyAll = 2;
    private const int ProjectTeamUser = 3;
    private const int NoNeedCheck = 1;

    private IMongoCollection<BsonDocument> Applications => store.Collection("application");

    private static string ScopeOf(string groupType) => groupType switch
    {
        "project" or "projects" => InvitationService.ProjectScope,
        "team" or "teams" => InvitationService.TeamScope,
        _ => throw IdentityErrors.GroupTypeNotSupport()
    };

    private async Task<BsonDocument> FetchGroupAsync(string groupType, string groupId, CancellationToken ct) =>
        ScopeOf(groupType) == InvitationService.ProjectScope
            ? await projects.GetProjectAsync(groupId, ct)
            : await projects.GetTeamAsync(groupId, ct);

    private async Task<bool> RequireCheckUserAsync(BsonDocument actor, string scope, BsonDocument group,
        CancellationToken ct)
    {
        var snapshot = scope == InvitationService.ProjectScope
            ? await permissions.ProjectSnapshotAsync(actor, group, ct)
            : await permissions.TeamSnapshotAsync(actor, group, ct);
        if (!snapshot.Has(scope == InvitationService.ProjectScope ? "CHECK_USER" : "team:CHECK_USER"))
            throw ApiError.Forbidden();
        return true;
    }

    private static BsonDocument GroupFilter(string scope, BsonDocument group) =>
        ProjectInvitationService.GenericRef(scope == InvitationService.ProjectScope ? "Project" : "Team",
            scope, group["_id"].AsObjectId);

    private static BsonDocument StatusFilter(IReadOnlyList<int>? statuses)
    {
        if (statuses is null || statuses.Count == 0) return new BsonDocument();
        return statuses.Count == 1
            ? new BsonDocument("s", statuses[0])
            : new BsonDocument("s", new BsonDocument("$in", new BsonArray(statuses)));
    }

    private static readonly SortDefinition<BsonDocument> ApplicationSort =
        Builders<BsonDocument>.Sort.Ascending("s").Descending("_id");

    // ---- lists ----

    public async Task<List<BsonDocument>> ListAsync(BsonDocument actor, string groupType, string groupId,
        IReadOnlyList<int>? statuses, int skip, int limit, CancellationToken ct)
    {
        var scope = ScopeOf(groupType);
        var group = await FetchGroupAsync(groupType, groupId, ct);
        await RequireCheckUserAsync(actor, scope, group, ct);
        var filter = new BsonDocument { ["g"] = GroupFilter(scope, group) };
        filter.Merge(StatusFilter(statuses), false);
        return await Applications.Find(filter).Sort(ApplicationSort).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    public async Task<long> ListCountAsync(BsonDocument actor, string groupType, string groupId,
        IReadOnlyList<int>? statuses, CancellationToken ct)
    {
        var scope = ScopeOf(groupType);
        var group = await FetchGroupAsync(groupType, groupId, ct);
        await RequireCheckUserAsync(actor, scope, group, ct);
        var filter = new BsonDocument { ["g"] = GroupFilter(scope, group) };
        filter.Merge(StatusFilter(statuses), false);
        return await Applications.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public async Task<List<BsonDocument>> RelatedAsync(BsonDocument actor, IReadOnlyList<int>? statuses,
        int skip, int limit, CancellationToken ct)
    {
        var filter = new BsonDocument { ["uc"] = actor["_id"].AsObjectId };
        filter.Merge(StatusFilter(statuses), false);
        return await Applications.Find(filter).Sort(ApplicationSort).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    public async Task<long> RelatedCountAsync(BsonDocument actor, IReadOnlyList<int>? statuses,
        CancellationToken ct)
    {
        var filter = new BsonDocument { ["uc"] = actor["_id"].AsObjectId };
        filter.Merge(StatusFilter(statuses), false);
        return await Applications.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    // ---- apply ----

    public async Task<BsonDocument> ApplyAsync(BsonDocument actor, string groupType, string groupId,
        string message, HttpContext ctx, CancellationToken ct)
    {
        var scope = ScopeOf(groupType);
        var initial = await FetchGroupAsync(groupType, groupId, ct);
        using var held = await gate.EnterAsync(initial["_id"].AsObjectId, ct);
        var group = await FetchGroupAsync(groupType, groupId, ct);
        var userId = actor["_id"].AsObjectId;

        var memberCollection = store.Collection(scope == InvitationService.ProjectScope ? "project_member" : "team_member");
        var groupField = scope == InvitationService.ProjectScope ? "project" : "team";
        var activeCount = await memberCollection.CountDocumentsAsync(new BsonDocument
        {
            [groupField] = group["_id"], ["status"] = "active"
        }, cancellationToken: ct);
        if (activeCount >= (int)group.Integer("u", 100000)) throw IdentityErrors.TargetIsFull();

        var projectMember = scope == InvitationService.ProjectScope
            ? await invitations.MemberAsync(group, userId, ct)
            : null;
        var teamMember = await store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = scope == InvitationService.ProjectScope ? group["t"] : group["_id"],
            ["user"] = userId, ["status"] = "active"
        }).FirstOrDefaultAsync(ct);
        var alreadyJoined = scope == InvitationService.ProjectScope
            ? projectMember?.Text("status") == "active"
            : teamMember is not null;
        if (alreadyJoined) throw IdentityErrors.UserAlreadyJoined(group.Text("n"));

        if (scope == InvitationService.ProjectScope
            && teamMember is not null && teamMember.Text("base_tag") is "creator" or "admin")
        {
            await invitations.JoinProjectAsync(group, actor, "admin", ct);
            var pendingApplications = await Applications.Find(new BsonDocument
            {
                ["u"] = userId, ["g"] = GroupFilter(scope, group), ["s"] = ProjectInvitationService.PendingStatus
            }).ToListAsync(ct);
            foreach (var pending in pendingApplications)
                await invitations.ProjectRemovedAsync(pending, "", ct);
            await Applications.DeleteManyAsync(new BsonDocument
            {
                ["u"] = userId, ["g"] = GroupFilter(scope, group), ["s"] = ProjectInvitationService.PendingStatus
            }, cancellationToken: ct);
            var pendingInvitations = await store.Collection("invitation").Find(new BsonDocument
            {
                ["u"] = userId, ["g"] = GroupFilter(scope, group), ["s"] = ProjectInvitationService.PendingStatus
            }).ToListAsync(ct);
            foreach (var pending in pendingInvitations)
                await invitations.ProjectRemovedAsync(pending, "", ct);
            await store.Collection("invitation").DeleteManyAsync(new BsonDocument
            {
                ["u"] = userId, ["g"] = GroupFilter(scope, group), ["s"] = ProjectInvitationService.PendingStatus
            }, cancellationToken: ct);
            return new BsonDocument
            {
                ["message"] = "加入成功，管理员继承自团队",
                ["group"] = await GroupViewAsync(scope, group, actor, ctx, ct)
            };
        }

        if (await invitations.FindPendingApplicationAsync(scope, group, userId, ct) is not null)
            throw IdentityErrors.ApplicationAlreadyExist();

        if (await invitationService.FindPendingInvitationAsync(scope, group, userId, ct) is { } invitation)
        {
            await invitationService.AcceptInvitationCoreAsync(invitation, "", ct);
            group = await FetchGroupAsync(groupType, groupId, ct);
            return new BsonDocument
            {
                ["message"] = "管理员先前邀请了您，已直接加入",
                ["group"] = await GroupViewAsync(scope, group, actor, ctx, ct)
            };
        }

        var allowType = (int)group.Integer("m_a", ApplyNone);
        var allowed = scope == InvitationService.ProjectScope
            ? allowType == ApplyAll || (allowType == ProjectTeamUser && teamMember is not null)
            : allowType == ApplyAll;
        if (!allowed) throw ApiError.Forbidden();

        var application = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["u"] = userId,
            ["g"] = GroupFilter(scope, group),
            ["s"] = ProjectInvitationService.PendingStatus,
            ["m"] = message,
            ["uc"] = new BsonArray(await CheckUserIdsAsync(scope, group, ct))
        };
        await Applications.InsertOneAsync(application, cancellationToken: ct);

        if ((int)group.Integer("m_t", 2) != NoNeedCheck) 
            return new BsonDocument { ["message"] = "申请成功，请等待管理员审核" };

        if (scope == InvitationService.ProjectScope)
            await invitations.AllowPendingApplicationAsync(group, application, actor, null, "", ct);
        else
        {
            await teamMembers.JoinApplicationRoleAsync(group, actor, await DefaultTeamRoleCodeAsync(group, ct), "", ct);
            application["s"] = ProjectInvitationService.AllowStatus;
            application["o"] = actor["_id"];
            await Applications.UpdateOneAsync(new BsonDocument("_id", application["_id"]),
                new BsonDocument("$set", new BsonDocument
                {
                    ["s"] = ProjectInvitationService.AllowStatus, ["o"] = actor["_id"]
                }), cancellationToken: ct);
        }
        group = await FetchGroupAsync(groupType, groupId, ct);
        return new BsonDocument
        {
            ["message"] = "加入成功",
            ["group"] = await GroupViewAsync(scope, group, actor, ctx, ct)
        };
    }

    // ---- approve / deny / delete ----

    public async Task<BsonDocument> PatchAsync(BsonDocument actor, string applicationId, bool allow,
        HttpContext ctx, string requestId, CancellationToken ct)
    {
        var application = await invitations.GetApplicationAsync(applicationId, ct);
        var scope = ScopeOfApplication(application);
        var groupId = ProjectInvitationService.RefId(application["g"]) ?? throw IdentityErrors.ApplicationNotExist();
        using var held = await gate.EnterAsync(groupId, ct);
        application = await invitations.GetApplicationAsync(applicationId, ct);
        var group = await FetchGroupAsync(scope, groupId.ToString(), ct);
        await RequireCheckUserAsync(actor, scope, group, ct);
        if ((int)application.Integer("s") != ProjectInvitationService.PendingStatus)
            throw IdentityErrors.ApplicationFinished();

        if (allow)
        {
            if (scope == InvitationService.ProjectScope)
                await invitations.AllowPendingApplicationAsync(group, application, actor, null, requestId, ct);
            else
            {
                var target = await store.Collection("user")
                    .Find(new BsonDocument("_id", application["u"])).FirstAsync(ct);
                await teamMembers.JoinApplicationRoleAsync(group, target,
                    await DefaultTeamRoleCodeAsync(group, ct), requestId, ct);
                application["s"] = ProjectInvitationService.AllowStatus;
                application["o"] = actor["_id"];
                await Applications.UpdateOneAsync(new BsonDocument("_id", application["_id"]),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["s"] = ProjectInvitationService.AllowStatus, ["o"] = actor["_id"]
                    }), cancellationToken: ct);
            }
        }
        else
        {
            if (scope == InvitationService.ProjectScope)
                await invitations.DenyPendingApplicationAsync(group, application, actor, requestId, ct);
            else
            {
                application["s"] = ProjectInvitationService.DenyStatus;
                application["o"] = actor["_id"];
                await Applications.UpdateOneAsync(new BsonDocument("_id", application["_id"]),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["s"] = ProjectInvitationService.DenyStatus, ["o"] = actor["_id"]
                    }), cancellationToken: ct);
            }
        }

        return new BsonDocument
        {
            ["message"] = allow ? "已接受" : "已拒绝",
            ["application"] = await ToApiAsync(application, actor, ctx, ct)
        };
    }

    public async Task<BsonDocument> DeleteAsync(BsonDocument actor, string applicationId,
        string requestId, CancellationToken ct)
    {
        var application = await invitations.GetApplicationAsync(applicationId, ct);
        var scope = ScopeOfApplication(application);
        var groupId = ProjectInvitationService.RefId(application["g"]) ?? throw IdentityErrors.ApplicationNotExist();
        using var held = await gate.EnterAsync(groupId, ct);
        application = await invitations.GetApplicationAsync(applicationId, ct);
        // Python checks the status before the applicant identity.
        if ((int)application.Integer("s") != ProjectInvitationService.PendingStatus)
            throw IdentityErrors.ApplicationFinished();
        if (application["u"].AsObjectId != actor["_id"].AsObjectId) throw ApiError.Forbidden();

        if (scope == InvitationService.ProjectScope)
        {
            var group = await FetchGroupAsync(scope, groupId.ToString(), ct);
            await invitations.ProjectRemovedAsync(application, requestId, ct);
            _ = group;
        }
        await Applications.DeleteOneAsync(new BsonDocument("_id", application["_id"]), cancellationToken: ct);
        return new BsonDocument { ["message"] = "删除成功" };
    }

    // ---- public info ----

    public async Task<BsonDocument> PublicInfoAsync(BsonDocument actor, string groupType, string groupId,
        CancellationToken ct)
    {
        var scope = ScopeOf(groupType);
        var group = await FetchGroupAsync(groupType, groupId, ct);
        var userId = actor["_id"].AsObjectId;
        var joined = scope == InvitationService.ProjectScope
            ? (await invitations.MemberAsync(group, userId, ct))?.Text("status") == "active"
            : await store.Collection("team_member").Find(new BsonDocument
            {
                ["team"] = group["_id"], ["user"] = userId, ["status"] = "active"
            }).AnyAsync(ct);

        if (!joined)
        {
            var allowType = (int)group.Integer("m_a", ApplyNone);
            if (allowType == ApplyNone) throw IdentityErrors.GroupNotOpen();
            if (scope == InvitationService.ProjectScope && allowType == ProjectTeamUser)
            {
                var teamRelation = await store.Collection("team_member").Find(new BsonDocument
                {
                    ["team"] = group["t"], ["user"] = userId, ["status"] = "active"
                }).AnyAsync(ct);
                if (!teamRelation) throw IdentityErrors.GroupNotOpen();
            }
        }

        var count = await store.Collection(scope == InvitationService.ProjectScope ? "project_member" : "team_member")
            .CountDocumentsAsync(new BsonDocument
            {
                [scope == InvitationService.ProjectScope ? "project" : "team"] = group["_id"],
                ["status"] = "active"
            }, cancellationToken: ct);
        return new BsonDocument
        {
            ["id"] = group["_id"].ToString()!,
            ["name"] = group.Text("n"),
            ["joined"] = joined,
            ["user_count"] = (int)count,
            ["application_check_type"] = (int)group.Integer("m_t", 2)
        };
    }

    // ---- views ----

    public async Task<List<BsonDocument>> ToApiListAsync(IEnumerable<BsonDocument> rows, BsonDocument? user,
        HttpContext ctx, CancellationToken ct)
    {
        var result = new List<BsonDocument>();
        foreach (var row in rows) result.Add(await ToApiAsync(row, user, ctx, ct));
        return result;
    }

    private async Task<BsonDocument> ToApiAsync(BsonDocument application, BsonDocument? user, HttpContext ctx,
        CancellationToken ct)
    {
        var scope = ScopeOfApplication(application);
        var groupId = ProjectInvitationService.RefId(application["g"]) ?? throw IdentityErrors.ApplicationNotExist();
        var group = await FetchGroupAsync(scope, groupId.ToString(), ct);
        var applicant = await store.Collection("user").Find(new BsonDocument("_id", application["u"]))
            .FirstOrDefaultAsync(ct) ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");
        var chinese = Chinese(ctx);

        object? userRole = null;
        if ((int)application.Integer("s") == ProjectInvitationService.AllowStatus)
            userRole = await UserRoleAsync(scope, group, applicant, chinese, ct);

        object? operatorView = null;
        if (ProjectInvitationService.RefId(application.GetValue("o", BsonNull.Value)) is { } operatorId)
        {
            var operatorDoc = await store.Collection("user").Find(new BsonDocument("_id", operatorId))
                .FirstOrDefaultAsync(ct);
            if (operatorDoc is not null)
            {
                operatorView = await userView.ToApiAsync(operatorDoc, ctx);
            }
        }

        var creation = application["_id"].AsObjectId.CreationTime;
        var payload = new Dictionary<string, object?>
        {
            ["id"] = application["_id"].ToString(),
            ["user"] = await userView.ToApiAsync(applicant, ctx),
            ["user_role"] = userRole,
            ["group"] = scope == InvitationService.ProjectScope
                ? await projects.ViewAsync(group, user, chinese, ct)
                : await teams.ViewAsync(group, user, chinese, ct),
            ["group_type"] = scope,
            ["group_roles"] = (await legacy.SystemRolesAsync(scope, false, chinese, ct))
                .OrderByDescending(RoleLevel).ToArray(),
            ["operator"] = operatorView,
            ["create_time"] = new DateTimeOffset(DateTime.SpecifyKind(creation, DateTimeKind.Utc))
                .ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = (int)application.Integer("s"),
            ["message"] = application.Text("m")
        };
        return BsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    private async Task<object?> UserRoleAsync(string scope, BsonDocument group, BsonDocument user, bool chinese,
        CancellationToken ct)
    {
        string? roleCode;
        if (scope == InvitationService.ProjectScope)
        {
            var member = await invitations.MemberAsync(group, user["_id"].AsObjectId, ct);
            if (member?.Text("status") != "active") return null;
            roleCode = ProjectInvitationService.RoleCodeFromTags(ProjectInvitationService.MemberTags(member));
        }
        else
        {
            var member = await store.Collection("team_member").Find(new BsonDocument
            {
                ["team"] = group["_id"], ["user"] = user["_id"], ["status"] = "active"
            }).FirstOrDefaultAsync(ct);
            if (member is null) return null;
            roleCode = member.Text("base_tag", "member");
        }
        var role = await store.Collection(scope == InvitationService.ProjectScope ? "project_role" : "team_role")
            .Find(new BsonDocument("m_o", roleCode)).FirstOrDefaultAsync(ct);
        return role is null ? null : legacy.RoleView(scope, role, chinese);
    }

    private async Task<BsonDocument> GroupViewAsync(string scope, BsonDocument group, BsonDocument actor,
        HttpContext ctx, CancellationToken ct)
    {
        var chinese = Chinese(ctx);
        var view = scope == InvitationService.ProjectScope
            ? await projects.ViewAsync(group, actor, chinese, ct)
            : await teams.ViewAsync(group, actor, chinese, ct);
        return BsonDocument.Parse(JsonSerializer.Serialize(view));
    }

    private static string ScopeOfApplication(BsonDocument application)
    {
        var gref = application.GetValue("g", new BsonDocument()).AsBsonDocument;
        var cls = gref.GetValue("_cls", "").ToString();
        return cls == "Team" ? InvitationService.TeamScope : InvitationService.ProjectScope;
    }

    private static bool Chinese(HttpContext context)
    {
        var value = context.Request.Headers.AcceptLanguage.ToString();
        return string.IsNullOrWhiteSpace(value) ||
               value.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static int RoleLevel(object role) =>
        JsonSerializer.SerializeToElement(role).GetProperty("level").GetInt32();

    private async Task<string> DefaultTeamRoleCodeAsync(BsonDocument team, CancellationToken ct)
    {
        if (ProjectInvitationService.RefId(team.GetValue("dr", BsonNull.Value)) is not { } roleId)
            return "member";
        var role = await store.Collection("team_role").Find(new BsonDocument("_id", roleId))
            .FirstOrDefaultAsync(ct);
        return role?.Text("m_o") ?? "member";
    }

    private const int CheckUserPermission = 101; // Python rbac CHECK_USER

    private async Task<List<ObjectId>> CheckUserIdsAsync(string scope, BsonDocument group, CancellationToken ct)
    {
        // Python Group.users_by_permission(CHECK_USER) resolves the roles holding
        // that permission and then Group.users(role=roles): active relations
        // whose position tags intersect the role's mapped tag(s).  The persisted
        // project_member.m_o is only a compatibility mirror and must not decide
        // this snapshot, and external/dangling member rows never contribute a
        // user id.
        var roleCollection = store.Collection(scope == InvitationService.ProjectScope ? "project_role" : "team_role");
        var roles = await roleCollection.Find(new BsonDocument
        {
            ["m_p"] = CheckUserPermission
        }).ToListAsync(ct);
        var codes = roles.Select(role => role.Text("m_o"))
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<ObjectId>();

        if (scope == InvitationService.ProjectScope)
        {
            var tags = codes
                .SelectMany(code => ProjectInvitationService.RoleTagMap.TryGetValue(code, out var mapped)
                    ? mapped
                    : [code])
                .ToHashSet(StringComparer.Ordinal);
            var members = await store.Collection("project_member").Find(new BsonDocument
            {
                ["project"] = group["_id"], ["status"] = "active", ["user"] = new BsonDocument("$exists", true)
            }).ToListAsync(ct);
            foreach (var member in members)
            {
                if (member.GetValue("user", BsonNull.Value) is not { IsObjectId: true } user) continue;
                if (ProjectInvitationService.MemberTags(member).Any(tags.Contains))
                    ids.Add(user.AsObjectId);
            }
        }
        else
        {
            // Team relations only keep creator/admin/member base tags; legacy
            // senior/beginner roles normalize to member in Group.users().
            var baseTags = codes
                .Select(code => code is "beginner" or "senior" ? "member" : code)
                .ToHashSet(StringComparer.Ordinal);
            var members = await store.Collection("team_member").Find(new BsonDocument
            {
                ["team"] = group["_id"], ["status"] = "active",
                ["base_tag"] = new BsonDocument("$in", new BsonArray(baseTags)),
                ["user"] = new BsonDocument("$exists", true)
            }).ToListAsync(ct);
            foreach (var member in members)
                if (member.GetValue("user", BsonNull.Value) is { IsObjectId: true } user)
                    ids.Add(user.AsObjectId);
        }

        if (ids.Count == 0) return [];

        // Python's final User.objects(id__in=...) drops member rows whose user
        // document no longer exists.
        var existingUsers = await store.Collection("user").Find(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(ids))
        }).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        return existingUsers.Select(user => user["_id"].AsObjectId).ToList();
    }
}
