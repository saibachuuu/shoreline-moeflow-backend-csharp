using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class IdentityAssignmentTests(LocalServices services)
{
    private IdentityPermissions Permissions => services.App.Services.GetRequiredService<IdentityPermissions>();
    private static JsonElement Tags(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Theory]
    [InlineData("null", "")]
    [InlineData("[\" translator \",\"proofreader\",\"translator\",\"\"]", "proofreader,translator")]
    public void NormalizationRemovesWhitespaceAndDuplicates(string json, string expected) =>
        Assert.Equal(expected, string.Join(',', IdentityPermissions.NormalizeTags(Tags(json))));

    [Theory]
    [InlineData("{}")]
    [InlineData("\"translator\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    public void InvalidTagShapeHasStableIdentityError(string json)
    {
        var error = Assert.Throws<ApiError>(() => IdentityPermissions.NormalizeTags(Tags(json)));
        Assert.Equal(5100, error.Code);
        Assert.Equal("INVALID_REQUEST", error.IdentityCode);
    }

    [Fact]
    public async Task OrdinaryMemberMayPositionSelfOnlyWithActiveQualification()
    {
        var (actor, team, project) = await SetupAsync();
        var target = new BsonDocument("_id", ObjectId.GenerateNewId());
        Assert.Empty(await Permissions.AssignableProjectTagsAsync(actor, project, target));
        await ExpectError(5102, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"translator\"]")));
        await services.Store.Collection("team_member").UpdateOneAsync(RelationFilter(actor, team),
            new BsonDocument("$set", new BsonDocument("worker_qualifications", new BsonArray { "translator" })));
        Assert.Equal(new[] { "translator" }, await Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"translator\"]")));
        await services.Store.Collection("team_member").UpdateOneAsync(RelationFilter(actor, team),
            new BsonDocument("$set", new BsonDocument("status", "removed")));
        await ExpectError(5102, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"translator\"]")));
    }

    [Fact]
    public async Task OpenModeAllowsOtherMembersButNeverNonmembersOrAdminEscalation()
    {
        var (actor, team, project) = await SetupAsync(open: true);
        var target = new BsonDocument("_id", ObjectId.GenerateNewId());
        await services.Store.Collection("team_member").InsertOneAsync(Relation(target, team));
        Assert.Equal(new[] { "translator" }, await Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"translator\"]")));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"admin\"]")));
        await ExpectError(5116, Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"creator\"]")));
        await services.Store.Collection("team_member").UpdateOneAsync(RelationFilter(target, team),
            new BsonDocument("$set", new BsonDocument("status", "removed")));
        await ExpectError(5102, Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"translator\"]")));
    }

    [Fact]
    public async Task ProjectManagerCanPositionSelfWithoutTeamQualificationButNotOthers()
    {
        var (actor, _, project) = await SetupAsync();
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument {
            ["project"] = project["_id"], ["user"] = actor["_id"], ["status"] = "active", ["tags"] = new BsonArray { "admin" } });
        Assert.Equal(new[] { "translator" }, await Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"translator\"]")));
        var target = new BsonDocument("_id", ObjectId.GenerateNewId());
        await ExpectError(5102, Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"translator\"]")));
        Assert.Equal(new[] { "admin" }, await Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"admin\"]")));
        Assert.Equal(new[] { "creator" }, await Permissions.ValidateProjectTagsAsync(actor, project, target, Tags("[\"creator\"]"), allowCreator: true));
    }

    [Fact]
    public async Task CustomPolicyIsReadLiveAndNonassignableWorkerOverridesAreEnforced()
    {
        var (actor, team, project) = await SetupAsync("admin");
        var policy = new BsonDocument { ["team"] = team["_id"], ["project_tags"] = new BsonDocument {
            ["reviewer"] = new BsonDocument("assignable", true),
            ["translator"] = new BsonDocument("assignable", false),
            ["blocked"] = new BsonDocument("assignable", false) },
            ["team_tags"] = new BsonDocument { ["editor"] = new BsonDocument("assignable", true),
                ["protected"] = new BsonDocument("assignable", false) } };
        await services.Store.Collection("identity_tag_policy").InsertOneAsync(policy);
        Assert.Equal(new[] { "reviewer" }, await Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"reviewer\"]")));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"translator\"]")));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"blocked\"]")));
        Assert.Equal(new[] { "editor" }, await Permissions.ValidateTeamTagsAsync(team, Tags("[\"editor\"]")));
        await ExpectError(5116, Permissions.ValidateTeamTagsAsync(team, Tags("[\"protected\"]")));
        await ExpectError(5101, Permissions.ValidateTeamTagsAsync(team, Tags("[\"admin\"]")));
        await services.Store.Collection("identity_tag_policy").UpdateOneAsync(new BsonDocument("team", team["_id"]),
            new BsonDocument("$set", new BsonDocument("project_tags.reviewer.assignable", false)));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"reviewer\"]")));
    }

    [Fact]
    public async Task ExternalInvitationsAcceptOnlyBuiltinWorkerTagsWithoutTeamQualification()
    {
        var (actor, _, project) = await SetupAsync();
        Assert.Equal(new[] { "proofreader", "translator" }, await Permissions.ValidateProjectTagsAsync(actor, project, null,
            Tags("[\"translator\",\"proofreader\"]"), external: true));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, null, Tags("[\"admin\"]"), external: true));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, null, Tags("[]")));
        await ExpectError(5101, Permissions.ValidateProjectTagsAsync(actor, project, actor, Tags("[\"unknown\"]")));
    }

    private static async Task ExpectError(int code, Task<string[]> task) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<ApiError>(() => task)).Code);

    private static BsonDocument RelationFilter(BsonDocument user, BsonDocument team) => new() { ["team"] = team["_id"], ["user"] = user["_id"] };
    private static BsonDocument Relation(BsonDocument user, BsonDocument team, string baseTag = "member") => new() {
        ["team"] = team["_id"], ["user"] = user["_id"], ["status"] = "active", ["base_tag"] = baseTag,
        ["worker_qualifications"] = new BsonArray() };
    private async Task<(BsonDocument Actor, BsonDocument Team, BsonDocument Project)> SetupAsync(string baseTag = "member", bool open = false)
    {
        var actor = new BsonDocument("_id", ObjectId.GenerateNewId());
        var team = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["wqm"] = open ? "open" : "qualified" };
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["t"] = team["_id"], ["st"] = 0 };
        await services.Store.Collection("team").InsertOneAsync(team);
        await services.Store.Collection("team_member").InsertOneAsync(Relation(actor, team, baseTag));
        return (actor, team, project);
    }
}
