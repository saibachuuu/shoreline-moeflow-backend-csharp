using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class TeamInsightTests(LocalServices services)
{
    private static readonly ObjectId MissingUser = ObjectId.GenerateNewId();

    private TeamInsightService Insight(IServiceScope scope)
    {
        var provider = scope.ServiceProvider;
        return new TeamInsightService(
            services.Store,
            provider.GetRequiredService<IdentityPermissions>(),
            provider.GetRequiredService<ProjectService>(),
            provider.GetRequiredService<UserView>(),
            provider.GetRequiredService<ObjectStorage>(),
            provider.GetRequiredService<IConfiguration>());
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "zh-CN";
        return context;
    }

    private static Dictionary<string, object?> Object(object? value) => (Dictionary<string, object?>)value!;
    private static object[] Objects(object? value) => (object[])value!;

    // ---- collection fixtures -------------------------------------------------

    private async Task<BsonDocument> UserAsync(string name, string[]? aliases = null, bool admin = false,
        bool banned = false)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name + "-" + id,
            ["ns"] = SearchText.Normalize(name + "-" + id),
            ["ddn"] = name,
            ["s"] = "",
            ["a"] = "",
            ["l"] = "auto",
            ["e"] = name.Replace(" ", "-") + "-" + id + "@example.test",
            ["p"] = "password",
            ["admin"] = admin,
            ["b"] = banned,
            ["as"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)value)),
            ["asrch"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)SearchText.Normalize(value)))
        };
        await services.Store.Collection("user").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> TeamAsync(string name = "Team")
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name + "-" + ObjectId.GenerateNewId(),
            ["i"] = "",
            ["a"] = "",
            ["m_c"] = DateTime.UtcNow,
            ["m_e"] = DateTime.UtcNow
        };
        await services.Store.Collection("team").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> TeamMemberAsync(BsonDocument team, BsonDocument user, string baseTag = "member",
        string[]? tags = null, string status = "active", string[]? aliases = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray((tags ?? []).Select(value => (BsonValue)value)),
            ["aliases"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)value)),
            ["asrch"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)SearchText.Normalize(value))),
            ["worker_qualifications"] = new BsonArray(),
            ["status"] = status,
            ["version"] = 0
        };
        await services.Store.Collection("team_member").InsertOneAsync(document);
        return document;
    }

    private async Task PolicyAsync(BsonDocument team, string tag, params string[] permissions)
    {
        var definition = new BsonDocument
        {
            ["name"] = tag,
            ["code"] = tag,
            ["permissions"] = new BsonArray(permissions.Select(value => (BsonValue)value))
        };
        await services.Store.Collection("identity_tag_policy").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("team", team["_id"]),
            Builders<BsonDocument>.Update
                .Set("team_tags." + tag, definition)
                .SetOnInsert("team", team["_id"]),
            new UpdateOptions { IsUpsert = true });
    }

    private async Task<BsonDocument> SetAsync(BsonDocument team, string name = "默认")
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["i"] = "",
            ["t"] = team["_id"],
            ["d"] = true,
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow
        };
        await services.Store.Collection("project_set").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> LanguageAsync(string code = "ja")
    {
        // Trigger host startup so the shared language catalog is seeded, then reuse a
        // catalog document. Inserting here would inflate the public catalog count and
        // pollute LanguageTests, which shares this database fixture.
        using var scope = services.App.Services.CreateScope();
        var document = await services.Store.Collection("language")
            .Find(new BsonDocument())
            .FirstOrDefaultAsync();
        return document ?? throw new InvalidOperationException("seeded language catalog is empty");
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team, BsonDocument set, BsonDocument language,
        string name, BsonDocument? owner = null, int status = 0, ObjectId? storageSource = null,
        DateTime? edited = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["i"] = "",
            ["t"] = team["_id"],
            ["ps"] = set["_id"],
            ["ol"] = language["_id"],
            ["st"] = status,
            ["ou"] = owner?["_id"] ?? BsonNull.Value,
            ["m_c"] = DateTime.UtcNow,
            ["m_e"] = edited ?? DateTime.UtcNow
        };
        if (storageSource is not null) document["csid"] = storageSource.Value;
        await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> ProjectMemberAsync(BsonDocument project, BsonDocument? user, string[]? tags = null,
        string status = "active", string? externalId = null, string? displayName = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = user?["_id"] ?? BsonNull.Value,
            ["external_id"] = externalId is null ? BsonNull.Value : externalId,
            ["display_name"] = displayName ?? "",
            ["tags"] = new BsonArray((tags ?? []).Select(value => (BsonValue)value)),
            ["status"] = status,
            ["version"] = 0,
            ["create_time"] = DateTime.UtcNow,
            ["edit_time"] = DateTime.UtcNow
        };
        await services.Store.Collection("project_member").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> DanglingProjectMemberAsync(BsonDocument project, string displayName)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = MissingUser,
            ["external_id"] = BsonNull.Value,
            ["display_name"] = displayName,
            ["tags"] = new BsonArray(),
            ["status"] = "active",
            ["version"] = 0,
            ["create_time"] = DateTime.UtcNow,
            ["edit_time"] = DateTime.UtcNow
        };
        await services.Store.Collection("project_member").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> TargetAsync(BsonDocument project, BsonDocument language)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["l"] = language["_id"],
            ["tsc"] = 12,
            ["csc"] = 3,
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow,
            ["i"] = ""
        };
        await services.Store.Collection("target").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> OutputAsync(BsonDocument project, BsonDocument target, BsonDocument? user,
        int status = 4, string fileName = "result.zip")
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["t"] = target["_id"],
            ["u"] = user?["_id"] ?? BsonNull.Value,
            ["s"] = status,
            ["ty"] = 0,
            ["file_name"] = fileName,
            ["sb"] = 4096L,
            ["ct"] = DateTime.UtcNow,
            ["file_ids_include"] = new BsonArray { ObjectId.GenerateNewId().ToString() },
            ["file_ids_exclude"] = new BsonArray()
        };
        await services.Store.Collection("output").InsertOneAsync(document);
        return document;
    }

    // ---- user list -----------------------------------------------------------

    [Fact]
    public async Task CustomInsightRoleAllowsListsWithoutAdminTag()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var project = await ProjectAsync(team, set, language, "工作项目", actor);
        await ProjectMemberAsync(project, actor, ["translator"]);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListUsersAsync(actor, team, null, 0, 10, Context(), default);

        Assert.Equal(1, count);
        var row = Object(Assert.Single(rows));
        Assert.Equal(actor["_id"].ToString(), Object(row["user"])["id"]);
        var projects = Objects(row["projects"]);
        Assert.Single(projects);
        Assert.Equal("工作项目", Object(projects[0])["name"]);
        Assert.Single(Objects(Object(projects[0])["member_summary"]));
    }

    [Fact]
    public async Task SiteAdminWithoutTeamMembershipCannotViewInsight()
    {
        var team = await TeamAsync();
        var admin = await UserAsync("Site Admin", admin: true);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            insight.ListUsersAsync(admin, team, null, 0, 10, Context(), default));
        Assert.Equal("NoPermissionError", error.Error);
        Assert.Equal(103, error.Code);
        Assert.Equal("您没有权限查看本团队项目统计", error.Message);
    }

    [Fact]
    public async Task UserListSearchesMemberAliasesAndNormalizedUnicode()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var luna = await UserAsync("Straße", aliases: ["Luna"]);
        await TeamMemberAsync(team, luna, aliases: ["Luna"]);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListUsersAsync(actor, team, "LUNA", 0, 10, Context(), default);
        Assert.Equal(1, count);
        Assert.Equal(luna["_id"].ToString(), Object(Object(Assert.Single(rows))["user"])["id"]);

        (rows, count) = await insight.ListUsersAsync(actor, team, "strasse", 0, 10, Context(), default);
        Assert.Equal(1, count);
        Assert.Equal(luna["_id"].ToString(), Object(Object(Assert.Single(rows))["user"])["id"]);
    }

    [Fact]
    public async Task UserListCountsAllProjectsButReturnsAtMostFive()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        for (var index = 0; index < 7; index++)
        {
            var project = await ProjectAsync(team, set, language, "项目" + index, actor);
            await ProjectMemberAsync(project, actor, ["translator"]);
        }
        var finished = await ProjectAsync(team, set, language, "完成项目", actor, status: 1);
        await ProjectMemberAsync(finished, actor, ["translator"]);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListUsersAsync(actor, team, null, 0, 10, Context(), default);
        Assert.Equal(1, count);
        var row = Object(Assert.Single(rows));
        Assert.Equal(5, Objects(row["projects"]).Length);
        Assert.Equal(7L, row["count"]);
    }

    [Fact]
    public async Task UserListIncludesOnlyActiveUserBackedMembersAndExcludesDangling()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var removed = await UserAsync("Removed");
        await TeamMemberAsync(team, removed, status: "removed");
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = MissingUser,
            ["base_tag"] = "member",
            ["tags"] = new BsonArray(),
            ["status"] = "active"
        });

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListUsersAsync(actor, team, null, 0, 10, Context(), default);
        Assert.Equal(1, count);
        Assert.Equal(actor["_id"].ToString(), Object(Object(Assert.Single(rows))["user"])["id"]);
    }

    // ---- user projects -------------------------------------------------------

    [Fact]
    public async Task UserProjectsRequireActiveTeamMember()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var subject = await UserAsync("Subject");
        await TeamMemberAsync(team, subject, status: "removed");

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            insight.ListUserProjectsAsync(actor, team, subject, 0, 20, default));
        Assert.Equal("UserNotExistError", error.Error);
        Assert.Equal(1005, error.Code);
    }

    [Fact]
    public async Task UserProjectsReturnActiveMembershipsInTeamWorkingProjectsOnly()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var subject = await UserAsync("Subject");
        await TeamMemberAsync(team, subject);
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var otherTeam = await TeamAsync("Other");
        var otherSet = await SetAsync(otherTeam);
        var working = await ProjectAsync(team, set, language, "Working");
        var finished = await ProjectAsync(team, set, language, "Finished", status: 1);
        var removedProject = await ProjectAsync(team, set, language, "RemovedProject");
        var foreign = await ProjectAsync(otherTeam, otherSet, language, "Foreign");
        await ProjectMemberAsync(working, subject, ["translator"]);
        await ProjectMemberAsync(finished, subject, ["translator"]);
        await ProjectMemberAsync(foreign, subject, ["translator"]);
        await ProjectMemberAsync(removedProject, subject, ["translator"], status: "removed");
        var external = await ProjectMemberAsync(working, null, externalId: "e-1", displayName: "External");

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListUserProjectsAsync(actor, team, subject, 0, 20, default);
        Assert.Equal(1, count);
        var row = Object(Assert.Single(rows));
        Assert.Equal(working["_id"].ToString(), row["id"]);
        var summary = Objects(row["member_summary"]);
        Assert.Single(summary);
        Assert.Equal(subject["_id"].ToString(), Object(summary[0])["user_id"]);
        Assert.NotEqual(external["_id"].ToString(), Object(summary[0])["id"]);
    }

    [Fact]
    public async Task UserProjectsPaginationUsesNaturalOrderWithoutDuplicates()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var subject = await UserAsync("Subject");
        await TeamMemberAsync(team, subject);
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var projects = new List<BsonDocument>();
        for (var index = 0; index < 6; index++)
        {
            var project = await ProjectAsync(team, set, language, "P" + index);
            await ProjectMemberAsync(project, subject, ["translator"]);
            projects.Add(project);
        }

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (first, count) = await insight.ListUserProjectsAsync(actor, team, subject, 0, 2, default);
        var (second, _) = await insight.ListUserProjectsAsync(actor, team, subject, 2, 2, default);
        Assert.Equal(6, count);
        Assert.Equal(2, first.Length);
        Assert.Equal(2, second.Length);
        var ids = first.Concat(second).Select(row => (string)Object(row)["id"]!).ToArray();
        Assert.Equal(4, ids.Distinct().Count());
    }

    // ---- project list --------------------------------------------------------

    [Fact]
    public async Task ProjectListSearchIsLiteralCaseInsensitiveAndIgnoresFinished()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var literal = await ProjectAsync(team, set, language, "A.B");
        var wildcard = await ProjectAsync(team, set, language, "AxB");
        var finished = await ProjectAsync(team, set, language, "A.B finished", status: 1);
        _ = finished;

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListProjectsAsync(actor, team, "a.b", 0, 10, Context(), default);
        Assert.Equal(1, count);
        Assert.Equal(literal["_id"].ToString(), Object(Assert.Single(rows))["project"] is Dictionary<string, object?> mini
            ? mini["id"] : null);
        Assert.NotEqual(wildcard["_id"].ToString(), Object(Assert.Single(rows))["project"] is Dictionary<string, object?> m
            ? m["id"] : null);
    }

    [Fact]
    public async Task ProjectListExcludesFinishedAndCountsAllMatches()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        for (var index = 0; index < 7; index++)
            await ProjectAsync(team, set, language, "项目甲" + index);
        await ProjectAsync(team, set, language, "已完成", status: 1);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListProjectsAsync(actor, team, "项目甲", 0, 2, Context(), default);
        Assert.Equal(7, count);
        Assert.Equal(2, rows.Length);
    }

    // ---- project users -------------------------------------------------------

    [Fact]
    public async Task ProjectUsersIncludeExternalAndFullMemberShape()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var owner = await UserAsync("Owner");
        var project = await ProjectAsync(team, set, language, "P", owner);
        await ProjectMemberAsync(project, owner, ["creator"]);
        var regular = await UserAsync("Regular");
        await ProjectMemberAsync(project, regular, ["translator"]);
        await ProjectMemberAsync(project, null, externalId: "ext-1", displayName: "外部署名");
        var removed = await UserAsync("Removed");
        await ProjectMemberAsync(project, removed, status: "removed");
        await DanglingProjectMemberAsync(project, "已删除用户");

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListProjectUsersAsync(actor, team, project, 0, 20, default);
        Assert.Equal(4, count);
        var external = rows.Select(Object).Single(row => (string?)row["external_id"] == "ext-1");
        Assert.Null(external["user"]);
        Assert.Equal("外部署名", external["name"]);
        var regularRow = rows.Select(Object).Single(row => (string?)row["user_id"] == regular["_id"].ToString());
        Assert.Equal(regular["_id"].ToString(), Object(regularRow["user"])["id"]);
        Assert.Empty((string[])regularRow["effective_permissions"]!);
        var ownerRow = rows.Select(Object).Single(row => (string?)row["user_id"] == owner["_id"].ToString());
        Assert.True((bool)ownerRow["is_owner"]!);
    }

    [Fact]
    public async Task ProjectUsersRequireProjectToBelongToRequestedTeam()
    {
        var team = await TeamAsync();
        var other = await TeamAsync("Other");
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(other);
        var language = await LanguageAsync();
        var project = await ProjectAsync(other, set, language, "Foreign");

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            insight.ListProjectUsersAsync(actor, team, project, 0, 20, default));
        Assert.Equal("ProjectNotExistError", error.Error);
        Assert.Equal(4001, error.Code);
    }

    [Fact]
    public async Task ProjectUsersPaginationReportsTotalNotPageSize()
    {
        var team = await TeamAsync();
        var actor = await UserAsync("Analyst");
        await TeamMemberAsync(team, actor, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var project = await ProjectAsync(team, set, language, "P");
        for (var index = 0; index < 6; index++)
        {
            var user = await UserAsync("Member " + index);
            await ProjectMemberAsync(project, user, ["translator"]);
        }

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, count) = await insight.ListProjectUsersAsync(actor, team, project, 2, 2, default);
        Assert.Equal(6, count);
        Assert.Equal(2, rows.Length);
    }

    // ---- outputs -------------------------------------------------------------

    [Fact]
    public async Task OutputsOnlyAppearForAutoBecomeProjectAdmin()
    {
        var team = await TeamAsync();
        var admin = await UserAsync("Team Admin");
        await TeamMemberAsync(team, admin, baseTag: "admin");
        await PolicyAsync(team, "admin", "team:ACCESS", "team:INSIGHT", "team:AUTO_BECOME_PROJECT_ADMIN");
        var analyst = await UserAsync("Analyst");
        await TeamMemberAsync(team, analyst, tags: ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var project = await ProjectAsync(team, set, language, "P");
        var target = await TargetAsync(project, language);
        await OutputAsync(project, target, admin);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (adminRows, _) = await insight.ListProjectsAsync(admin, team, null, 0, 10, Context(), default);
        var adminItem = Object(Assert.Single(adminRows));
        Assert.True(adminItem.ContainsKey("outputs"));
        var output = Object(Assert.Single(Objects(adminItem["outputs"])));
        Assert.Equal(6, Objects(output["status_details"]).Length);
        var outputProject = JsonSerializer.Serialize(output["project"]);
        Assert.Contains("\"team\"", outputProject);
        Assert.Contains("\"project_set\"", outputProject);

        var (analystRows, _) = await insight.ListProjectsAsync(analyst, team, null, 0, 10, Context(), default);
        Assert.False(Object(Assert.Single(analystRows)).ContainsKey("outputs"));
    }

    [Fact]
    public async Task CompletedOutputLinkSignsDefaultStorageButFailsClosedForStoragePool()
    {
        var team = await TeamAsync();
        var admin = await UserAsync("Team Admin");
        await TeamMemberAsync(team, admin, baseTag: "admin");
        await PolicyAsync(team, "admin", "team:ACCESS", "team:INSIGHT", "team:AUTO_BECOME_PROJECT_ADMIN");
        var set = await SetAsync(team);
        var language = await LanguageAsync();
        var plain = await ProjectAsync(team, set, language, "Plain");
        var pooled = await ProjectAsync(team, set, language, "Pooled", storageSource: ObjectId.GenerateNewId());
        var plainTarget = await TargetAsync(plain, language);
        var pooledTarget = await TargetAsync(pooled, language);
        await OutputAsync(plain, plainTarget, admin);
        await OutputAsync(pooled, pooledTarget, admin);

        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var (rows, _) = await insight.ListProjectsAsync(admin, team, null, 0, 10, Context(), default);
        var plainItem = rows.Select(Object).Single(item =>
            (string)Object(item["project"]!)["id"]! == plain["_id"].ToString());
        var pooledItem = rows.Select(Object).Single(item =>
            (string)Object(item["project"]!)["id"]! == pooled["_id"].ToString());
        var plainOutput = Object(Assert.Single(Objects(plainItem["outputs"])));
        var pooledOutput = Object(Assert.Single(Objects(pooledItem["outputs"])));
        Assert.False(string.IsNullOrEmpty((string?)plainOutput["link"]));
        Assert.Null(pooledOutput["link"]);
        Assert.Equal(4096L, plainOutput["storage_bytes"]);
        Assert.Equal(12L, Convert.ToInt64(((Dictionary<string, object?>)plainOutput["target"]!)["translated_source_count"]));
    }

    [Fact]
    public async Task InvalidUserIdUsesLegacyUserNotExistError()
    {
        using var scope = services.App.Services.CreateScope();
        var insight = Insight(scope);
        var error = await Assert.ThrowsAsync<ApiError>(() => insight.GetUserAsync("not-an-id", default));
        Assert.Equal("UserNotExistError", error.Error);
        Assert.Equal(1005, error.Code);
    }
}
