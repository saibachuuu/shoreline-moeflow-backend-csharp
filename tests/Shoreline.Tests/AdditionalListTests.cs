using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class AdditionalListTests(LocalServices services)
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task StatusFilterSplitsCommasRepeatsAndDropsEmptyTokens()
    {
        var world = await WorldAsync();
        var normal = await ProjectAsync(world, "normal", 0, BaseTime.AddMinutes(1));
        var cleared = await ProjectAsync(world, "cleared", 1, BaseTime.AddMinutes(2));
        var completed = await ProjectAsync(world, "completed", 5, BaseTime.AddMinutes(3));
        var planning = await ProjectAsync(world, "planning", 2, BaseTime.AddMinutes(4));
        var deleting = await ProjectAsync(world, "deleting", 3, BaseTime.AddMinutes(5));

        var page = await Lists.ListAsync(world.Actor, ["NORMAL,CLEARED", "", " COMPLETED "], "",
            new Pagination(1, 100), default);

        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { cleared, completed, normal }.Select(p => p["_id"].ToString()!)
            .Order(StringComparer.Ordinal), Ids(page));
        Assert.DoesNotContain(planning["_id"].ToString()!, Ids(page));
        Assert.DoesNotContain(deleting["_id"].ToString()!, Ids(page));
    }

    [Fact]
    public async Task CompletedStatusIncludesOnlyCompletedAndCleared()
    {
        var world = await WorldAsync();
        var normal = await ProjectAsync(world, "normal", 0);
        var cleared = await ProjectAsync(world, "cleared", 1);
        var planning = await ProjectAsync(world, "planning", 2);
        var deleting = await ProjectAsync(world, "deleting", 3);
        var completed = await ProjectAsync(world, "completed", 5);

        var page = await Lists.ListAsync(world.Actor, ["COMPLETED"], "", new Pagination(1, 100), default);

        Assert.Equal(new[] { cleared, completed }.Select(p => p["_id"].ToString()!)
            .Order(StringComparer.Ordinal), Ids(page));
        Assert.DoesNotContain(normal["_id"].ToString()!, Ids(page));
        Assert.DoesNotContain(planning["_id"].ToString()!, Ids(page));
        Assert.DoesNotContain(deleting["_id"].ToString()!, Ids(page));
    }

    [Fact]
    public async Task InvalidStatusIsCaseSensitiveAndMatchesLegacyIdentityError()
    {
        var world = await WorldAsync();
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            Lists.ListAsync(world.Actor, ["normal"], "", new Pagination(1, 20), default));
        Assert.Equal("InvalidIdentityRequestError", error.Error);
        Assert.Equal(5100, error.Code);
        Assert.Equal(400, error.Status);
        Assert.Equal("INVALID_REQUEST", error.IdentityCode);
        Assert.Equal("身份标签请求参数错误: invalid project status", error.Message);
    }

    [Fact]
    public async Task OnlyDirectActiveMembershipsAreListed()
    {
        var world = await WorldAsync();
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["team"] = world.Team["_id"], ["user"] = world.Actor["_id"],
            ["base_tag"] = "admin", ["status"] = "active"
        });
        var active = await ProjectAsync(world, "active", 0, join: false);
        var removed = await ProjectAsync(world, "removed", 0, join: false);
        var invited = await ProjectAsync(world, "invited", 0, join: false);
        var noMembership = await ProjectAsync(world, "team admin only", 0, join: false);
        var otherUser = await ProjectAsync(world, "other user", 0, join: false);
        await MemberAsync(world, active, "Active", "active");
        await MemberAsync(world, removed, "Removed", "removed");
        await MemberAsync(world, invited, "Invited", "invited");
        await MemberAsync(world, otherUser, "Someone else", "active", ObjectId.GenerateNewId());

        var page = await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 100), default);

        Assert.Equal(active["_id"].ToString(), Assert.Single(Ids(page)));
        Assert.NotEqual(noMembership["_id"].ToString(), active["_id"].ToString());
    }

    [Fact]
    public async Task WordMatchesNormalizedNameLiterallyAndCaseInsensitively()
    {
        var world = await WorldAsync();
        var cpp = await ProjectAsync(world, "C++ [测试]", 0, search: "C++ [测试]");
        var alpha = await ProjectAsync(world, "Alpha", 0);
        var star = await ProjectAsync(world, "Dot Star", 0, search: ".*");

        Assert.Equal(cpp["_id"].ToString(), Assert.Single(Ids(await Lists.ListAsync(world.Actor, [], "测试",
            new Pagination(1, 100), default))));
        Assert.Equal(cpp["_id"].ToString(), Assert.Single(Ids(await Lists.ListAsync(world.Actor, [], "[测试]",
            new Pagination(1, 100), default))));
        Assert.Equal(cpp["_id"].ToString(), Assert.Single(Ids(await Lists.ListAsync(world.Actor, [], "C++",
            new Pagination(1, 100), default))));
        Assert.Equal(star["_id"].ToString(), Assert.Single(Ids(await Lists.ListAsync(world.Actor, [], ".*",
            new Pagination(1, 100), default))));
        Assert.Empty(Ids(await Lists.ListAsync(world.Actor, [], "alpha-missing",
            new Pagination(1, 100), default)));
        Assert.DoesNotContain(alpha["_id"].ToString()!, Ids(await Lists.ListAsync(world.Actor, [], "C++",
            new Pagination(1, 100), default)));
    }

    [Fact]
    public async Task WordDoesNotSearchIntroOrAliases()
    {
        var world = await WorldAsync();
        var project = await ProjectAsync(world, "Plain", 0);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument
            {
                ["i"] = "secret intro marker",
                ["alias"] = "secret alias marker",
                ["aliases"] = new BsonArray { "secret alias marker" }
            }));

        Assert.Empty(Ids(await Lists.ListAsync(world.Actor, [], "secret intro marker",
            new Pagination(1, 100), default)));
        Assert.Empty(Ids(await Lists.ListAsync(world.Actor, [], "secret alias marker",
            new Pagination(1, 100), default)));
    }

    [Fact]
    public async Task PaginationUsesEditTimeDescendingAndReportsTotal()
    {
        var world = await WorldAsync();
        var projects = new List<BsonDocument>();
        for (var i = 0; i < 5; i++)
        {
            var project = await ProjectAsync(world, "p" + i, 0, BaseTime.AddMinutes(i), join: false);
            await MemberAsync(world, project, "M" + i);
            projects.Add(project);
        }

        var first = await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 2), default);
        Assert.Equal(5, first.Total);
        Assert.Equal(new[] { projects[4], projects[3] }.Select(p => p["_id"].ToString()!)
            .Order(StringComparer.Ordinal), Ids(first));

        var second = await Lists.ListAsync(world.Actor, [], "", new Pagination(2, 2), default);
        Assert.Equal(5, second.Total);
        Assert.Equal(new[] { projects[2], projects[1] }.Select(p => p["_id"].ToString()!)
            .Order(StringComparer.Ordinal), Ids(second));

        var third = await Lists.ListAsync(world.Actor, [], "", new Pagination(3, 2), default);
        Assert.Equal(new[] { projects[0]["_id"].ToString()! }, Ids(third));
    }

    [Fact]
    public async Task SortUsesProjectEditTimeMEvenWhenLegacyEtDiffersOrIsMissing()
    {
        var world = await WorldAsync();
        var newest = await ProjectAsync(world, "m_e newest", 0, BaseTime.AddMinutes(3));
        var middleNoEt = await ProjectAsync(world, "m_e middle no et", 0, BaseTime.AddMinutes(2));
        var oldest = await ProjectAsync(world, "m_e oldest", 0, BaseTime.AddMinutes(1));
        // ProjectSet owns `et`; Project inherits GroupMixin.edit_time with db_field `m_e`.
        // Deliberately write reversed legacy-looking `et` values to catch the wrong field.
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", newest["_id"]),
            new BsonDocument("$set", new BsonDocument("et", new BsonDateTime(BaseTime.AddMinutes(1)))));
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", oldest["_id"]),
            new BsonDocument("$set", new BsonDocument("et", new BsonDateTime(BaseTime.AddMinutes(3)))));

        var first = await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 2), default);
        Assert.Equal(3, first.Total);
        Assert.Equal(new[] { newest["_id"].ToString()!, middleNoEt["_id"].ToString()! }, OrderedIds(first));

        var second = await Lists.ListAsync(world.Actor, [], "", new Pagination(2, 2), default);
        Assert.Equal(3, second.Total);
        Assert.Equal(new[] { oldest["_id"].ToString()! }, OrderedIds(second));
    }

    [Fact]
    public async Task CardAndCompactMemberSummaryMatchLegacyShape()
    {
        var world = await WorldAsync();
        var project = await ProjectAsync(world, "Shape", 1, BaseTime, search: "Shape", join: false);
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument
            {
                ["sc"] = 3, ["tc"] = 4, ["tsc"] = 5, ["csc"] = 6, ["fs"] = 7L
            }));
        await MemberAsync(world, project, "Zed", "active", null, "translator");
        await MemberAsync(world, project, "Amy", "invited", ObjectId.GenerateNewId());
        await MemberAsync(world, project, "Old", "removed", ObjectId.GenerateNewId());

        var card = Assert.Single(Cards(await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 100), default)));
        Assert.Equal(new[]
        {
            "group_type", "id", "name", "status", "effective_permissions", "source_count", "target_count",
            "translated_source_count", "checked_source_count", "file_size", "team", "project_set", "member_summary"
        }.Order(StringComparer.Ordinal), card.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("project", card.GetProperty("group_type").GetString());
        Assert.Equal(project["_id"].ToString(), card.GetProperty("id").GetString());
        Assert.Equal(1, card.GetProperty("status").GetInt32());
        Assert.Equal(3, card.GetProperty("source_count").GetInt32());
        Assert.Equal(4, card.GetProperty("target_count").GetInt32());
        Assert.Equal(5, card.GetProperty("translated_source_count").GetInt32());
        Assert.Equal(6, card.GetProperty("checked_source_count").GetInt32());
        Assert.Equal(7, card.GetProperty("file_size").GetInt64());

        var team = card.GetProperty("team");
        Assert.Equal(new[] { "group_type", "id", "name", "worker_qualification_mode" }.Order(StringComparer.Ordinal),
            team.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("team", team.GetProperty("group_type").GetString());
        Assert.Equal(world.Team["_id"].ToString(), team.GetProperty("id").GetString());
        Assert.Equal("unqualified", team.GetProperty("worker_qualification_mode").GetString());

        var set = card.GetProperty("project_set");
        Assert.Equal(new[] { "id", "name", "default" }.Order(StringComparer.Ordinal),
            set.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(world.Set["_id"].ToString(), set.GetProperty("id").GetString());
        Assert.True(set.GetProperty("default").GetBoolean());

        var summaries = card.GetProperty("member_summary").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Amy", "Zed" }, summaries.Select(item => item.GetProperty("display_name").GetString()));
        Assert.Equal(new[] { "invited", "active" }, summaries.Select(item => item.GetProperty("status").GetString()));
        foreach (var summary in summaries)
            Assert.Equal(new[] { "id", "display_name", "tags", "status" }.Order(StringComparer.Ordinal),
                summary.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("translator", Assert.Single(summaries[1].GetProperty("tags").EnumerateArray()).GetString());
        Assert.Empty(summaries[0].GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task MemberSummariesAreBatchedAndDoNotLeakAcrossProjects()
    {
        var world = await WorldAsync();
        var left = await ProjectAsync(world, "Left", 0, BaseTime.AddMinutes(2), join: false);
        var right = await ProjectAsync(world, "Right", 0, BaseTime.AddMinutes(1), join: false);
        await MemberAsync(world, left, "Left Member", "active");
        await MemberAsync(world, right, "Right Member", "active");
        await MemberAsync(world, right, "Right Invited", "invited", ObjectId.GenerateNewId());

        var cards = Cards(await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 100), default));

        var leftCard = Card(cards, left);
        var rightCard = Card(cards, right);
        Assert.Equal(new[] { "Left Member" }, leftCard.GetProperty("member_summary").EnumerateArray()
            .Select(item => item.GetProperty("display_name").GetString()));
        Assert.Equal(new[] { "Right Invited", "Right Member" }, rightCard.GetProperty("member_summary").EnumerateArray()
            .Select(item => item.GetProperty("display_name").GetString()));
    }

    [Fact]
    public async Task EffectivePermissionsAreTheSortedLegacyIntersection()
    {
        var world = await WorldAsync();
        var project = await ProjectAsync(world, "Permissions", 0, join: false);
        await services.Store.Collection("identity_tag_policy").InsertOneAsync(new BsonDocument
        {
            ["team"] = world.Team["_id"],
            ["project_tags"] = new BsonDocument("custom", new BsonDocument("permissions",
                new BsonArray { "project:MANAGE_MEMBERS", "project:COMPLETE_PROJECT", "project:ADD_TRA" }))
        });
        await MemberAsync(world, project, "Custom", "active", null, "custom");

        var card = Assert.Single(Cards(await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 100), default)));
        Assert.Equal(new[] { "project:ACCESS", "project:COMPLETE_PROJECT", "project:MANAGE_MEMBERS" },
            card.GetProperty("effective_permissions").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task EmptyMembershipReturnsEmptyPageWithZeroTotal()
    {
        var world = await WorldAsync();
        var page = await Lists.ListAsync(world.Actor, [], "", new Pagination(1, 20), default);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task AdminVCodesExcludeCaptchaAndCapAtOneHundred()
    {
        await services.Store.Collection("v_code").DeleteManyAsync(new BsonDocument());
        var documents = new List<BsonDocument>();
        for (var i = 0; i < 105; i++)
            documents.Add(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["c"] = "code-" + i, ["t"] = 2, ["i"] = "verify:" + i,
                ["e"] = new BsonDateTime(BaseTime.AddDays(1)), ["w"] = 0,
                ["s"] = new BsonDateTime(BaseTime.AddSeconds(i))
            });
        for (var i = 0; i < 3; i++)
            documents.Add(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["c"] = "captcha-" + i, ["t"] = 1, ["i"] = "captcha",
                ["e"] = new BsonDateTime(BaseTime.AddDays(1)), ["w"] = 0,
                ["s"] = new BsonDateTime(BaseTime.AddDays(10).AddSeconds(i))
            });
        await services.Store.Collection("v_code").InsertManyAsync(documents);

        var result = JsonSerializer.SerializeToElement(await new AdminListService(services.Store).VCodesAsync(default));

        Assert.Equal(100, result.GetArrayLength());
        Assert.DoesNotContain(result.EnumerateArray(), item => item.GetProperty("intro").GetString() == "人机验证码");
        var times = result.EnumerateArray().Select(item => item.GetProperty("send_time").GetString()).ToArray();
        Assert.Equal(times.OrderDescending(StringComparer.Ordinal), times);
        Assert.Equal("2026-01-01T00:01:44", times[0]);
    }

    [Fact]
    public async Task AdminVCodesUseLegacyIntrosIsoDatesAndSafeUnknownFallback()
    {
        await services.Store.Collection("v_code").DeleteManyAsync(new BsonDocument());
        var emailId = ObjectId.GenerateNewId();
        var resetId = ObjectId.GenerateNewId();
        var unknownId = ObjectId.GenerateNewId();
        await services.Store.Collection("v_code").InsertManyAsync(new[]
        {
            new BsonDocument
            {
                ["_id"] = emailId, ["c"] = "mail-code", ["t"] = 2, ["i"] = "user@example.test",
                ["e"] = new BsonDateTime(new DateTime(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc)), ["w"] = 3,
                ["s"] = new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc))
            },
            new BsonDocument
            {
                ["_id"] = resetId, ["c"] = "reset-code", ["t"] = 4, ["i"] = "+10000000000",
                ["e"] = new BsonDateTime(new DateTime(2026, 1, 4, 4, 5, 6, DateTimeKind.Utc)), ["w"] = 1,
                ["s"] = new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc))
            },
            new BsonDocument
            {
                ["_id"] = unknownId, ["c"] = "future-code", ["t"] = 99, ["i"] = "future",
                ["e"] = new BsonDateTime(new DateTime(2026, 1, 5, 4, 5, 6, DateTimeKind.Utc)),
                ["s"] = new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc))
            }
        });

        var result = JsonSerializer.SerializeToElement(await new AdminListService(services.Store).VCodesAsync(default));

        var email = result.EnumerateArray().Single(item => item.GetProperty("id").GetString() == emailId.ToString());
        Assert.Equal("mail-code", email.GetProperty("content").GetString());
        Assert.Equal("验证邮箱", email.GetProperty("intro").GetString());
        Assert.Equal("user@example.test", email.GetProperty("info").GetString());
        Assert.Equal("2026-01-02T03:04:05.123000", email.GetProperty("send_time").GetString());
        Assert.Equal("2026-01-03T04:05:06", email.GetProperty("expires").GetString());
        Assert.Equal(3, email.GetProperty("wrong_count").GetInt32());

        var reset = result.EnumerateArray().Single(item => item.GetProperty("id").GetString() == resetId.ToString());
        Assert.Equal("重置密码", reset.GetProperty("intro").GetString());
        Assert.Equal("2026-01-02T03:04:06", reset.GetProperty("send_time").GetString());

        var unknown = result.EnumerateArray().Single(item => item.GetProperty("id").GetString() == unknownId.ToString());
        Assert.Equal("未知类型", unknown.GetProperty("intro").GetString());
        Assert.Equal(0, unknown.GetProperty("wrong_count").GetInt32());
    }

    [Fact]
    public void PythonIsoFormattingKeepsSixDigitsOnlyWhenMicrosecondsExist()
    {
        Assert.Equal("2026-01-02T03:04:05",
            AdminListService.IsoTimestamp(new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))));
        Assert.Equal("2026-01-02T03:04:05.120000",
            AdminListService.IsoTimestamp(new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 5, 120, DateTimeKind.Utc))));
        Assert.Null(AdminListService.IsoTimestamp(BsonNull.Value));
    }

    private UserProjectListService Lists => new(services.Store,
        services.App.Services.GetRequiredService<IdentityPermissions>(),
        services.App.Services.GetRequiredService<ProjectService>());

    private async Task<World> WorldAsync()
    {
        var actorId = ObjectId.GenerateNewId();
        var actor = new BsonDocument
        {
            ["_id"] = actorId, ["n"] = "List Actor " + actorId, ["e"] = actorId + "@example.test"
        };
        var team = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = "List Team " + actorId, ["wqm"] = "unqualified"
        };
        var set = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = "List Set " + actorId, ["d"] = true
        };
        await services.Store.Collection("user").InsertOneAsync(actor);
        await services.Store.Collection("team").InsertOneAsync(team);
        await services.Store.Collection("project_set").InsertOneAsync(set);
        return new World(actor, team, set);
    }

    private async Task<BsonDocument> ProjectAsync(World world, string name, int status, DateTime? editTime = null,
        string? search = null, bool join = true)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = name, ["ns"] = SearchText.Normalize(search ?? name),
            ["st"] = status, ["t"] = world.Team["_id"], ["ps"] = world.Set["_id"],
            ["m_e"] = new BsonDateTime(editTime ?? BaseTime), ["sc"] = 0, ["tc"] = 0, ["tsc"] = 0, ["csc"] = 0,
            ["fs"] = 0L
        };
        await services.Store.Collection("project").InsertOneAsync(document);
        if (join) await MemberAsync(world, document, world.Actor["n"].AsString);
        return document;
    }

    private async Task MemberAsync(World world, BsonDocument project, string displayName, string status = "active",
        ObjectId? userId = null, params string[] tags)
    {
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project["_id"],
            ["user"] = userId ?? world.Actor["_id"], ["display_name"] = displayName,
            ["status"] = status, ["tags"] = new BsonArray(tags)
        });
    }

    private static string[] OrderedIds(UserProjectListPage page) =>
        Cards(page).Select(card => card.GetProperty("id").GetString()!).ToArray();

    private static string[] Ids(UserProjectListPage page) =>
        OrderedIds(page).Order(StringComparer.Ordinal).ToArray();

    private static JsonElement[] Cards(UserProjectListPage page) =>
        JsonSerializer.SerializeToElement(page.Items).EnumerateArray().ToArray();

    private static JsonElement Card(JsonElement[] cards, BsonDocument project) =>
        cards.Single(card => card.GetProperty("id").GetString() == project["_id"].ToString());

    private sealed record World(BsonDocument Actor, BsonDocument Team, BsonDocument Set);
}
