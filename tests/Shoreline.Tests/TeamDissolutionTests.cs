using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class TeamDissolutionTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task CreatorDissolvesMixedProjectsAndCascadesAllRows()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        await RoleAsync(team["_id"].AsObjectId, "grouped");
        await services.Store.Collection("team_role").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "system-role",
            ["m_o"] = "system"
        });
        var otherTeam = await TeamAsync(actor);
        await RoleAsync(otherTeam["_id"].AsObjectId, "other-team-role");
        var otherProject = await ProjectAsync(otherTeam["_id"].AsObjectId, actor, status: 5);

        var completed = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        var cleared = await ProjectAsync(team["_id"].AsObjectId, actor, status: 1);
        var target = await TargetAsync(completed);
        var file = await FileAsync(completed, "image.png");
        var output = await OutputAsync(completed, target, "out.txt");
        var contentSourceId = ObjectId.GenerateNewId();
        using (var harness = new Harness(services))
        {
            await harness.PutAsync("files/image.png");
            await harness.PutAsync("outputs/" + output["_id"].AsObjectId + "/out.txt");
            var contentSource = new BsonDocument
            {
                ["_id"] = contentSourceId,
                ["f"] = file["_id"]
            };
            await services.Store.Collection("source").InsertOneAsync(contentSource);
            await services.Store.Collection("translation").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = contentSource["_id"]
            });
            await services.Store.Collection("tip").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = contentSource["_id"]
            });
            await services.Store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = file["_id"],
                ["t"] = target["_id"]
            });

            await InsertProjectRowsAsync(completed, target);
            await InsertProjectRowsAsync(cleared, null);
            await InsertTeamRowsAsync(team);
            await services.Store.Collection("application").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["g"] = GenericRef("Project", "project", completed["_id"].AsObjectId)
            });
            await services.Store.Collection("invitation").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["g"] = GenericRef("Team", "team", team["_id"].AsObjectId)
            });
            await services.Store.Collection("audit").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["team"] = team["_id"], ["action"] = "keep"
            });
            await services.Store.Collection("proofread_file").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["team"] = team["_id"], ["token"] = "keep"
            });

            var result = await harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);
            Assert.Equal("解散成功", result.Message);
            Assert.False(File.Exists(Path.Combine(harness.Root, "files", "image.png")));
            Assert.False(File.Exists(Path.Combine(
                harness.Root, "outputs", output["_id"].AsObjectId.ToString()!, "out.txt")));
        }

        Assert.Equal(0L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal(0L, await CountAsync("project", new BsonDocument("t", team["_id"])));
        Assert.Equal(0L, await CountAsync("file", new BsonDocument("_id", file["_id"])));
        Assert.Equal(0L, await CountAsync("output", new BsonDocument("_id", output["_id"])));
        Assert.Equal(0L, await CountAsync("target", new BsonDocument("_id", target["_id"])));
        Assert.Equal(0L, await CountAsync("source", new BsonDocument("f", file["_id"])));
        Assert.Equal(0L, await CountAsync("translation", new BsonDocument("o", contentSourceId)));
        Assert.Equal(0L, await CountAsync("tip", new BsonDocument("o", contentSourceId)));
        Assert.Equal(0L, await CountAsync("file_target_cache", new BsonDocument("f", file["_id"])));
        Assert.Equal(0L, await CountAsync("project_member", new BsonDocument("project", completed["_id"])));
        Assert.Equal(0L, await CountAsync("project_user_relation", new BsonDocument("g", completed["_id"])));
        Assert.Equal(0L, await CountAsync("project_role", new BsonDocument("g", completed["_id"])));
        Assert.Equal(0L, await CountAsync("project_set", new BsonDocument("t", team["_id"])));
        Assert.Equal(0L, await CountAsync("team_member", new BsonDocument("team", team["_id"])));
        Assert.Equal(0L, await CountAsync("team_user_relation", new BsonDocument("g", team["_id"])));
        Assert.Equal(0L, await CountAsync("team_role", new BsonDocument("g", team["_id"])));
        Assert.Equal(1L, await CountAsync("team_role", new BsonDocument("n", "system-role")));
        Assert.Equal(1L, await CountAsync("team_role", new BsonDocument("g", otherTeam["_id"])));
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", otherTeam["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", otherProject["_id"])));
        Assert.Equal(0L, await CountAsync("application", new BsonDocument("_id", new BsonDocument("$exists", true))));
        Assert.Equal(0L, await CountAsync("invitation", new BsonDocument("_id", new BsonDocument("$exists", true))));
        Assert.Equal(1L, await CountAsync("audit", new BsonDocument("team", team["_id"])));
        Assert.Equal(1L, await CountAsync("proofread_file", new BsonDocument("team", team["_id"])));
    }

    [Fact]
    public async Task WorkingProjectRejectsWithoutMutation()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 0);
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(103, error.Code);
        Assert.Equal("此团队含有未完结的项目，不能解散（请先完结或者转移项目）", error.Message);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
        Assert.Equal(0L, await CountAsync("team_lifecycle_lock", new BsonDocument("_id", team["_id"])));
        Assert.Equal("", (await TeamAsync(team["_id"].AsObjectId)).Text("lc_phase"));
    }

    [Fact]
    public async Task ProjectWithMissingStatusIsTreatedAsWorking()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var projectId = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["t"] = team["_id"],
            ["n"] = "legacy-without-status"
        });
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(103, error.Code);
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", projectId)));
    }

    [Fact]
    public async Task NonMemberCannotDissolve()
    {
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var team = await TeamAsync(owner);
        await MemberAsync(team, owner, "creator");
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(outsider, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(103, error.Code);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task TeamMemberWithoutDeleteCannotDissolve()
    {
        var owner = await UserAsync();
        var member = await UserAsync();
        var team = await TeamAsync(owner);
        await MemberAsync(team, owner, "creator");
        await MemberAsync(team, member, "member");
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(member, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(103, error.Code);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task SiteAdminDoesNotInheritTeamDelete()
    {
        var owner = await UserAsync();
        var admin = await UserAsync(admin: true);
        var team = await TeamAsync(owner);
        await MemberAsync(team, owner, "creator");
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(admin, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(103, error.Code);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task CustomTeamDeletePolicyAllowsMember()
    {
        var owner = await UserAsync();
        var member = await UserAsync();
        var team = await TeamAsync(owner);
        await MemberAsync(team, owner, "creator");
        await MemberAsync(team, member, "member");
        await services.Store.Collection("identity_tag_policy").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["team_tags"] = new BsonDocument
            {
                ["member"] = new BsonDocument
                {
                    ["permissions"] = new BsonArray { "team:ACCESS", "team:DELETE" }
                }
            }
        });
        using var harness = new Harness(services);

        var result = await harness.Service.DeleteAsync(member, team["_id"].AsObjectId.ToString()!);

        Assert.Equal("解散成功", result.Message);
        Assert.Equal(0L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task MissingOrInvalidTeamReturns3001()
    {
        var actor = await UserAsync();
        using var harness = new Harness(services);

        var missing = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, ObjectId.GenerateNewId().ToString()!));
        var invalid = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, "not-an-object-id"));

        Assert.Equal(3001, missing.Code);
        Assert.Equal(3001, invalid.Code);
    }

    [Fact]
    public async Task TermBanksAndSourcePossibleTermsAreCleaned()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        var bank = await TermBankAsync(team, actor);
        var terms = new List<BsonDocument>();
        for (var i = 0; i < 205; i++)
        {
            terms.Add(await TermAsync(bank, "o" + i, "t" + i));
        }
        var termIds = new BsonArray(terms.Select(term => term["_id"]));
        var sourceId = ObjectId.GenerateNewId();
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = sourceId,
            ["pt"] = termIds
        });
        var otherTeam = await TeamAsync(actor);
        var foreignProject = await ProjectAsync(otherTeam["_id"].AsObjectId, actor, status: 5);
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", foreignProject["_id"]),
            new BsonDocument("$set", new BsonDocument("tb", new BsonArray { bank["_id"] })));
        using var harness = new Harness(services);

        await harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);

        Assert.Equal(0L, await CountAsync("term_bank", new BsonDocument("_id", bank["_id"])));
        Assert.Equal(0L, await CountAsync("term", new BsonDocument("tb", bank["_id"])));
        var source = await services.Store.Collection("source")
            .Find(new BsonDocument("_id", sourceId)).FirstAsync();
        Assert.Empty(source["pt"].AsBsonArray);
        Assert.Equal(0L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
        var foreign = await services.Store.Collection("project")
            .Find(new BsonDocument("_id", foreignProject["_id"])).FirstAsync();
        Assert.Empty(foreign["tb"].AsBsonArray);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", otherTeam["_id"])));
    }

    [Fact]
    public async Task GenericProjectAndTeamReferencesAreDeleted()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        var projectId = project["_id"].AsObjectId;
        var teamId = team["_id"].AsObjectId;
        await services.Store.Collection("application").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["g"] = new BsonDocument
            {
                ["_cls"] = "Project",
                ["_ref"] = new BsonDocument { ["$id"] = projectId, ["$ref"] = "project" }
            }
        });
        await services.Store.Collection("invitation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["g"] = new BsonDocument
            {
                ["_cls"] = "Team",
                ["_ref"] = new BsonDocument { ["$ref"] = "team", ["$id"] = teamId }
            }
        });
        await services.Store.Collection("application").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["g"] = GenericRef("Team", "team", teamId)
        });
        using var harness = new Harness(services);

        await harness.Service.DeleteAsync(actor, teamId.ToString()!);

        Assert.Equal(0L, await CountAsync("application", new BsonDocument("_id", new BsonDocument("$exists", true))));
        Assert.Equal(0L, await CountAsync("invitation", new BsonDocument("_id", new BsonDocument("$exists", true))));
    }

    [Fact]
    public async Task SystemRolesOtherTeamsProjectsAndAuditArePreserved()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        var otherTeam = await TeamAsync(actor);
        var otherProject = await ProjectAsync(otherTeam["_id"].AsObjectId, actor, status: 5);
        await services.Store.Collection("team_role").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = "system-preserved"
        });
        await services.Store.Collection("team_role").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["g"] = team["_id"], ["n"] = "grouped-deleted"
        });
        var sourceId = ObjectId.GenerateNewId();
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = sourceId, ["f"] = ObjectId.GenerateNewId()
        });
        await services.Store.Collection("audit").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["team"] = team["_id"]
        });
        using var harness = new Harness(services);

        await harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);

        Assert.Equal(1L, await CountAsync("team_role", new BsonDocument("n", "system-preserved")));
        Assert.Equal(0L, await CountAsync("team_role", new BsonDocument("n", "grouped-deleted")));
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", otherTeam["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", otherProject["_id"])));
        Assert.Equal(1L, await CountAsync("source", new BsonDocument("_id", sourceId)));
        Assert.Equal(1L, await CountAsync("audit", new BsonDocument("team", team["_id"])));
        Assert.Equal(0L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
    }

    [Fact]
    public async Task StoragePoolProjectFailsClosedWithoutDeletingDefaultBucketObjects()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument("csid", ObjectId.GenerateNewId())));
        await FileAsync(project, "pool.png");
        using var harness = new Harness(services);
        await harness.PutAsync("files/pool.png");

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(5113, error.Code);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
        Assert.Equal("deleting", (await TeamAsync(team["_id"].AsObjectId)).Text("lc_phase"));
        Assert.Equal(0L, await CountAsync("team_lifecycle_lock", new BsonDocument("_id", team["_id"])));
        Assert.True(File.Exists(Path.Combine(harness.Root, "files", "pool.png")));
    }

    [Fact]
    public async Task StorageFailureLeavesRetryableTeamAndProject()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        await FileAsync(project, "good.png");
        var bad = await FileAsync(project, "../escape");
        using var harness = new Harness(services);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(5113, error.Code);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
        Assert.Equal("deleting", (await TeamAsync(team["_id"].AsObjectId)).Text("lc_phase"));
        Assert.Equal(0L, await CountAsync("team_lifecycle_lock", new BsonDocument("_id", team["_id"])));

        await services.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", bad["_id"]),
            new BsonDocument("$set", new BsonDocument("sa", "fixed.png")));
        var retry = await harness.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);
        Assert.Equal("解散成功", retry.Message);
        Assert.Equal(0L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
    }

    [Fact]
    public async Task CancellationAfterMarkerCanBeReclaimed()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        using var first = new Harness(services, new TeamLifecycleLockHooks
        {
            AfterMarkAsync = (_, _, _) => throw new OperationCanceledException("test cancel")
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            first.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal("deleting", (await TeamAsync(team["_id"].AsObjectId)).Text("lc_phase"));
        Assert.Equal(0L, await CountAsync("team_lifecycle_lock", new BsonDocument("_id", team["_id"])));

        using var second = new Harness(services);
        var result = await second.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);
        Assert.Equal("解散成功", result.Message);
        Assert.Equal(0L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
    }

    [Fact]
    public async Task UnknownClaimCleanupDoesNotLeavePermanentLock()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        var project = await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        using var first = new Harness(services, new TeamLifecycleLockHooks
        {
            AfterInsertAsync = (_, _, _) => throw new InvalidOperationException("ambiguous commit")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!));

        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
        Assert.Equal(1L, await CountAsync("project", new BsonDocument("_id", project["_id"])));
        Assert.Equal(0L, await CountAsync("team_lifecycle_lock", new BsonDocument("_id", team["_id"])));
        Assert.Equal("", (await TeamAsync(team["_id"].AsObjectId)).Text("lc_phase"));

        using var second = new Harness(services);
        var result = await second.Service.DeleteAsync(actor, team["_id"].AsObjectId.ToString()!);
        Assert.Equal("解散成功", result.Message);
    }

    [Fact]
    public async Task ConcurrentDeleteHasSingleOwner()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        await ProjectAsync(team["_id"].AsObjectId, actor, status: 5);
        using var harness = new Harness(services);
        var id = team["_id"].AsObjectId.ToString()!;

        var outcomes = await Task.WhenAll(
            DeleteOutcomeAsync(harness.Service, actor, id),
            DeleteOutcomeAsync(harness.Service, actor, id));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Contains(outcomes, outcome => outcome is 5120 or 3001);
        Assert.Equal(0L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task EnsureWritableRejectsWhileLockHeld()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        using var harness = new Harness(services);

        await using (var lease = await harness.Lifecycle.AcquireAsync(team["_id"].AsObjectId))
        {
            var error = await Assert.ThrowsAsync<ApiError>(() =>
                harness.Lifecycle.EnsureWritableAsync(team["_id"].AsObjectId));
            Assert.Equal(5120, error.Code);
            Assert.True(await harness.Lifecycle.IsDeletingAsync(team["_id"].AsObjectId));
        }

        await harness.Lifecycle.EnsureWritableAsync(team["_id"].AsObjectId);
        Assert.False(await harness.Lifecycle.IsDeletingAsync(team["_id"].AsObjectId));
    }

    [Fact]
    public async Task HttpMissingTokenReturns1003()
    {
        using var client = services.App.CreateClient();
        var response = await client.DeleteAsync("/v1/teams/" + ObjectId.GenerateNewId());
        await AssertErrorAsync(response, 1003);
    }

    [Fact]
    public async Task HttpCreatorCanDissolveTeam()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        using var client = Client(actor);

        var response = await client.DeleteAsync("/v1/teams/" + team["_id"].AsObjectId);
        var body = await BodyAsync(response);

        Assert.Equal("解散成功", body.GetProperty("message").GetString());
        Assert.Equal(0L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task HttpNonMemberGets103()
    {
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var team = await TeamAsync(owner);
        await MemberAsync(team, owner, "creator");
        using var client = Client(outsider);

        var response = await client.DeleteAsync("/v1/teams/" + team["_id"].AsObjectId);

        await AssertErrorAsync(response, 103);
        Assert.Equal(1L, await CountAsync("team", new BsonDocument("_id", team["_id"])));
    }

    [Fact]
    public async Task HttpWorkingProjectGets103()
    {
        var actor = await UserAsync();
        var team = await TeamAsync(actor);
        await MemberAsync(team, actor, "creator");
        await ProjectAsync(team["_id"].AsObjectId, actor, status: 0);
        using var client = Client(actor);

        var response = await client.DeleteAsync("/v1/teams/" + team["_id"].AsObjectId);

        var error = await AssertErrorAsync(response, 103);
        Assert.Equal("此团队含有未完结的项目，不能解散（请先完结或者转移项目）", error.GetProperty("message").GetString());
    }

    private async Task InsertProjectRowsAsync(BsonDocument project, BsonDocument? target)
    {
        var projectId = project["_id"].AsObjectId;
        var user = await UserAsync();
        await services.Store.Collection("project_user_relation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["g"] = project["_id"], ["u"] = user["_id"]
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project["_id"], ["user"] = user["_id"],
            ["status"] = "active", ["tags"] = new BsonArray()
        });
        await services.Store.Collection("project_role").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["g"] = project["_id"], ["n"] = "role"
        });
        if (target is not null)
        {
            await services.Store.Collection("application").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["g"] = GenericRef("Project", "project", projectId)
            });
        }
    }

    private async Task InsertTeamRowsAsync(BsonDocument team)
    {
        await services.Store.Collection("project_set").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["t"] = team["_id"], ["n"] = "default"
        });
        await services.Store.Collection("team_user_relation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["g"] = team["_id"],
            ["u"] = ObjectId.GenerateNewId()
        });
    }

    private async Task<BsonDocument> UserAsync(bool admin = false)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = name + "@example.test",
            ["p"] = Password,
            ["admin"] = admin,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> TeamAsync(BsonDocument owner)
    {
        var id = ObjectId.GenerateNewId();
        var name = "team_" + id.ToString()[12..];
        var team = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["i"] = "",
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task MemberAsync(
        BsonDocument team,
        BsonDocument user,
        string baseTag)
    {
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });
    }

    private async Task RoleAsync(ObjectId teamId, string name)
    {
        await services.Store.Collection("team_role").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["g"] = teamId,
            ["n"] = name,
            ["m_o"] = "member"
        });
    }

    private async Task<BsonDocument> ProjectAsync(ObjectId teamId, BsonDocument owner, int status)
    {
        var id = ObjectId.GenerateNewId();
        var project = new BsonDocument
        {
            ["_id"] = id,
            ["t"] = teamId,
            ["st"] = status,
            ["n"] = "project_" + id.ToString()[12..],
            ["ou"] = owner["_id"],
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow
        };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> TargetAsync(BsonDocument project)
    {
        var target = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["n"] = "target"
        };
        await services.Store.Collection("target").InsertOneAsync(target);
        return target;
    }

    private async Task<BsonDocument> FileAsync(BsonDocument project, string saveName)
    {
        var file = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["t"] = 0,
            ["sa"] = saveName,
            ["n"] = saveName
        };
        await services.Store.Collection("file").InsertOneAsync(file);
        return file;
    }

    private async Task<BsonDocument> OutputAsync(
        BsonDocument project,
        BsonDocument target,
        string fileName)
    {
        var output = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["t"] = target["_id"],
            ["file_name"] = fileName
        };
        await services.Store.Collection("output").InsertOneAsync(output);
        return output;
    }

    private async Task<BsonDocument> TermBankAsync(BsonDocument team, BsonDocument owner)
    {
        var id = ObjectId.GenerateNewId();
        var language = (await services.Store.Collection("language").Find(new BsonDocument()).FirstAsync())["_id"];
        var bank = new BsonDocument
        {
            ["_id"] = id,
            ["t"] = team["_id"],
            ["n"] = "bank_" + id.ToString()[12..],
            ["sl"] = language,
            ["tl"] = language,
            ["ti"] = "",
            ["u"] = owner["_id"],
            ["c"] = DateTime.UtcNow,
            ["e"] = DateTime.UtcNow
        };
        await services.Store.Collection("term_bank").InsertOneAsync(bank);
        return bank;
    }

    private async Task<BsonDocument> TermAsync(BsonDocument bank, string source, string target)
    {
        var term = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["tb"] = bank["_id"],
            ["o"] = source,
            ["t"] = target,
            ["ti"] = ""
        };
        await services.Store.Collection("term").InsertOneAsync(term);
        return term;
    }

    private static BsonDocument GenericRef(string cls, string collection, ObjectId id) => new()
    {
        ["_cls"] = cls,
        ["_ref"] = new BsonDocument { ["$ref"] = collection, ["$id"] = id }
    };

    private async Task<BsonDocument> TeamAsync(ObjectId id) =>
        await services.Store.Collection("team").Find(new BsonDocument("_id", id)).FirstAsync();

    private Task<long> CountAsync(string collection, BsonDocument filter) =>
        services.Store.Collection(collection).CountDocumentsAsync(filter);

    private async Task<int?> DeleteOutcomeAsync(
        TeamDissolutionService service,
        BsonDocument actor,
        string teamId)
    {
        try
        {
            await service.DeleteAsync(actor, teamId);
            return null;
        }
        catch (ApiError error)
        {
            return error.Code;
        }
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            services.App.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response,
        int code)
    {
        using (response)
        {
            Assert.False(response.IsSuccessStatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("code").GetInt32());
            return body;
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string root;

        public Harness(LocalServices services, TeamLifecycleLockHooks? hooks = null)
        {
            root = Path.Combine(Path.GetTempPath(), "shoreline-k1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:Type"] = "Local",
                    ["Storage:Root"] = root,
                    ["Storage:Domain"] = "http://files.example.test",
                    ["Storage:FilePrefix"] = "files/",
                    ["Storage:CoverPrefix"] = "cover",
                    ["Storage:ResamplePrefix"] = "resample",
                    ["Storage:OutputPrefix"] = "outputs/"
                }).Build();
            Storage = new ObjectStorage(Configuration);
            Lifecycle = new TeamLifecycleLock(
                services.Store, TimeProvider.System, NullLogger<TeamLifecycleLock>.Instance)
            {
                Hooks = hooks
            };
            var labels = new ProjectLabelLock(
                services.Store, TimeProvider.System, NullLogger<ProjectLabelLock>.Instance);
            var context = new LabelRequestContext();
            Service = new TeamDissolutionService(
                services.Store,
                services.App.Services.GetRequiredService<IdentityPermissions>(),
                services.App.Services.GetRequiredService<TeamMemberService>(),
                Lifecycle,
                labels,
                context,
                Storage,
                Configuration,
                NullLogger<TeamDissolutionService>.Instance);
        }

        public IConfiguration Configuration { get; }
        public ObjectStorage Storage { get; }
        public TeamLifecycleLock Lifecycle { get; }
        public TeamDissolutionService Service { get; }
        public string Root => root;

        public async Task PutAsync(string key)
        {
            await using var stream = new MemoryStream("object"u8.ToArray());
            await Storage.PutAsync(key, stream, "application/octet-stream", default);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
