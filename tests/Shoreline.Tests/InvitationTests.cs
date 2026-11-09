using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class InvitationTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task TagsCreatePendingInvitationAndReuseWithoutVersionOrAuditChanges()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var form = new { user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "欢迎加入" };
        var first = await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), form));
        var view = first.GetProperty("invitation");
        var id = ObjectId.Parse(view.GetProperty("id").GetString());
        Assert.Equal(1, view.GetProperty("status").GetInt32());
        Assert.Equal("project", view.GetProperty("group_type").GetString());
        Assert.Equal(creator["_id"].ToString(), view.GetProperty("operator").GetProperty("id").GetString());
        Assert.Equal(target["_id"].ToString(), view.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(new[] { "admin" }, Tags(view));
        Assert.True(DateTimeOffset.TryParse(view.GetProperty("create_time").GetString(), out _));
        var invitation = await InvitationAsync(id);
        Assert.Equal("欢迎加入", invitation.Text("m"));
        Assert.Equal("Project", invitation["g"]["_cls"].AsString);
        Assert.Equal("project", invitation["g"]["_ref"]["$ref"].AsString);
        Assert.Equal(project, invitation["g"]["_ref"]["$id"].AsObjectId);
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("invited", member.Text("status"));
        Assert.Equal(target.Text("n"), member.Text("display_name"));
        Assert.Equal(1, (await ProjectAsync(project)).Integer("m_uc"));
        var auditCount = await AuditCountAsync(project);

        var second = await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), form));
        Assert.Equal(id.ToString(), second.GetProperty("invitation").GetProperty("id").GetString());
        Assert.Equal(member.Integer("version"), (await ProjectMemberAsync(project, target)).Integer("version"));
        Assert.Equal(auditCount, await AuditCountAsync(project));
        Assert.Equal(1, await services.Store.Collection("invitation").CountDocumentsAsync(new BsonDocument("u", target["_id"])));
    }

    [Fact]
    public async Task TeamMemberInvitationJoinsDirectlyWithRequestedTagsAndTeamDisplayName()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var target = await UserAsync();
        await TeamMemberAsync(team, target, displayName: "团队别名");
        var body = await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = target["_id"].ToString(), tags = new[] { "proofreader" }, message = "加入项目"
        }));
        Assert.Contains("已直接加入", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("invitation", out _));
        Assert.Equal(2, body.GetProperty("project").GetProperty("user_count").GetInt32());
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal("团队别名", member.Text("display_name"));
        Assert.Equal(new[] { "proofreader" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(0, await services.Store.Collection("invitation").CountDocumentsAsync(new BsonDocument("u", target["_id"])));
    }

    [Fact]
    public async Task PutTagsUpdatesProjectionButKeepsLegacyRoleAndCreationTime()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var before = await InvitationAsync(id);
        var member = await ProjectMemberAsync(project, target);
        await BodyAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { tags = Array.Empty<string>() }));
        var changed = await ProjectMemberAsync(project, target);
        Assert.Empty(changed["tags"].AsBsonArray);
        Assert.Equal("invited", changed.Text("status"));
        Assert.Equal(member.Integer("version") + 1, changed.Integer("version"));
        var after = await InvitationAsync(id);
        Assert.Equal(before["r"], after["r"]);
        Assert.Equal(before["c"], after["c"]);
        Assert.Equal(1, after.Integer("s"));
        var count = await AuditCountAsync(project);
        await BodyAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { tags = Array.Empty<string>() }));
        Assert.Equal(changed.Integer("version"), (await ProjectMemberAsync(project, target)).Integer("version"));
        Assert.Equal(count, await AuditCountAsync(project));
    }

    [Fact]
    public async Task AcceptPreservesProjectionTagsAndRejectsOtherActorsAndFinishedInvitations()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        await ErrorAsync(await owner.PatchAsJsonAsync(InvitationPath(id), new { allow = true }), 103);
        var accepted = await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true }));
        Assert.Equal("已接受", accepted.GetProperty("message").GetString());
        Assert.Equal(2, accepted.GetProperty("group").GetProperty("user_count").GetInt32());
        Assert.Equal(2, (await InvitationAsync(id)).Integer("s"));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("active", member.Text("status"));
        Assert.Equal(new[] { "admin" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(2, (await ProjectAsync(project)).Integer("m_uc"));
        await ErrorAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = false }), 5010);
        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { tags = new[] { "admin" } }), 5010);
    }

    [Fact]
    public async Task DenyAndDeleteRemovePendingProjectionAndReinviteReusesMember()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var denied = await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = false }));
        Assert.Equal("已拒绝", denied.GetProperty("message").GetString());
        Assert.False(denied.TryGetProperty("group", out _));
        Assert.Equal(3, (await InvitationAsync(id)).Integer("s"));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("removed", member.Text("status"));
        Assert.IsType<BsonDateTime>(member["removed_time"]);
        await ErrorAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true }), 5010);

        var again = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        Assert.NotEqual(id, again);
        Assert.Equal(member["_id"], (await ProjectMemberAsync(project, target))["_id"]);
        Assert.Equal("invited", (await ProjectMemberAsync(project, target)).Text("status"));
        await ErrorAsync(await recipient.DeleteAsync(InvitationPath(again)), 103);
        await BodyAsync(await owner.DeleteAsync(InvitationPath(again)));
        Assert.Equal(0, await services.Store.Collection("invitation").CountDocumentsAsync(new BsonDocument("_id", again)));
        Assert.Equal("removed", (await ProjectMemberAsync(project, target)).Text("status"));
        Assert.Equal(1, (await ProjectAsync(project)).Integer("m_uc"));
    }

    [Fact]
    public async Task GroupAndPersonalListsFilterSortPaginateAndEnforcePermissions()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var first = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(first), new { allow = false }));
        var second = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var other = await UserAsync();
        await InviteAsync(owner, "project", project, other, tags: ["admin"]);
        var all = await BodyAsync(await owner.GetAsync(GroupPath("project", project)));
        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal(new[] { 1, 1, 3 }, all.EnumerateArray().Select(row => row.GetProperty("status").GetInt32()));
        using var response = await owner.GetAsync(GroupPath("project", project) + "?status=1&limit=1&page=2");
        Assert.Equal("2", Assert.Single(response.Headers.GetValues("X-Pagination-Count")));
        Assert.Single((await BodyAsync(response)).EnumerateArray());
        var personal = await BodyAsync(await recipient.GetAsync("/v1/user/invitations?status=1,3"));
        Assert.Equal(new[] { second.ToString(), first.ToString() }, personal.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        await ErrorAsync(await recipient.GetAsync(GroupPath("project", project)), 103);
        using var anonymous = services.App.CreateClient();
        await ErrorAsync(await anonymous.GetAsync("/v1/user/invitations"), 1003);
    }

    [Fact]
    public async Task LegacyProjectInvitationsCoverRoleUpdatesDuplicateJoinedCapacityAndMissingErrors()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var translator = await RoleAsync("project", "translator");
        var id = await InviteAsync(owner, "project", project, target, role: translator);
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), role_id = translator.ToString(), message = "重复" }), 5004);
        var stored = await InvitationAsync(id);
        var proofreader = await RoleAsync("project", "proofreader");
        await BodyAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { role_id = proofreader.ToString() }));
        Assert.Equal(stored["c"], (await InvitationAsync(id))["c"]);
        await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true }));
        Assert.Equal(new[] { "proofreader" }, (await ProjectMemberAsync(project, target))["tags"].AsBsonArray.Select(v => v.AsString));
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), role_id = translator.ToString(), message = "重复" }), 5003);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project), new BsonDocument("$set", new BsonDocument("u", 2)));
        var outsider = await UserAsync();
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = outsider["_id"].ToString(), tags = new[] { "admin" }, message = "满员" }), 5012);
        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(ObjectId.GenerateNewId()), new { tags = new[] { "admin" } }), 5006);
        await ErrorAsync(await owner.GetAsync(GroupPath("invalid", project)), 5001);
    }

    [Fact]
    public async Task TagsValidationAndPositionLockDoNotBypassAuthorization()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var outsider = await UserAsync();
        using var stranger = Client(outsider);
        await ErrorAsync(await stranger.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "无权限" }), 103);
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), tags = new[] { "creator" }, message = "受保护" }), 5116);
        var manager = await UserAsync();
        await TeamMemberAsync(team, manager, baseTag: "admin");
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = manager["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = manager.Text("n"), ["status"] = "active", ["version"] = 0
        });
        var invitation = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument { ["ple"] = true }));
        using var admin = Client(manager);
        await ErrorAsync(await admin.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "锁定" }), 103);
        await ErrorAsync(await admin.PutAsJsonAsync(InvitationPath(invitation), new { tags = new[] { "admin" } }), 103);
    }

    [Fact]
    public async Task TeamInvitationAcceptRestoresRemovedMemberAndKeepsIdentityData()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, _) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var memberId = await TeamMemberAsync(team, target, status: "removed", displayName: "保留别名");
        var id = await InviteAsync(owner, "team", team, target, role: await RoleAsync("team", "admin"));
        var list = await BodyAsync(await owner.GetAsync(GroupPath("team", team)));
        Assert.Equal(JsonValueKind.Null, Assert.Single(list.EnumerateArray()).GetProperty("tags").ValueKind);
        await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true }));
        var restored = await services.Store.Collection("team_member").Find(new BsonDocument("_id", memberId)).FirstAsync();
        Assert.Equal("active", restored.Text("status"));
        Assert.Equal("admin", restored.Text("base_tag"));
        Assert.Equal("保留别名", restored.Text("default_display_name"));
        Assert.Equal(1, restored.Integer("version"));
        Assert.Equal(2, (await services.Store.Collection("team").Find(new BsonDocument("_id", team)).FirstAsync()).Integer("m_uc"));
    }

    [Fact]
    public async Task StalePendingInvitationCancellationNeverDowngradesAnActiveMember()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var member = await ProjectMemberAsync(project, target);
        await services.Store.Collection("project_member").UpdateOneAsync(new BsonDocument("_id", member["_id"]),
            new BsonDocument("$set", new BsonDocument("status", "active")));
        await BodyAsync(await owner.DeleteAsync(InvitationPath(id)));
        var current = await ProjectMemberAsync(project, target);
        Assert.Equal("active", current.Text("status"));
        Assert.Equal(member.Integer("version"), current.Integer("version"));
    }

    [Fact]
    public async Task ConcurrentTagsInvitationsShareOnePendingInvitationAndOneProjection()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var form = new Dictionary<string, object?>
        {
            ["user_id"] = target["_id"].ToString(), ["tags"] = new[] { "admin" }, ["message"] = "并发邀请"
        };

        var responses = await Task.WhenAll(
            owner.PostAsJsonAsync(GroupPath("project", project), form),
            owner.PostAsJsonAsync(GroupPath("project", project), form));
        var bodies = await Task.WhenAll(responses.Select(BodyAsync));
        var ids = bodies.Select(body => body.GetProperty("invitation").GetProperty("id").GetString()).Distinct().ToArray();
        Assert.Single(ids);
        Assert.All(bodies, body => Assert.Equal(1, body.GetProperty("invitation").GetProperty("status").GetInt32()));

        var id = ObjectId.Parse(ids[0]);
        Assert.Equal(1, await services.Store.Collection("invitation").CountDocumentsAsync(
            new BsonDocument { ["u"] = target["_id"], ["s"] = 1 }));
        Assert.Equal(1, await services.Store.Collection("project_member").CountDocumentsAsync(
            new BsonDocument { ["project"] = project, ["user"] = target["_id"] }));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("invited", member.Text("status"));
        Assert.Equal(new[] { "admin" }, member["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(0, member.Integer("version"));
        Assert.Equal(1, await AuditCountAsync(project));
        Assert.Equal(id, (await InvitationAsync(id))["_id"].AsObjectId);
    }

    [Fact]
    public async Task ConcurrentAcceptAndDenyAllowExactlyOneTerminalTransition()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);

        var accept = recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true });
        var deny = recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = false });
        var responses = await Task.WhenAll(accept, deny);
        var succeeded = responses.Where(response => response.IsSuccessStatusCode).ToArray();
        var failed = responses.Where(response => !response.IsSuccessStatusCode).ToArray();
        Assert.Single(succeeded);

        Assert.Single(failed);
        await ErrorAsync(failed[0], 5010);
        var winner = await BodyAsync(Assert.Single(succeeded));

        var invitation = await InvitationAsync(id);
        var member = await ProjectMemberAsync(project, target);
        var projectDoc = await ProjectAsync(project);
        if ((int)invitation.Integer("s") == 2)
        {
            Assert.Equal("已接受", winner.GetProperty("message").GetString());
            Assert.Equal("active", member.Text("status"));
            Assert.Equal(2, (int)projectDoc.Integer("m_uc"));
        }
        else
        {
            Assert.Equal(3, (int)invitation.Integer("s"));
            Assert.Equal("已拒绝", winner.GetProperty("message").GetString());
            Assert.Equal("removed", member.Text("status"));
            Assert.Equal(1, (int)projectDoc.Integer("m_uc"));
            Assert.IsType<BsonDateTime>(member["removed_time"]);
        }
    }

    [Fact]
    public async Task InvalidInvitationInputsReturnValidationErrorsWithoutWrites()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var user = target["_id"].ToString();
        var path = GroupPath("project", project);
        async Task ExpectValidationError(HttpResponseMessage response)
        {
            await ErrorAsync(response, 2);
            Assert.Equal(0, await services.Store.Collection("invitation").CountDocumentsAsync(
                new BsonDocument { ["u"] = target["_id"] }));
            Assert.Equal(0, await services.Store.Collection("project_member").CountDocumentsAsync(
                new BsonDocument { ["project"] = project, ["user"] = target["_id"] }));
        }

        await ExpectValidationError(await owner.PostAsJsonAsync(path, new { user_id = user, tags = new[] { "admin" } }));
        await ExpectValidationError(await owner.PostAsJsonAsync(path, new
        {
            user_id = user, tags = new[] { "admin" }, message = new string('喵', 141)
        }));
        await ExpectValidationError(await owner.PostAsJsonAsync(path, new
        {
            user_id = user, tags = new object[] { 1, true }, message = "非法 tags"
        }));
        await ExpectValidationError(await owner.PostAsJsonAsync(path, new { user_id = user, message = "无角色无标签" }));
        await ExpectValidationError(await owner.PostAsJsonAsync(path, new
        {
            user_id = user, tags = Array.Empty<string>(), message = "空标签"
        }));
        await ExpectValidationError(await owner.PostAsync(path,
            new StringContent("{not-json", Encoding.UTF8, "application/json")));
        await ErrorAsync(await owner.GetAsync(path + "?status=abc"), 2);

        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { tags = 5 }), 2);
        await ErrorAsync(await owner.PutAsync(InvitationPath(id),
            new StringContent("[", Encoding.UTF8, "application/json")), 2);
        await ErrorAsync(await owner.PatchAsJsonAsync(InvitationPath(id), new { }), 2);
        var invitation = await InvitationAsync(id);
        Assert.Equal(1, (int)invitation.Integer("s"));
        Assert.Equal("invited", (await ProjectMemberAsync(project, target)).Text("status"));
    }

    [Fact]
    public async Task LegacyHierarchyAndTeamInvitationLifecycleFollowPythonSemantics()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var outsider = await UserAsync();
        var teamAdmin = await RoleAsync("team", "admin");
        var teamMember = await RoleAsync("team", "member");
        var teamCreator = await RoleAsync("team", "creator");
        var projectAdmin = await RoleAsync("project", "admin");
        var projectCreator = await RoleAsync("project", "creator");

        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), role_id = projectCreator.ToString(), message = "同级" }), 103);
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("team", team), new
        { user_id = target["_id"].ToString(), role_id = teamCreator.ToString(), message = "同级" }), 103);

        var manager = await UserAsync();
        await TeamMemberAsync(team, manager, baseTag: "admin");
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = manager["_id"], ["tags"] = new BsonArray { "admin" },
            ["display_name"] = manager.Text("n"), ["status"] = "active", ["version"] = 0
        });
        using var admin = Client(manager);
        await ErrorAsync(await admin.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), role_id = projectAdmin.ToString(), message = "同级" }), 103);
        await ErrorAsync(await admin.PostAsJsonAsync(GroupPath("project", project), new
        { user_id = target["_id"].ToString(), role_id = projectCreator.ToString(), message = "更高" }), 103);

        var id = await InviteAsync(owner, "team", team, target, role: teamAdmin);
        var list = await BodyAsync(await owner.GetAsync(GroupPath("team", team)));
        Assert.Equal(1, Assert.Single(list.EnumerateArray()).GetProperty("status").GetInt32());
        await BodyAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { role_id = teamMember.ToString() }));
        Assert.Equal(teamMember, (await InvitationAsync(id))["r"]["_ref"]["$id"].AsObjectId);

        await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = false }));
        Assert.Equal(3, (int)(await InvitationAsync(id)).Integer("s"));
        Assert.Equal(0, await services.Store.Collection("team_member").CountDocumentsAsync(
            new BsonDocument { ["team"] = team, ["user"] = target["_id"] }));

        var again = await InviteAsync(owner, "team", team, target, role: teamMember);
        var accepted = await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(again), new { allow = true }));
        Assert.Equal("已接受", accepted.GetProperty("message").GetString());
        var restored = await services.Store.Collection("team_member").Find(
            new BsonDocument { ["team"] = team, ["user"] = target["_id"] }).FirstAsync();
        Assert.Equal("active", restored.Text("status"));
        Assert.Equal("member", restored.Text("base_tag"));
        Assert.Equal(3, (await services.Store.Collection("team").Find(new BsonDocument("_id", team)).FirstAsync()).Integer("m_uc"));

        var pending = await InviteAsync(owner, "team", team, outsider, role: teamMember);
        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(pending), new
        { role_id = teamCreator.ToString() }), 103);
        await BodyAsync(await owner.DeleteAsync(InvitationPath(pending)));
        Assert.Equal(0, await services.Store.Collection("invitation").CountDocumentsAsync(
            new BsonDocument("_id", pending)));
    }

    [Fact]
    public async Task InvitationNestedGroupsUseFullPublicViewsWithoutActorContext()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var teamTarget = await UserAsync();

        var projectView = (await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "公共视图"
        }))).GetProperty("invitation").GetProperty("group");
        Assert.Equal("project", projectView.GetProperty("group_type").GetString());
        Assert.Equal(project.ToString(), projectView.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(projectView.GetProperty("name").GetString()));
        Assert.True(projectView.TryGetProperty("intro", out _));
        Assert.True(projectView.TryGetProperty("max_user", out _));
        Assert.True(projectView.TryGetProperty("user_count", out _));
        Assert.True(projectView.TryGetProperty("project_set", out _));
        Assert.Equal(JsonValueKind.Null, projectView.GetProperty("role").ValueKind);
        Assert.Empty(projectView.GetProperty("effective_permissions").EnumerateArray());
        Assert.Empty(projectView.GetProperty("permission_sources").EnumerateObject());
        var nestedTeam = projectView.GetProperty("team");
        Assert.Equal(team.ToString(), nestedTeam.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, nestedTeam.GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.Null, nestedTeam.GetProperty("base_tag").ValueKind);
        Assert.Empty(nestedTeam.GetProperty("effective_permissions").EnumerateArray());
        Assert.True(nestedTeam.TryGetProperty("user_count", out _));

        var teamMemberRole = await RoleAsync("team", "member");
        var teamView = (await BodyAsync(await owner.PostAsJsonAsync(GroupPath("team", team), new
        {
            user_id = teamTarget["_id"].ToString(), role_id = teamMemberRole.ToString(), message = "团队公共视图"
        }))).GetProperty("invitation").GetProperty("group");
        Assert.Equal("team", teamView.GetProperty("group_type").GetString());
        Assert.Equal(team.ToString(), teamView.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, teamView.GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.Null, teamView.GetProperty("base_tag").ValueKind);
        Assert.Empty(teamView.GetProperty("effective_permissions").EnumerateArray());
        Assert.True(teamView.TryGetProperty("intro", out _));

        var listView = Assert.Single((await BodyAsync(await owner.GetAsync(GroupPath("team", team)))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, listView.GetProperty("group").GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.Null, listView.GetProperty("group").GetProperty("base_tag").ValueKind);
    }

    [Fact]
    public async Task PutWithoutRoleReturnsRoleNotExistAfterCurrentRoleCheck()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var before = await InvitationAsync(id);

        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { }), 102);
        var unchanged = await InvitationAsync(id);
        Assert.Equal(1, unchanged.Integer("s"));
        Assert.Equal(before["r"], unchanged["r"]);
        Assert.Equal("invited", (await ProjectMemberAsync(project, target)).Text("status"));

        // An empty tags array is still the project position route, not a
        // missing-role request.
        await BodyAsync(await owner.PutAsJsonAsync(InvitationPath(id), new { tags = Array.Empty<string>() }));
        Assert.Empty((await ProjectMemberAsync(project, target))["tags"].AsBsonArray);
    }

    [Fact]
    public async Task PutRoleIdSchemaErrorsRejectNonStringsBeforeTagsAndPreserveState()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        var beforeInvitation = await InvitationAsync(id);
        var beforeMember = await ProjectMemberAsync(project, target);

        var invalid = new object[]
        {
            new { role_id = 7 },
            new { role_id = 7, tags = Array.Empty<string>() },
            new { role_id = new { nested = true } },
            new { role_id = new { nested = true }, tags = Array.Empty<string>() },
            new { role_id = new[] { "translator" } },
            new { role_id = new[] { "translator" }, tags = Array.Empty<string>() },
            new { role_id = true },
            new { role_id = true, tags = Array.Empty<string>() }
        };
        foreach (var payload in invalid)
        {
            await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(id), payload), 2);
            var invitation = await InvitationAsync(id);
            Assert.Equal(beforeInvitation["r"], invitation["r"]);
            Assert.Equal(beforeInvitation.Integer("s"), invitation.Integer("s"));
            Assert.Equal(beforeInvitation["c"], invitation["c"]);
            var member = await ProjectMemberAsync(project, target);
            Assert.Equal(beforeMember["tags"], member["tags"]);
            Assert.Equal(beforeMember.Integer("version"), member.Integer("version"));
        }

        // A string that does not resolve is not a schema error: the current
        // role level is checked first and the missing role is 102.
        await ErrorAsync(await owner.PutAsJsonAsync(InvitationPath(id), new
        {
            role_id = ObjectId.GenerateNewId().ToString()
        }), 102);
        Assert.Equal(1, (await InvitationAsync(id)).Integer("s"));
    }

    [Fact]
    public async Task DenyIgnoresUnresolvableInvitationRoleAndRemovesProjection()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);
        await services.Store.Collection("invitation").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument("r", new BsonDocument
            {
                ["_cls"] = "ProjectRole",
                ["_ref"] = new BsonDocument
                {
                    ["$ref"] = "project_role", ["$id"] = ObjectId.GenerateNewId()
                }
            })));

        var denied = await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = false }));
        Assert.Equal("已拒绝", denied.GetProperty("message").GetString());
        Assert.False(denied.TryGetProperty("group", out _));
        Assert.Equal(3, (await InvitationAsync(id)).Integer("s"));
        var member = await ProjectMemberAsync(project, target);
        Assert.Equal("removed", member.Text("status"));
        Assert.NotEqual(BsonNull.Value, member.GetValue("removed_time", BsonNull.Value));
    }

    [Fact]
    public async Task AcceptAttributesProjectionAuditToOriginalInviter()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var target = await UserAsync();
        using var recipient = Client(target);
        var id = await InviteAsync(owner, "project", project, target, tags: ["admin"]);

        await BodyAsync(await recipient.PatchAsJsonAsync(InvitationPath(id), new { allow = true }));
        var audit = await services.Store.Collection("identity_audit_event").Find(new BsonDocument
        {
            ["project_id"] = project.ToString(),
            ["action"] = "project_invitation_projection_active"
        }).Sort(Builders<BsonDocument>.Sort.Descending("create_time")).FirstAsync();
        Assert.Equal(creator["_id"].AsObjectId, audit["actor"].AsObjectId);
        Assert.Equal(target["_id"].ToString(), audit["target_user_id"].AsString);
        Assert.Equal("active", (await ProjectMemberAsync(project, target)).Text("status"));
    }

    [Fact]
    public async Task BodyDisplayNameIsIgnoredAndTeamAliasOrUserNameWins()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var pendingTarget = await UserAsync();
        await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = pendingTarget["_id"].ToString(), tags = new[] { "admin" },
            message = "忽略额外字段", display_name = "伪造显示名"
        }));
        var pendingMember = await ProjectMemberAsync(project, pendingTarget);
        Assert.Equal(pendingTarget.Text("n"), pendingMember.Text("display_name"));

        var teammate = await UserAsync();
        await TeamMemberAsync(team, teammate, displayName: "团队别名");
        await BodyAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = teammate["_id"].ToString(), tags = new[] { "translator" },
            message = "直接加入", display_name = "伪造显示名"
        }));
        var direct = await ProjectMemberAsync(project, teammate);
        Assert.Equal("active", direct.Text("status"));
        Assert.Equal("团队别名", direct.Text("display_name"));

        var missing = ObjectId.GenerateNewId();
        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = missing.ToString(), tags = new[] { "translator" }, message = "不存在"
        }), 1005);
        Assert.Equal(0, await services.Store.Collection("invitation").CountDocumentsAsync(
            new BsonDocument("u", missing)));
        Assert.Equal(0, await services.Store.Collection("project_member").CountDocumentsAsync(
            new BsonDocument("user", missing)));

        await ErrorAsync(await owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = "not-an-objectid", tags = new[] { "translator" }, message = "非法"
        }), 2);
    }

    [Fact]
    public async Task LegacyProjectRoleUsesActiveRelationThenTeamAdminFallback()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (team, project) = await SetupAsync(owner);
        var projectAdmin = await RoleAsync("project", "admin");
        var projectTranslator = await RoleAsync("project", "translator");
        var target = await UserAsync();

        var teamAdmin = await UserAsync();
        await TeamMemberAsync(team, teamAdmin, baseTag: "admin");
        using var inherited = Client(teamAdmin);
        await ErrorAsync(await inherited.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = target["_id"].ToString(), role_id = projectAdmin.ToString(), message = "同级"
        }), 103);

        var teammate = await UserAsync();
        await TeamMemberAsync(team, teammate, baseTag: "member");
        var direct = await BodyAsync(await inherited.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = teammate["_id"].ToString(), role_id = projectTranslator.ToString(), message = "直接加入"
        }));
        Assert.Contains("已直接加入", direct.GetProperty("message").GetString());
        var joined = await ProjectMemberAsync(project, teammate);
        Assert.Equal("active", joined.Text("status"));
        Assert.Equal(new[] { "translator" }, joined["tags"].AsBsonArray.Select(v => v.AsString));

        // Persisted tag order decides the legacy role; the old set-priority
        // implementation would read admin here and allow a translator invite.
        var manager = await UserAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project, ["user"] = manager["_id"],
            ["tags"] = new BsonArray { "translator", "admin" }, ["display_name"] = manager.Text("n"),
            ["status"] = "active", ["version"] = 0
        });
        using var managerClient = Client(manager);
        await ErrorAsync(await managerClient.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = target["_id"].ToString(), role_id = projectTranslator.ToString(), message = "同级翻译"
        }), 103);
    }

    [Fact]
    public async Task JoinProjectCompatibilityRepairsOwnerAndKeepsUnknownRolesUntagged()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument { ["project"] = project, ["user"] = creator["_id"] },
            new BsonDocument("$set", new BsonDocument("tags", new BsonArray { "admin" })));
        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument { ["project"] = project, ["user"] = creator["_id"] },
            new BsonDocument("$set", new BsonDocument("version", 0)));
        var ownerBefore = await ProjectMemberAsync(project, creator);

        using var scope = services.App.Services.CreateScope();
        var invitations = scope.ServiceProvider.GetRequiredService<ProjectInvitationService>();
        var join = typeof(ProjectInvitationService).GetMethod("JoinProjectAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var repaired = await (Task<BsonDocument>)join.Invoke(invitations,
            [projectDoc, creator, "translator", CancellationToken.None])!;
        Assert.Equal(new[] { "creator" }, repaired["tags"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(ownerBefore.Integer("version") + 1, repaired.Integer("version"));

        var stranger = await UserAsync();
        var joined = await (Task<BsonDocument>)join.Invoke(invitations,
            [projectDoc, stranger, "unmapped_role", CancellationToken.None])!;
        Assert.Empty(joined["tags"].AsBsonArray);
        Assert.Equal("active", joined.Text("status"));
        Assert.Equal(stranger.Text("n"), joined.Text("display_name"));
    }

    [Fact]
    public async Task ConcurrentIdentityAddAndInvitationTagsShareOnePendingInvitation()
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

        var invitationTask = owner.PostAsJsonAsync(GroupPath("project", project), new
        {
            user_id = target["_id"].ToString(), tags = new[] { "admin" }, message = "并发邀请"
        });
        var identityTask = managerClient.PostAsJsonAsync($"/v1/projects/{project}/members/changes", new
        {
            operations = new[]
            {
                new
                {
                    operation_id = Name("op"), action = "add",
                    user_id = target["_id"].ToString(), tags = new[] { "admin" }
                }
            }
        });
        await Task.WhenAll(invitationTask, identityTask);

        var invitationBody = await BodyAsync(await invitationTask);
        var identityBody = await BodyAsync(await identityTask);
        Assert.False(identityBody.GetProperty("stopped").GetBoolean());
        var invitationId = ObjectId.Parse(invitationBody.GetProperty("invitation").GetProperty("id").GetString());
        Assert.Equal(1, (await InvitationAsync(invitationId)).Integer("s"));

        var invitations = await services.Store.Collection("invitation")
            .Find(new BsonDocument("u", target["_id"])).ToListAsync();
        var invitation = Assert.Single(invitations);
        Assert.Equal(invitationId, invitation["_id"].AsObjectId);
        Assert.Equal(1, invitation.Integer("s"));
        var members = await services.Store.Collection("project_member")
            .Find(new BsonDocument { ["project"] = project, ["user"] = target["_id"] }).ToListAsync();
        Assert.Single(members);
        Assert.Equal("invited", members[0].Text("status"));
    }

    // Same disposable-user and public team/project setup pattern as ProjectTests.
    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];
    private static string GroupPath(string scope, ObjectId group) => $"/v1/{scope}/{group}/invitations";
    private static string InvitationPath(ObjectId id) => $"/v1/invitations/{id}";
    private static string[] Tags(JsonElement row) => row.GetProperty("tags").EnumerateArray().Select(v => v.GetString()!).ToArray();

    private async Task<(ObjectId Team, ObjectId Project)> SetupAsync(HttpClient client)
    {
        var teamBody = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name = Name("team"), intro = "", allow_apply_type = 1, application_check_type = 2,
            default_role = (await RoleAsync("team", "member")).ToString()
        }));
        var team = ObjectId.Parse(teamBody.GetProperty("team").GetProperty("id").GetString());
        var projectBody = await BodyAsync(await client.PostAsJsonAsync($"/v1/teams/{team}/projects", new
        {
            name = Name("project"), intro = "", allow_apply_type = 1, application_check_type = 2,
            source_language = "ja", target_languages = new[] { "zh-CN" }
        }));
        return (team, ObjectId.Parse(projectBody.GetProperty("project").GetProperty("id").GetString()));
    }

    private async Task<ObjectId> InviteAsync(HttpClient client, string scope, ObjectId group, BsonDocument target,
        string[]? tags = null, ObjectId? role = null)
    {
        var form = new Dictionary<string, object?> { ["user_id"] = target["_id"].ToString(), ["message"] = "欢迎" };
        if (tags is not null) form["tags"] = tags;
        if (role is not null) form["role_id"] = role.ToString();
        var body = await BodyAsync(await client.PostAsJsonAsync(GroupPath(scope, group), form));
        return ObjectId.Parse(body.GetProperty("invitation").GetProperty("id").GetString());
    }

    private async Task<ObjectId> RoleAsync(string scope, string code) =>
        (await services.Store.Collection(scope + "_role").Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId;
    private Task<BsonDocument> InvitationAsync(ObjectId id) => services.Store.Collection("invitation").Find(new BsonDocument("_id", id)).FirstAsync();
    private Task<BsonDocument> ProjectAsync(ObjectId id) => services.Store.Collection("project").Find(new BsonDocument("_id", id)).FirstAsync();
    private Task<BsonDocument> ProjectMemberAsync(ObjectId project, BsonDocument user) => services.Store.Collection("project_member")
        .Find(new BsonDocument { ["project"] = project, ["user"] = user["_id"] }).FirstAsync();
    private Task<long> AuditCountAsync(ObjectId project) => services.Store.Collection("identity_audit_event")
        .CountDocumentsAsync(new BsonDocument("project_id", project.ToString()));

    private async Task<BsonDocument> UserAsync(bool admin = true)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["n"] = name, ["ns"] = SearchText.Normalize(name), ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["e"] = name + "@example.test", ["p"] = Password, ["admin"] = admin, ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<ObjectId> TeamMemberAsync(ObjectId team, BsonDocument user, string status = "active",
        string baseTag = "member", string displayName = "")
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id, ["team"] = team, ["user"] = user["_id"], ["base_tag"] = baseTag, ["status"] = status,
            ["tags"] = new BsonArray(), ["worker_qualifications"] = new BsonArray { "translator", "proofreader" },
            ["default_display_name"] = displayName, ["aliases"] = new BsonArray(), ["asrch"] = new BsonArray(), ["version"] = 0
        });
        return id;
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
        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetInt32());
    }
}
