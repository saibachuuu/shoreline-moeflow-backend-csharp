using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

// Direct service/integration coverage. The HTTP routes are deliberately not
// exercised here until Program.cs maps them in the wiring step.
[Collection("local-services")]
public sealed class PresenceTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task LegacyBsonRowsAreReadAndAvatarUsesTheExistingSigningConvention()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync(name: "legacy-name", displayName: "Legacy DDN", avatar: "legacy.png");
        var lastHeartbeat = new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc);
        await InsertPresenceAsync(project, team, user, "translating", lastHeartbeat,
            clock.GetUtcNow().UtcDateTime.AddSeconds(60));

        var active = Assert.Single(await service.ActiveUsersForProjectAsync(project, default));
        Assert.Equal(user["_id"].AsObjectId.ToString(), Assert.IsType<string>(active["id"]));
        Assert.Equal("Legacy DDN", Assert.IsType<string>(active["name"]));
        Assert.Equal("translating", Assert.IsType<string>(active["action"]));
        Assert.Equal("2026-01-02T03:04:05.123000", Assert.IsType<string>(active["last_heartbeat"]));
        var avatar = Assert.IsType<string>(active["avatar"]);
        Assert.Contains("legacy.png", avatar);
        Assert.DoesNotContain("local-disposable-test-secret", avatar);

        var stored = await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId);
        Assert.True(stored["project"].IsObjectId);
        Assert.True(stored["team"].IsObjectId);
        Assert.True(stored["user"].IsObjectId);
        Assert.True(stored["expire_at"].IsBsonDateTime);
    }

    [Fact]
    public async Task AvatarIsEmptyWhenNoAvatarAndDefaultIsMissingAndUsesConfiguredDefault()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        var noDefaultConfig = new ConfigurationBuilder().Build();
        var service = Service(clock, noDefaultConfig);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync(name: "no-avatar");
        await service.HeartbeatAsync(project, user, "working", default);

        var active = Assert.Single(await service.ActiveUsersForProjectAsync(project, default));
        Assert.Equal("", Assert.IsType<string>(active["avatar"]));

        var configuredDefault = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DefaultUserAvatar"] = "https://cdn.example.test/default-user.png"
            })
            .Build();
        var configuredService = Service(clock, configuredDefault);
        var configuredActive = Assert.Single(await configuredService.ActiveUsersForProjectAsync(project, default));
        Assert.Equal("https://cdn.example.test/default-user.png",
            Assert.IsType<string>(configuredActive["avatar"]));
    }

    [Fact]
    public async Task HeartbeatUpsertsOneRowAndRefreshesTeamActionHeartbeatAndExpiry()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var firstTeam = await TeamAsync();
        var secondTeam = await TeamAsync();
        var project = await ProjectAsync(firstTeam);
        var user = await UserAsync();

        await service.HeartbeatAsync(project, user, "first", default);
        clock.Now = clock.Now.AddSeconds(10);
        project["t"] = secondTeam["_id"];
        await service.HeartbeatAsync(project, user, "second", default);

        var rows = await ProjectRowsAsync(project["_id"].AsObjectId);
        var row = Assert.Single(rows);
        Assert.Equal(secondTeam["_id"], row["team"]);
        Assert.Equal("second", row["action"].AsString);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, row["last_heartbeat"].ToUniversalTime());
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(45), row["expire_at"].ToUniversalTime());
    }

    [Fact]
    public async Task QueryFilterEnforcesFortyFiveSecondExpiryWithoutRelyingOnTheTtlMonitor()
    {
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(start);
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync();

        await service.HeartbeatAsync(project, user, "working", default);
        var row = await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId);
        Assert.Equal(45,
            (row["expire_at"].ToUniversalTime() - row["last_heartbeat"].ToUniversalTime()).TotalSeconds);
        var active = Assert.Single(await service.ActiveUsersForProjectAsync(project, default));
        Assert.Equal("2026-02-01T00:00:00", Assert.IsType<string>(active["last_heartbeat"]));

        // Python uses expire_at__gt=now, so the row is inactive at the exact
        // boundary even if MongoDB's TTL monitor has not deleted it yet.
        clock.Now = start.AddSeconds(45);
        Assert.Empty(await service.ActiveUsersForProjectAsync(project, default));
        clock.Now = start.AddSeconds(46);
        Assert.Empty(await service.ActiveUsersForProjectAsync(project, default));

        await service.HeartbeatAsync(project, user, "working", default);
        Assert.Single(await service.ActiveUsersForProjectAsync(project, default));
    }

    [Fact]
    public async Task ProjectAndTeamPresenceAreIsolated()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var teamA = await TeamAsync();
        var teamB = await TeamAsync();
        var projectA1 = await ProjectAsync(teamA);
        var projectA2 = await ProjectAsync(teamA);
        var projectB = await ProjectAsync(teamB);
        var user1 = await UserAsync();
        var user2 = await UserAsync();

        await service.HeartbeatAsync(projectA1, user1, "a1", default);
        await service.HeartbeatAsync(projectA2, user2, "a2", default);
        await service.HeartbeatAsync(projectB, user2, "b", default);

        Assert.Equal(new[] { user1["_id"].AsObjectId.ToString() },
            (await service.ActiveUsersForProjectAsync(projectA1, default)).Select(u => (string)u["id"]!));
        Assert.Equal(new[] { user2["_id"].AsObjectId.ToString() },
            (await service.ActiveUsersForProjectAsync(projectA2, default)).Select(u => (string)u["id"]!));
        Assert.Equal(new[] { user2["_id"].AsObjectId.ToString() },
            (await service.ActiveUsersForProjectAsync(projectB, default)).Select(u => (string)u["id"]!));

        var aggregateA = await service.ActivePresenceForTeamAsync(teamA, default);
        Assert.Equal(2, aggregateA.Count);
        Assert.Contains(projectA1["_id"].AsObjectId.ToString(), aggregateA.Keys);
        Assert.Contains(projectA2["_id"].AsObjectId.ToString(), aggregateA.Keys);
        Assert.DoesNotContain(projectB["_id"].AsObjectId.ToString(), aggregateA.Keys);
        Assert.Equal(1, aggregateA[projectA1["_id"].AsObjectId.ToString()]["user_count"]);

        var aggregateB = await service.ActivePresenceForTeamAsync(teamB, default);
        Assert.Single(aggregateB);
        Assert.Contains(projectB["_id"].AsObjectId.ToString(), aggregateB.Keys);
    }

    [Fact]
    public async Task LeaveRemovesOnlyOwnRowAndIsIdempotent()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user1 = await UserAsync();
        var user2 = await UserAsync();

        await service.HeartbeatAsync(project, user1, "one", default);
        await service.HeartbeatAsync(project, user2, "two", default);

        await service.LeaveAsync(project, user1, default);
        await service.LeaveAsync(project, user1, default);
        var active = await service.ActiveUsersForProjectAsync(project, default);
        Assert.Equal(new[] { user2["_id"].AsObjectId.ToString() }, active.Select(u => (string)u["id"]!));
        Assert.Single(await ProjectRowsAsync(project["_id"].AsObjectId));

        await service.LeaveAsync(project, user2, default);
        await service.LeaveAsync(project, user2, default);
        Assert.Empty(await service.ActiveUsersForProjectAsync(project, default));
        Assert.Empty(await ProjectRowsAsync(project["_id"].AsObjectId));
    }

    [Fact]
    public async Task NamesPreferActiveMemberDisplayNameThenDdnThenName()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);

        var activeMember = await UserAsync(name: "active-name", displayName: "Active DDN");
        var removedMember = await UserAsync(name: "removed-name", displayName: "Removed DDN");
        var invitedMember = await UserAsync(name: "invited-name", displayName: "Invited DDN");
        var ddnOnly = await UserAsync(name: "ddn-name", displayName: "DDN Only");
        var nameOnly = await UserAsync(name: "Name Only");
        var emptyMember = await UserAsync(name: "empty-name", displayName: "Empty Member DDN");

        await MemberAsync(project, activeMember, "Active Member Alias", "active");
        await MemberAsync(project, removedMember, "Removed Member Alias", "removed");
        await MemberAsync(project, invitedMember, "Invited Member Alias", "invited");
        await MemberAsync(project, emptyMember, "", "active");

        foreach (var user in new[] { activeMember, removedMember, invitedMember, ddnOnly, nameOnly, emptyMember })
            await service.HeartbeatAsync(project, user, "working", default);

        var names = (await service.ActiveUsersForProjectAsync(project, default))
            .ToDictionary(u => (string)u["id"]!, u => (string)u["name"]!);
        Assert.Equal("Active Member Alias", names[activeMember["_id"].AsObjectId.ToString()]);
        Assert.Equal("Removed DDN", names[removedMember["_id"].AsObjectId.ToString()]);
        Assert.Equal("Invited DDN", names[invitedMember["_id"].AsObjectId.ToString()]);
        Assert.Equal("DDN Only", names[ddnOnly["_id"].AsObjectId.ToString()]);
        Assert.Equal("Name Only", names[nameOnly["_id"].AsObjectId.ToString()]);
        Assert.Equal("Empty Member DDN", names[emptyMember["_id"].AsObjectId.ToString()]);
    }

    [Fact]
    public async Task DanglingUserAndProjectReferencesAreSkipped()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync();
        await service.HeartbeatAsync(project, user, "valid", default);

        var missingUser = ObjectId.GenerateNewId();
        await InsertPresenceAsync(project, team, new BsonDocument("_id", missingUser), "ghost-user",
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddSeconds(60));
        var missingProject = ObjectId.GenerateNewId();
        await InsertPresenceAsync(new BsonDocument("_id", missingProject), team, user, "ghost-project",
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddSeconds(60));
        await InsertPresenceAsync(project, team, new BsonDocument("_id", "not-an-object-id"), "bad-user",
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddSeconds(60));
        await InsertPresenceAsync(new BsonDocument("_id", "not-an-object-id"), team, user, "bad-project",
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddSeconds(60));

        var active = Assert.Single(await service.ActiveUsersForProjectAsync(project, default));
        Assert.Equal(user["_id"].AsObjectId.ToString(), Assert.IsType<string>(active["id"]));

        var aggregate = await service.ActivePresenceForTeamAsync(team, default);
        var group = Assert.Single(aggregate).Value;
        Assert.Equal(project["_id"].AsObjectId.ToString(), Assert.IsType<string>(group["project_id"]));
        Assert.Equal(1, group["user_count"]);
        var users = Assert.IsType<List<Dictionary<string, object?>>>(group["users"]);
        Assert.Equal(user["_id"].AsObjectId.ToString(), Assert.IsType<string>(Assert.Single(users)["id"]));
    }

    [Fact]
    public async Task UnicodeActionIsTruncatedByCodePointAndFalsyActionsBecomeWorking()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync();

        var emoji = string.Concat(Enumerable.Repeat("😀", 70));
        await service.HeartbeatAsync(project, user, emoji, default);
        var action = (await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId))["action"].AsString;
        Assert.Equal(64, action.EnumerateRunes().Count());
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 64)), action);

        await service.HeartbeatAsync(project, user, new string('x', 65), default);
        Assert.Equal(new string('x', 64),
            (await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId))["action"].AsString);

        await service.HeartbeatAsync(project, user, "", default);
        Assert.Equal("working",
            (await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId))["action"].AsString);
        await service.HeartbeatAsync(project, user, null, default);
        Assert.Equal("working",
            (await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId))["action"].AsString);

        // Python keeps truthy whitespace; it does not trim the action.
        await service.HeartbeatAsync(project, user, "   ", default);
        Assert.Equal("   ",
            (await RowAsync(project["_id"].AsObjectId, user["_id"].AsObjectId))["action"].AsString);
    }

    [Fact]
    public async Task TeamAggregateHasPythonShapeAndEmptyResultsUseObjectAndArray()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var service = Service(clock);
        await service.InitializeAsync(default);
        var team = await TeamAsync();
        var project1 = await ProjectAsync(team);
        var project2 = await ProjectAsync(team);
        var user1 = await UserAsync();
        var user2 = await UserAsync();
        var user3 = await UserAsync();

        await service.HeartbeatAsync(project1, user1, "one", default);
        await service.HeartbeatAsync(project1, user2, "two", default);
        await service.HeartbeatAsync(project2, user3, "three", default);

        var aggregate = await service.ActivePresenceForTeamAsync(team, default);
        Assert.Equal(2, aggregate.Count);
        var first = aggregate[project1["_id"].AsObjectId.ToString()];
        Assert.Equal(project1["_id"].AsObjectId.ToString(), Assert.IsType<string>(first["project_id"]));
        Assert.Equal(2, first["user_count"]);
        var firstUsers = Assert.IsType<List<Dictionary<string, object?>>>(first["users"]);
        Assert.Equal(2, firstUsers.Count);
        foreach (var entry in firstUsers)
            Assert.Equal(new[] { "action", "avatar", "id", "last_heartbeat", "name" },
                entry.Keys.OrderBy(key => key, StringComparer.Ordinal));

        var second = aggregate[project2["_id"].AsObjectId.ToString()];
        Assert.Equal(1, second["user_count"]);
        Assert.Single(Assert.IsType<List<Dictionary<string, object?>>>(second["users"]));

        var emptyTeam = await TeamAsync();
        Assert.Empty(await service.ActivePresenceForTeamAsync(emptyTeam, default));
        var emptyProject = await ProjectAsync(team);
        Assert.Empty(await service.ActiveUsersForProjectAsync(emptyProject, default));
    }

    [Fact]
    public async Task IndexesMatchPythonNamesTtlAndUniqueProjectUserConstraint()
    {
        var service = Service(new TestClock(DateTimeOffset.UtcNow));
        await service.InitializeAsync(default);
        using var cursor = await services.Store.Collection("project_presence").Indexes.ListAsync(default);
        var listed = new List<BsonDocument>();
        while (await cursor.MoveNextAsync(default))
            listed.AddRange(cursor.Current);
        var indexes = listed.ToDictionary(index => index["name"].AsString);

        Assert.Contains("project_1", indexes.Keys);
        Assert.Contains("team_1", indexes.Keys);
        Assert.Contains("user_1", indexes.Keys);
        Assert.Contains("team_1_expire_at_1", indexes.Keys);
        Assert.Contains("project_1_expire_at_1", indexes.Keys);
        Assert.Equal(0, indexes["expire_at_1"]["expireAfterSeconds"].ToInt32());
        Assert.True(indexes["project_1_user_1"]["unique"].AsBoolean);
        var keys = indexes["project_1_user_1"]["key"].AsBsonDocument;
        Assert.Equal(1, keys["project"].ToInt32());
        Assert.Equal(1, keys["user"].ToInt32());
    }

    private PresenceService Service(TestClock clock, IConfiguration? configuration = null) =>
        new(services.Store, configuration ?? services.Configuration,
            services.App.Services.GetRequiredService<ObjectStorage>(), clock);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<BsonDocument> TeamAsync()
    {
        var id = ObjectId.GenerateNewId();
        var team = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "team-" + id
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team)
    {
        var id = ObjectId.GenerateNewId();
        var project = new BsonDocument
        {
            ["_id"] = id,
            ["t"] = team["_id"],
            ["n"] = "project-" + id
        };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> UserAsync(string? name = null, string? displayName = null, string? avatar = null)
    {
        var id = ObjectId.GenerateNewId();
        var userName = name ?? "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = userName,
            ["ns"] = userName,
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = id + "@example.test",
            ["p"] = Password,
            ["admin"] = false,
            ["b"] = false
        };
        if (displayName is not null) user["ddn"] = displayName;
        if (avatar is not null) user["a"] = avatar;
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private Task MemberAsync(BsonDocument project, BsonDocument user, string displayName, string status) =>
        services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project["_id"],
            ["user"] = user["_id"],
            ["display_name"] = displayName,
            ["status"] = status,
            ["tags"] = new BsonArray(),
            ["version"] = 0
        });

    private Task InsertPresenceAsync(BsonDocument project, BsonDocument team, BsonDocument user,
        string action, DateTime lastHeartbeat, DateTime expireAt) =>
        services.Store.Collection("project_presence").InsertOneAsync(new BsonDocument
        {
            ["project"] = project["_id"],
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["action"] = action,
            ["last_heartbeat"] = lastHeartbeat,
            ["expire_at"] = expireAt
        });

    private Task<List<BsonDocument>> ProjectRowsAsync(ObjectId projectId) =>
        services.Store.Collection("project_presence")
            .Find(new BsonDocument("project", projectId)).ToListAsync();

    private Task<BsonDocument> RowAsync(ObjectId projectId, ObjectId userId) =>
        services.Store.Collection("project_presence").Find(new BsonDocument
        {
            ["project"] = projectId,
            ["user"] = userId
        }).FirstAsync();
}
