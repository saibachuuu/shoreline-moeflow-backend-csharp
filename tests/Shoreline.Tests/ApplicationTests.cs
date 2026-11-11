using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class ApplicationTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task ApplyCreatesPendingApplicationWithCheckUserSnapshotAndRejectsDuplicate()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);

        var body = await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "请让我加入" }));
        Assert.Equal("Application Sent. Please wait for administrator to review.", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("group", out _));

        var application = await ApplicationAsync(project, target);
        Assert.Equal(1, application.Integer("s"));
        Assert.Equal("请让我加入", application.Text("m"));
        Assert.Equal("Project", application["g"]["_cls"].AsString);
        Assert.Equal(project, application["g"]["_ref"]["$id"].AsObjectId);
        Assert.Contains(creator["_id"].AsObjectId, application["uc"].AsBsonArray.Select(v => v.AsObjectId));
        Assert.False(application.Contains("r"));
        Assert.False(application.Contains("c"));

        await ErrorAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "再次申请" }), 5005);
        Assert.Equal(1, await services.Store.Collection("application")
            .CountDocumentsAsync(new BsonDocument { ["g._ref.$id"] = project, ["u"] = target["_id"] }));
    }

    [Fact]
    public async Task GroupAndRelatedListsFilterPaginateAndUseViewerContext()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var first = await UserAsync();
        var second = await UserAsync();
        using var firstClient = Client(first);
        using var secondClient = Client(second);
        await BodyAsync(await firstClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "第一个" }));
        await BodyAsync(await secondClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "第二个" }));

        var query = await owner.GetAsync(ApplicationPath("project", project) + "?status=1&page=1&limit=1");
        var page = await BodyAsync(query);
        Assert.Single(page.EnumerateArray());
        Assert.Equal("2", query.Headers.GetValues("X-Pagination-Count").Single());
        var listed = page[0];
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("group").GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("user_role").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("operator").ValueKind);

        var related = (await BodyAsync(await owner.GetAsync("/v1/user/related-applications"))).EnumerateArray()
            .ToArray();
        Assert.Equal(2, related.Length);
        var relatedFirst = Assert.Single(related, item =>
            item.GetProperty("user").GetProperty("id").GetString() == first["_id"].ToString());
        Assert.Equal(JsonValueKind.Null, relatedFirst.GetProperty("user_role").ValueKind);
        Assert.Equal(project.ToString(), relatedFirst.GetProperty("group").GetProperty("id").GetString());
        Assert.DoesNotContain(relatedFirst.GetProperty("group_roles").EnumerateArray(),
            role => role.GetProperty("system_code").GetString() == "creator");

        using var unrelated = Client(await UserAsync());
        await ErrorAsync(await unrelated.GetAsync(ApplicationPath("project", project)), 103);
        await ErrorAsync(await unrelated.GetAsync("/v1/user/related-applications?status=abc"), 2);
    }

    [Fact]
    public async Task ApproveProjectApplicationUsesDefaultOrPreservedProjectionTags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var plain = await UserAsync();
        var tagged = await UserAsync();
        using var plainClient = Client(plain);
        using var taggedClient = Client(tagged);
        await BodyAsync(await plainClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "默认角色" }));
        await BodyAsync(await taggedClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "保留标签" }));
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = tagged["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = "旧标签", ["status"] = "invited", ["version"] = 0
        });

        var plainApplication = await ApplicationAsync(project, plain);
        var plainResponse = await BodyAsync(await owner.PatchAsJsonAsync(
            ApplicationEndpoint(plainApplication["_id"].AsObjectId), new { allow = true }));
        Assert.Equal("Approved", plainResponse.GetProperty("message").GetString());
        Assert.Equal(2, (await ApplicationAsync(project, plain)).Integer("s"));
        var plainMember = await ProjectMemberAsync(project, plain);
        Assert.Equal("active", plainMember.Text("status"));
        Assert.Equal(new[] { "translator" }, plainMember["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(plain.Text("n"), plainMember.Text("display_name"));

        var taggedApplication = await ApplicationAsync(project, tagged);
        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(taggedApplication["_id"].AsObjectId),
            new { allow = true }));
        var taggedMember = await ProjectMemberAsync(project, tagged);
        Assert.Equal("active", taggedMember.Text("status"));
        Assert.Equal(new[] { "admin" }, taggedMember["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal("旧标签", taggedMember.Text("display_name"));
        Assert.Equal(2, (await ApplicationAsync(project, tagged)).Integer("s"));
    }

    [Fact]
    public async Task InvitationApprovesPendingProjectApplicationWithInvitedRole()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "先申请" }));

        var role = await RoleAsync("project", "proofreader");
        var invited = await BodyAsync(await owner.PostAsJsonAsync($"/v1/project/{project}/invitations", new
        {
            user_id = target["_id"].ToString(), role_id = role.ToString(), message = "邀请"
        }));
        Assert.Equal("This user already has an application and has been directly added.", invited.GetProperty("message").GetString());
        Assert.False(invited.TryGetProperty("invitation", out _));

        var application = await ApplicationAsync(project, target);
        Assert.Equal(2, application.Integer("s"));
        Assert.Equal(creator["_id"].AsObjectId, application["o"].AsObjectId);
        Assert.Equal(0, await services.Store.Collection("invitation")
            .CountDocumentsAsync(new BsonDocument("u", target["_id"])));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal(new[] { "proofreader" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        _ = team;
    }

    [Fact]
    public async Task TagsInvitationAlsoApprovesPendingApplicationAndAppliesRequestedTags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "先申请" }));

        await BodyAsync(await owner.PostAsJsonAsync($"/v1/project/{project}/invitations", new
        {
            user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "标签邀请"
        }));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal(new[] { "admin" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(2, (await ApplicationAsync(project, target)).Integer("s"));
    }

    [Fact]
    public async Task TeamApplicationApprovalJoinsDefaultRoleAndReturnsApplicationShape()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, _) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("team", team), new { message = "加入团队" }));

        var application = await ApplicationAsync(team, target, "team");
        var result = await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }));
        Assert.Equal("Approved", result.GetProperty("message").GetString());
        var view = result.GetProperty("application");
        Assert.Equal(2, view.GetProperty("status").GetInt32());
        Assert.Equal(creator["_id"].ToString(), view.GetProperty("operator").GetProperty("id").GetString());
        Assert.True(DateTimeOffset.TryParse(view.GetProperty("create_time").GetString(), out _));
        Assert.DoesNotContain(view.GetProperty("group_roles").EnumerateArray(),
            role => role.GetProperty("system_code").GetString() == "creator");
        Assert.Equal("member", view.GetProperty("user_role").GetProperty("system_code").GetString());

        var member = await services.Store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = team, ["user"] = target["_id"]
        }).FirstAsync();
        Assert.Equal("active", member.Text("status"));
        Assert.Equal("member", member.Text("base_tag"));
    }

    [Fact]
    public async Task DenyDeleteAndStaleDenyNeverDowngradeActiveProjection()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var denied = await UserAsync();
        using var deniedClient = Client(denied);
        await BodyAsync(await deniedClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "会被拒绝" }));
        var deniedApplication = await ApplicationAsync(project, denied);
        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(deniedApplication["_id"].AsObjectId),
            new { allow = false }));
        Assert.Equal(3, (await ApplicationAsync(project, denied)).Integer("s"));
        Assert.Equal("removed", (await ProjectMemberAsync(project, denied)).Text("status"));

        var deleted = await UserAsync();
        using var deletedClient = Client(deleted);
        await BodyAsync(await deletedClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "会被删除" }));
        var deletedApplication = await ApplicationAsync(project, deleted);
        await ErrorAsync(await owner.DeleteAsync(ApplicationEndpoint(deletedApplication["_id"].AsObjectId)), 103);
        var deletedBody = await BodyAsync(await deletedClient.DeleteAsync(
            ApplicationEndpoint(deletedApplication["_id"].AsObjectId)));
        Assert.Equal("Removed", deletedBody.GetProperty("message").GetString());
        Assert.Equal(0, await services.Store.Collection("application")
            .CountDocumentsAsync(new BsonDocument("_id", deletedApplication["_id"])));
        Assert.Equal("removed", (await ProjectMemberAsync(project, deleted)).Text("status"));

        var active = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = active["_id"], ["tags"] = new BsonArray { "translator" },
            ["display_name"] = active.Text("n"), ["status"] = "active", ["version"] = 0
        });
        var stale = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["u"] = active["_id"],
            ["g"] = ProjectInvitationService.GenericRef("Project", "project", project),
            ["s"] = 1, ["m"] = "stale", ["uc"] = new BsonArray { creator["_id"] }
        };
        await services.Store.Collection("application").InsertOneAsync(stale);
        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(stale["_id"].AsObjectId),
            new { allow = false }));
        Assert.Equal(3, (await services.Store.Collection("application")
            .Find(new BsonDocument("_id", stale["_id"])).FirstAsync()).Integer("s"));
        Assert.Equal("active", (await ProjectMemberAsync(project, active)).Text("status"));
    }

    [Fact]
    public async Task AllowApplyAndPublicInfoFollowTeamUserAndNoneModes()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (closedTeam, _) = await SetupAsync(owner, teamAllow: 1);
        var outsider = await UserAsync();
        using var outsiderClient = Client(outsider);
        await ErrorAsync(await outsiderClient.GetAsync($"/v1/team/{closedTeam}/public-info"), 5014);
        await ErrorAsync(await outsiderClient.PostAsJsonAsync(ApplicationPath("team", closedTeam),
            new { message = "不允许" }), 103);
        var joinedInfo = await BodyAsync(await owner.GetAsync($"/v1/team/{closedTeam}/public-info"));
        Assert.True(joinedInfo.GetProperty("joined").GetBoolean());
        var properties = joinedInfo.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "application_check_type", "id", "joined", "name", "user_count" }, properties);

        var (team, project) = await SetupAsync(owner, teamAllow: 1);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument("m_a", 3)));
        var teamMember = await UserAsync();
        await TeamMemberAsync(team, teamMember);
        using var memberClient = Client(teamMember);
        await ErrorAsync(await outsiderClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "非团队成员" }), 103);
        await ErrorAsync(await outsiderClient.GetAsync($"/v1/project/{project}/public-info"), 5014);

        var memberApplication = await BodyAsync(await memberClient.PostAsJsonAsync(
            ApplicationPath("project", project), new { message = "团队成员申请" }));
        Assert.Equal("Application Sent. Please wait for administrator to review.", memberApplication.GetProperty("message").GetString());
        var pending = await ApplicationAsync(project, teamMember);
        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(pending["_id"].AsObjectId),
            new { allow = true }));
        var info = await BodyAsync(await memberClient.GetAsync($"/v1/project/{project}/public-info"));
        Assert.True(info.GetProperty("joined").GetBoolean());
        Assert.Equal(new[] { "application_check_type", "id", "joined", "name", "user_count" },
            info.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());

        await ErrorAsync(await services.App.CreateClient().GetAsync($"/v1/project/{project}/public-info"), 1003);
    }

    [Fact]
    public async Task InheritedTeamAdminApplyJoinsProjectAdminAndClearsPendingInvitation()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner, projectAllow: 1);
        var teamAdmin = await UserAsync();
        await TeamMemberAsync(team, teamAdmin, baseTag: "admin");
        using var adminClient = Client(teamAdmin);
        var role = await RoleAsync("project", "translator");
        await services.Store.Collection("invitation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["u"] = teamAdmin["_id"], ["o"] = creator["_id"],
            ["g"] = ProjectInvitationService.GenericRef("Project", "project", project),
            ["r"] = ProjectInvitationService.GenericRef("ProjectRole", "project_role", role),
            ["s"] = 1, ["m"] = "旧邀请", ["c"] = DateTime.UtcNow
        });
        Assert.Equal(1, await services.Store.Collection("invitation")
            .CountDocumentsAsync(new BsonDocument { ["u"] = teamAdmin["_id"], ["s"] = 1 }));
        await services.Store.Collection("application").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["u"] = teamAdmin["_id"],
            ["g"] = ProjectInvitationService.GenericRef("Project", "project", project),
            ["s"] = 1, ["m"] = "旧申请", ["uc"] = new BsonArray()
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = teamAdmin["_id"],
            ["display_name"] = "", ["dns"] = "", ["tags"] = new BsonArray { "translator" },
            ["status"] = "invited", ["version"] = 0
        });

        var body = await BodyAsync(await adminClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "管理员继承" }));
        Assert.Equal("加入成功，管理员继承自团队", body.GetProperty("message").GetString());
        Assert.Equal(project.ToString(), body.GetProperty("group").GetProperty("id").GetString());
        var member = await ProjectMemberAsync(project, teamAdmin);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal(new[] { "admin" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(0, await services.Store.Collection("invitation")
            .CountDocumentsAsync(new BsonDocument("u", teamAdmin["_id"])));
        Assert.Equal(0, await services.Store.Collection("application")
            .CountDocumentsAsync(new BsonDocument("u", teamAdmin["_id"])));
    }

    [Fact]
    public async Task NoCheckApplicationIsApprovedImmediatelyForProjectAndTeam()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner, teamCheck: 1, projectCheck: 1);
        var projectApplicant = await UserAsync();
        var teamApplicant = await UserAsync();
        using var projectClient = Client(projectApplicant);
        using var teamClient = Client(teamApplicant);

        var projectBody = await BodyAsync(await projectClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "免审项目" }));
        Assert.Equal("Successfully joined.", projectBody.GetProperty("message").GetString());
        Assert.Equal(project.ToString(), projectBody.GetProperty("group").GetProperty("id").GetString());
        var projectApplication = await ApplicationAsync(project, projectApplicant);
        Assert.Equal(2, projectApplication.Integer("s"));
        Assert.Equal(projectApplicant["_id"].AsObjectId, projectApplication["o"].AsObjectId);
        Assert.Equal("active", (await ProjectMemberAsync(project, projectApplicant)).Text("status"));

        var teamBody = await BodyAsync(await teamClient.PostAsJsonAsync(ApplicationPath("team", team),
            new { message = "免审团队" }));
        Assert.Equal("Successfully joined.", teamBody.GetProperty("message").GetString());
        var teamApplication = await ApplicationAsync(team, teamApplicant, "team");
        Assert.Equal(2, teamApplication.Integer("s"));
        Assert.Equal(teamApplicant["_id"].AsObjectId, teamApplication["o"].AsObjectId);
        Assert.Equal("active", (await services.Store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = team, ["user"] = teamApplicant["_id"]
        }).FirstAsync()).Text("status"));
    }

    [Fact]
    public async Task PendingInvitationIsAcceptedBeforeAllowApplyCheck()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner, projectAllow: 1);
        var target = await UserAsync();
        using var applicant = Client(target);
        var role = await RoleAsync("project", "translator");
        await BodyAsync(await owner.PostAsJsonAsync($"/v1/project/{project}/invitations", new
        {
            user_id = target["_id"].ToString(), role_id = role.ToString(), message = "先邀请"
        }));

        var body = await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "后申请" }));
        Assert.Equal("The administrator previously invited you, so you have been directly added.", body.GetProperty("message").GetString());
        Assert.Equal(2, (await services.Store.Collection("invitation").Find(
            new BsonDocument("u", target["_id"])).FirstAsync()).Integer("s"));
        Assert.Equal("active", (await ProjectMemberAsync(project, target)).Text("status"));
        Assert.Equal(0, await services.Store.Collection("application")
            .CountDocumentsAsync(new BsonDocument("u", target["_id"])));
    }

    [Fact]
    public async Task RelatedApplicationsKeepTheCheckerSnapshotAfterPermissionsChange()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "快照" }));
        var application = await ApplicationAsync(project, target);
        Assert.Contains(creator["_id"].AsObjectId, application["uc"].AsBsonArray.Select(v => v.AsObjectId));
        var approved = await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }));
        Assert.Equal("translator", approved.GetProperty("application").GetProperty("user_role")
            .GetProperty("system_code").GetString());

        await services.Store.Collection("project_member").DeleteOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = creator["_id"]
        });
        var related = Assert.Single((await BodyAsync(await owner.GetAsync(
            "/v1/user/related-applications?status=2"))).EnumerateArray());
        Assert.Equal(application["_id"].ToString(), related.GetProperty("id").GetString());
        Assert.Equal(target["_id"].ToString(), related.GetProperty("user").GetProperty("id").GetString());
        Assert.Contains(creator["_id"].AsObjectId, application["uc"].AsBsonArray.Select(v => v.AsObjectId));
    }

    [Fact]
    public async Task ApplicationValidationPermissionsAndLegacyPutRejection()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var outsider = await UserAsync();
        using var outsiderClient = Client(outsider);

        await ErrorAsync(await services.App.CreateClient().GetAsync(ApplicationPath("project", project)), 1003);
        await ErrorAsync(await outsiderClient.GetAsync(ApplicationPath("project", project)), 103);
        await ErrorAsync(await owner.GetAsync(ApplicationPath("project", ObjectId.GenerateNewId())), 4001);
        await ErrorAsync(await owner.GetAsync(ApplicationPath("team", ObjectId.GenerateNewId())), 3001);
        await ErrorAsync(await owner.GetAsync("/v1/nope/x/applications"), 5001);
        await ErrorAsync(await owner.GetAsync(ApplicationPath("project", project) + "?status=abc"), 2);
        await ErrorAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(ObjectId.GenerateNewId()),
            new { allow = true }), 5007);

        var invalidBodies = new object?[]
        {
            null,
            new { },
            new { message = 3 },
            new { message = new { text = "x" } }
        };
        foreach (var invalid in invalidBodies)
        {
            var response = invalid is null
                ? await outsiderClient.PostAsync(ApplicationPath("project", project),
                    new StringContent("not-json", System.Text.Encoding.UTF8, "application/json"))
                : await outsiderClient.PostAsJsonAsync(ApplicationPath("project", project), invalid);
            await ErrorAsync(response, 2);
        }
        await BodyAsync(await outsiderClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "x", unknown = 1, display_name = "ignored" }));
        var ignoredExtra = await ApplicationAsync(project, outsider);
        Assert.Equal("x", ignoredExtra.Text("m"));
        await BodyAsync(await outsiderClient.DeleteAsync(ApplicationEndpoint(ignoredExtra["_id"].AsObjectId)));

        // No 140-rune limit and an empty message are valid for applications.
        var member = await UserAsync();
        await TeamMemberAsync(team, member);
        using var memberClient = Client(member);
        await BodyAsync(await memberClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "" }));
        var pending = await ApplicationAsync(project, member);
        await BodyAsync(await memberClient.DeleteAsync(ApplicationEndpoint(pending["_id"].AsObjectId)));

        await BodyAsync(await outsiderClient.PostAsJsonAsync(ApplicationPath("team", team),
            new { message = new string('x', 300) }));
        var teamApplication = await ApplicationAsync(team, outsider, "team");
        Assert.Equal(300, teamApplication.Text("m").Length);

        // The legacy URL only registered PUT; the API has no method on it.
        var put = await owner.PutAsJsonAsync(ApplicationEndpoint(pending["_id"].AsObjectId), new { allow = true });
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, put.StatusCode);
    }

    [Fact]
    public async Task FinishedApplicationCannotBeChangedAndDeleteChecksStatusFirst()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "完成" }));
        var application = await ApplicationAsync(project, target);
        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }));

        await ErrorAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = false }), 5011);
        await ErrorAsync(await applicant.DeleteAsync(ApplicationEndpoint(application["_id"].AsObjectId)), 5011);
        using var stranger = Client(await UserAsync());
        await ErrorAsync(await stranger.DeleteAsync(ApplicationEndpoint(application["_id"].AsObjectId)), 5011);
        Assert.Equal(2, (await ApplicationAsync(project, target)).Integer("s"));
        Assert.Equal("active", (await ProjectMemberAsync(project, target)).Text("status"));
    }

    [Fact]
    public async Task ApprovalHonoursLockedProjectPositions()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument("ple", true)));
        var manager = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = manager["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = manager.Text("n"), ["status"] = "active", ["version"] = 0
        });
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "锁定职位" }));
        var application = await ApplicationAsync(project, target);
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = target["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = target.Text("n"), ["status"] = "invited", ["version"] = 0
        });

        using var managerClient = Client(manager);
        await ErrorAsync(await managerClient.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }), 103);
        Assert.Equal(1, (await ApplicationAsync(project, target)).Integer("s"));

        await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal(new[] { "admin" }, member["tags"].AsBsonArray.Select(v => v.AsString));
    }

    [Fact]
    public async Task ConcurrentApproveAndDenyAllowExactlyOneTerminalTransition()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var manager = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = manager["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = manager.Text("n"), ["status"] = "active", ["version"] = 0
        });
        using var managerClient = Client(manager);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "并发审核" }));
        var application = await ApplicationAsync(project, target);

        var allowTask = owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true });
        var denyTask = managerClient.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = false });
        await Task.WhenAll(allowTask, denyTask);
        var responses = new[] { await allowTask, await denyTask };
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        var failed = responses.Single(response => !response.IsSuccessStatusCode);
        await ErrorAsync(failed, 5011);

        var final = await ApplicationAsync(project, target);
        var member = await ProjectMemberAsync(project, target);
        if (final.Integer("s") == 2)
        {
            Assert.Equal("active", member.Text("status"));
            Assert.Equal(creator["_id"].AsObjectId, final["o"].AsObjectId);
        }
        else
        {
            Assert.Equal(3, final.Integer("s"));
            Assert.Equal("removed", member.Text("status"));
            Assert.Equal(manager["_id"].AsObjectId, final["o"].AsObjectId);
        }
    }

    [Fact]
    public async Task ConcurrentApplyAndInvitationNeverLeaveAPendingDuplicate()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);

        var applyTask = applicant.PostAsJsonAsync(ApplicationPath("project", project), new { message = "并发申请" });
        var inviteTask = owner.PostAsJsonAsync($"/v1/project/{project}/invitations", new
        {
            user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "并发邀请"
        });
        await Task.WhenAll(applyTask, inviteTask);
        await BodyAsync(await applyTask);
        await BodyAsync(await inviteTask);

        Assert.Equal("active", (await ProjectMemberAsync(project, target)).Text("status"));
        var pendingApplications = await services.Store.Collection("application").CountDocumentsAsync(
            new BsonDocument { ["u"] = target["_id"], ["s"] = 1 });
        var pendingInvitations = await services.Store.Collection("invitation").CountDocumentsAsync(
            new BsonDocument { ["u"] = target["_id"], ["s"] = 1 });
        Assert.Equal(0, pendingApplications);
        Assert.Equal(0, pendingInvitations);
        Assert.Equal(1, await services.Store.Collection("project_member")
            .CountDocumentsAsync(new BsonDocument { ["project"] = project, ["user"] = target["_id"] }));
    }

    [Fact]
    public async Task UserRoleDescribesApplicantNotViewerAndTracksApplicantRelation()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var applicant = Client(target);
        await BodyAsync(await applicant.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "申请人角色" }));
        var application = await ApplicationAsync(project, target);

        var approved = await BodyAsync(await owner.PatchAsJsonAsync(ApplicationEndpoint(application["_id"].AsObjectId),
            new { allow = true }));
        var approvedView = approved.GetProperty("application");
        Assert.Equal("translator", approvedView.GetProperty("user_role").GetProperty("system_code").GetString());
        Assert.Equal("creator", approvedView.GetProperty("group").GetProperty("role")
            .GetProperty("system_code").GetString());

        var listed = Assert.Single((await BodyAsync(await owner.GetAsync(
            ApplicationPath("project", project) + "?status=2"))).EnumerateArray());
        Assert.Equal("translator", listed.GetProperty("user_role").GetProperty("system_code").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("group").GetProperty("role").ValueKind);

        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument { ["project"] = project, ["user"] = target["_id"] },
            new BsonDocument("$set", new BsonDocument("status", "removed")));
        var related = Assert.Single((await BodyAsync(await owner.GetAsync(
            "/v1/user/related-applications?status=2"))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, related.GetProperty("user_role").ValueKind);
        Assert.Equal("creator", related.GetProperty("group").GetProperty("role")
            .GetProperty("system_code").GetString());
    }

    [Fact]
    public async Task CheckUserSnapshotMatchesRoleTagsAndExistingActiveUsers()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var first = await UserAsync();
        using var firstClient = Client(first);
        await BodyAsync(await firstClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "首个申请" }));
        var firstApplication = await ApplicationAsync(project, first);
        Assert.Contains(creator["_id"].AsObjectId,
            firstApplication["uc"].AsBsonArray.Select(v => v.AsObjectId));

        var tagged = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = tagged["_id"],
            ["tags"] = new BsonArray { "translator", "admin" }, ["display_name"] = tagged.Text("n"),
            ["status"] = "active", ["version"] = 0
        });

        var coordinatorRole = await RoleAsync("project", "coordinator");
        await services.Store.Collection("project_role").UpdateOneAsync(
            new BsonDocument("_id", coordinatorRole),
            new BsonDocument("$addToSet", new BsonDocument("m_p", 101)));
        var proofreader = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = proofreader["_id"],
            ["tags"] = new BsonArray { "proofreader" }, ["display_name"] = proofreader.Text("n"),
            ["status"] = "active", ["version"] = 0
        });

        var inherited = await UserAsync();
        await TeamMemberAsync(team, inherited, "admin");
        var removed = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = removed["_id"],
            ["tags"] = new BsonArray { "admin" }, ["display_name"] = removed.Text("n"),
            ["status"] = "removed", ["version"] = 0
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["external_id"] = ObjectId.GenerateNewId(),
            ["tags"] = new BsonArray { "admin" }, ["display_name"] = "外部成员",
            ["status"] = "active", ["version"] = 0
        });
        var dangling = ObjectId.GenerateNewId();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = dangling,
            ["tags"] = new BsonArray { "admin" }, ["display_name"] = "悬空用户",
            ["status"] = "active", ["version"] = 0
        });

        var fresh = await UserAsync();
        using var freshClient = Client(fresh);
        await BodyAsync(await freshClient.PostAsJsonAsync(ApplicationPath("project", project),
            new { message = "快照验证" }));
        var snapshot = (await ApplicationAsync(project, fresh))["uc"].AsBsonArray
            .Select(v => v.AsObjectId).ToHashSet();
        Assert.Contains(creator["_id"].AsObjectId, snapshot);
        Assert.Contains(tagged["_id"].AsObjectId, snapshot);
        Assert.Contains(proofreader["_id"].AsObjectId, snapshot);
        Assert.DoesNotContain(inherited["_id"].AsObjectId, snapshot);
        Assert.DoesNotContain(removed["_id"].AsObjectId, snapshot);
        Assert.DoesNotContain(dangling, snapshot);
    }

    [Fact]
    public async Task ApplicationCreateTimeUsesWholeSecondUtcIsoFromObjectId()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var id = ObjectId.Parse("507f1f77bcf86cd799439011");
        await services.Store.Collection("application").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id, ["u"] = target["_id"], ["o"] = creator["_id"],
            ["g"] = ProjectInvitationService.GenericRef("Project", "project", project),
            ["s"] = 2, ["m"] = "时间", ["uc"] = new BsonArray()
        });

        var listed = Assert.Single((await BodyAsync(await owner.GetAsync(
            ApplicationPath("project", project)))).EnumerateArray());
        Assert.Equal(id.ToString(), listed.GetProperty("id").GetString());
        Assert.Equal("2012-10-17T21:13:27+00:00", listed.GetProperty("create_time").GetString());
    }

    // ---- helpers ----

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];
    private static string ApplicationPath(string scope, ObjectId group) => $"/v1/{scope}/{group}/applications";
    private static string ApplicationEndpoint(ObjectId id) => $"/v1/applications/{id}";

    private async Task<(ObjectId Team, ObjectId Project)> SetupAsync(HttpClient client, int teamAllow = 2,
        int teamCheck = 2, int projectAllow = 2, int projectCheck = 2)
    {
        var teamBody = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name = Name("team"), intro = "", allow_apply_type = teamAllow, application_check_type = teamCheck,
            default_role = (await RoleAsync("team", "member")).ToString()
        }));
        var team = ObjectId.Parse(teamBody.GetProperty("team").GetProperty("id").GetString());
        var projectBody = await BodyAsync(await client.PostAsJsonAsync($"/v1/teams/{team}/projects", new
        {
            name = Name("project"), intro = "", allow_apply_type = projectAllow,
            application_check_type = projectCheck, source_language = "ja",
            target_languages = new[] { "zh-CN" }
        }));
        return (team, ObjectId.Parse(projectBody.GetProperty("project").GetProperty("id").GetString()));
    }

    private async Task<ObjectId> RoleAsync(string scope, string code) =>
        (await services.Store.Collection(scope + "_role").Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId;

    private Task<BsonDocument> ApplicationAsync(ObjectId group, BsonDocument user, string scope = "project") =>
        services.Store.Collection("application").Find(new BsonDocument
        {
            ["u"] = user["_id"], ["g._ref.$id"] = group
        }).Sort(Builders<BsonDocument>.Sort.Descending("_id")).FirstAsync();

    private Task<BsonDocument> ProjectMemberAsync(ObjectId project, BsonDocument user) =>
        services.Store.Collection("project_member")
            .Find(new BsonDocument { ["project"] = project, ["user"] = user["_id"] }).FirstAsync();

    private async Task<BsonDocument> UserAsync(bool admin = true)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["n"] = name, ["ns"] = SearchText.Normalize(name), ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["e"] = name + "@example.test", ["p"] = Password,
            ["admin"] = admin, ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task TeamMemberAsync(ObjectId team, BsonDocument user, string baseTag = "member")
    {
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["team"] = team, ["user"] = user["_id"],
            ["base_tag"] = baseTag, ["status"] = "active", ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray { "translator", "proofreader" },
            ["default_display_name"] = "", ["aliases"] = new BsonArray(), ["asrch"] = new BsonArray(),
            ["version"] = 0
        });
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task ErrorAsync(HttpResponseMessage response, int code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode, text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetInt32());
    }
}
