using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class IdentityPermissionTests(LocalServices services)
{
    [Fact]
    public async Task RemovedProjectMemberBlocksEvenTeamCreatorAndSiteAdminInheritance()
    {
        var (user, team, project, relation) = await SetupAsync("creator");
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();
        var inherited = await permissions.ProjectSnapshotAsync(user, project);
        Assert.True(inherited.Has("MANAGE_MEMBERS"));
        Assert.False(inherited.IsOwner);
        await services.Store.Collection("project_member").InsertOneAsync(Member(user, project, "removed", "creator"));
        var removed = await permissions.ProjectSnapshotAsync(user, project);
        Assert.Empty(removed.EffectivePermissions);
        Assert.False(removed.IsOwner);
        Assert.True((await permissions.TeamSnapshotAsync(user, team)).Has("team:DELETE"));
        Assert.Contains("team_inherited:creator", inherited.PermissionSources["project:ACCESS"]);
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", relation["_id"]),
            new BsonDocument("$set", new BsonDocument("status", "removed")));
        Assert.Empty((await permissions.TeamSnapshotAsync(user, team)).EffectivePermissions);
    }

    [Fact]
    public async Task InvitationsGrantOnlyAccessAndOwnerRequiresActiveCreatorMembership()
    {
        var (user, _, project, relation) = await SetupAsync("member");
        await services.Store.Collection("team_member").DeleteOneAsync(new BsonDocument("_id", relation["_id"]));
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();
        Assert.Empty((await permissions.ProjectSnapshotAsync(user, project)).EffectivePermissions);
        var member = Member(user, project, "invited", "creator");
        await services.Store.Collection("project_member").InsertOneAsync(member);
        var invited = await permissions.ProjectSnapshotAsync(user, project);
        Assert.Equal("project:ACCESS", Assert.Single(invited.EffectivePermissions));
        Assert.False(invited.IsOwner);
        await services.Store.Collection("project_member").UpdateOneAsync(new BsonDocument("_id", member["_id"]),
            new BsonDocument("$set", new BsonDocument("status", "active")));
        var active = await permissions.ProjectSnapshotAsync(user, project);
        Assert.True(active.IsOwner);
        Assert.True(active.Has("CHECK_TRA"));
        Assert.Empty((await permissions.ProjectSnapshotAsync(null, project)).EffectivePermissions);
    }

    [Fact]
    public async Task PolicyOverridesProjectTagsButNotTeamAdminInheritanceAndCompletedProjectsRejectEdits()
    {
        var (user, team, project, relation) = await SetupAsync("member");
        var policy = new BsonDocument { ["team"] = team["_id"],
            ["team_tags"] = new BsonDocument("editor", new BsonDocument("permissions", new BsonArray { "team:CREATE_PROJECT" })),
            ["project_tags"] = new BsonDocument {
                ["translator"] = new BsonDocument("permissions", new BsonArray { "project:ADD_TRA" }),
                ["custom"] = new BsonDocument("permissions", new BsonArray { "project:OUTPUT_TRA" }),
                ["admin"] = new BsonDocument("permissions", new BsonArray()) } };
        await services.Store.Collection("identity_tag_policy").InsertOneAsync(policy);
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", relation["_id"]),
            new BsonDocument("$set", new BsonDocument("tags", new BsonArray { "editor" })));
        await services.Store.Collection("project_member").InsertOneAsync(Member(user, project, "active", "translator", "custom", "missing"));
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();
        var snapshot = await permissions.ProjectSnapshotAsync(user, project);
        Assert.True(snapshot.Has("ADD_TRA"));
        Assert.True(snapshot.Has("OUTPUT_TRA"));
        Assert.False(snapshot.Has("DELETE_TRA"));
        Assert.False(snapshot.Has("team:CREATE_PROJECT"));
        Assert.Contains("team_tag:editor", snapshot.PermissionSources["team:CREATE_PROJECT"]);
        Assert.Equal("project_tag:custom", Assert.Single(snapshot.PermissionSources["project:OUTPUT_TRA"]));
        Assert.True((await permissions.TeamSnapshotAsync(user, team)).Has("team:CREATE_PROJECT"));
        Assert.True(IdentityPermissions.CanProject(snapshot, project, "ADD_TRA"));
        foreach (var status in new[] { 1, 5, 999 })
        {
            project["st"] = status;
            Assert.False(IdentityPermissions.CanProject(snapshot, project, "ADD_TRA"));
            Assert.False(IdentityPermissions.CanProject(snapshot, project, "OUTPUT_TRA"));
            Assert.True(IdentityPermissions.CanProject(snapshot, project, "ACCESS"));
        }
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", relation["_id"]),
            new BsonDocument("$set", new BsonDocument("base_tag", "admin")));
        Assert.True((await permissions.ProjectSnapshotAsync(user, project)).Has("MANAGE_MEMBERS"));
    }

    [Theory]
    [InlineData("raw_provider", false, false, false)]
    [InlineData("scanner", false, false, false)]
    [InlineData("cropper", false, false, false)]
    [InlineData("cleaner", false, false, false)]
    [InlineData("translator", true, true, false)]
    [InlineData("proofreader", true, true, true)]
    [InlineData("typesetter", false, false, false)]
    public async Task WorkerRolesPreserveLegacyPermissions(string tag, bool addTranslation, bool deleteTranslation, bool proofread)
    {
        var (user, _, project, _) = await SetupAsync("member");
        await services.Store.Collection("project_member").InsertOneAsync(Member(user, project, "active", tag));
        var snapshot = await services.App.Services.GetRequiredService<IdentityPermissions>().ProjectSnapshotAsync(user, project);
        Assert.True(snapshot.Has("MOVE_FILE"));
        Assert.Equal(addTranslation, snapshot.Has("ADD_TRA"));
        Assert.Equal(deleteTranslation, snapshot.Has("DELETE_TRA"));
        Assert.Equal(proofread, snapshot.Has("PROOFREAD_TRA"));
        Assert.Equal(proofread, snapshot.Has("CHECK_TRA"));
        Assert.False(snapshot.Has("MANAGE_MEMBERS"));
        Assert.False(snapshot.IsOwner);
    }

    [Fact]
    public async Task PageSnapshotsKeepMembershipAndPoliciesIsolatedByProjectAndTeam()
    {
        var (user, _, project, _) = await SetupAsync("member");
        var other = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["t"] = ObjectId.GenerateNewId() };
        await services.Store.Collection("project_member").InsertOneAsync(Member(user, other, "active", "proofreader"));
        var service = services.App.Services.GetRequiredService<IdentityPermissions>();
        var snapshots = await service.ProjectSnapshotsAsync(user, [project, other]);
        Assert.Equal("project:ACCESS", Assert.Single(snapshots[project["_id"].AsObjectId].EffectivePermissions));
        Assert.True(snapshots[other["_id"].AsObjectId].Has("CHECK_TRA"));
        Assert.Empty(await service.ProjectSnapshotsAsync(user, []));
    }

    [Fact]
    public async Task LifecycleActionsAndLegacyCodesUseTheSameAuthority()
    {
        var (user, _, project, relation) = await SetupAsync("admin");
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();
        var snapshot = await permissions.ProjectSnapshotAsync(user, project);
        Assert.True(snapshot.Has(1010));
        Assert.True(snapshot.Has(1140));
        Assert.False(snapshot.Has(5));
        Assert.False(snapshot.Has(99999));
        Assert.True(await permissions.CanProjectActionAsync(user, project, "complete"));
        Assert.True(await permissions.CanProjectActionAsync(user, project, "reopen"));
        Assert.False(await permissions.CanProjectActionAsync(user, project, "clear"));
        Assert.False(await permissions.CanProjectActionAsync(user, project, "delete"));
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", relation["_id"]),
            new BsonDocument("$set", new BsonDocument("base_tag", "creator")));
        Assert.True(await permissions.CanProjectActionAsync(user, project, "clear"));
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", relation["_id"]),
            new BsonDocument("$set", new BsonDocument("status", "removed")));
        Assert.False(await permissions.CanProjectActionAsync(user, project, "clear"));
        Assert.False(await permissions.CanProjectActionAsync(user, project, "complete"));
    }

    [Fact]
    public async Task LegacyMissingTagsPolicyRefreshAndSerializedSourcesRemainCompatible()
    {
        var (user, team, project, _) = await SetupAsync("member");
        var member = Member(user, project, "active");
        member["tags"] = BsonNull.Value;
        await services.Store.Collection("project_member").InsertOneAsync(member);
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();
        var empty = await permissions.ProjectSnapshotAsync(user, project);
        Assert.Empty(empty.SourceTags);
        Assert.Equal("project:ACCESS", Assert.Single(empty.EffectivePermissions));
        await services.Store.Collection("project_member").UpdateOneAsync(new BsonDocument("_id", member["_id"]),
            new BsonDocument("$set", new BsonDocument("tags", new BsonArray { "custom" })));
        var policy = new BsonDocument { ["team"] = team["_id"], ["project_tags"] = new BsonDocument("custom",
            new BsonDocument("permissions", new BsonArray { "project:CHECK_TRA" })) };
        await services.Store.Collection("identity_tag_policy").InsertOneAsync(policy);
        var granted = await permissions.ProjectSnapshotAsync(user, project);
        Assert.True(granted.Has("CHECK_TRA"));
        var api = System.Text.Json.JsonSerializer.SerializeToElement(granted.ToApi());
        Assert.Equal(member["_id"].ToString(), api.GetProperty("project_member_id").GetString());
        Assert.Equal(project["t"].ToString(), api.GetProperty("team_id").GetString());
        Assert.Equal("project_tag:custom", api.GetProperty("permission_sources").GetProperty("project:CHECK_TRA")[0].GetString());
        Assert.Equal(new[] { "project:ACCESS", "project:CHECK_TRA" }, api.GetProperty("effective_permissions").EnumerateArray().Select(v => v.GetString()));
        await services.Store.Collection("identity_tag_policy").UpdateOneAsync(new BsonDocument("_id", policy["_id"]),
            new BsonDocument("$set", new BsonDocument("project_tags.custom.permissions", new BsonArray())));
        Assert.False((await permissions.ProjectSnapshotAsync(user, project)).Has("CHECK_TRA"));
        // An earlier immutable snapshot is an audit value, never a mutable cache.
        Assert.True(granted.Has("CHECK_TRA"));
    }

    private static BsonDocument Member(BsonDocument user, BsonDocument project, string status, params string[] tags) => new()
    {
        ["_id"] = ObjectId.GenerateNewId(), ["user"] = user["_id"], ["project"] = project["_id"],
        ["status"] = status, ["tags"] = new BsonArray(tags)
    };

    private async Task<(BsonDocument User, BsonDocument Team, BsonDocument Project, BsonDocument Relation)> SetupAsync(string baseTag)
    {
        var user = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["admin"] = true };
        var team = new BsonDocument("_id", ObjectId.GenerateNewId());
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["t"] = team["_id"], ["ou"] = user["_id"], ["st"] = 0 };
        var relation = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["team"] = team["_id"], ["user"] = user["_id"], ["base_tag"] = baseTag, ["status"] = "active" };
        await services.Store.Collection("team_member").InsertOneAsync(relation);
        return (user, team, project, relation);
    }
}
