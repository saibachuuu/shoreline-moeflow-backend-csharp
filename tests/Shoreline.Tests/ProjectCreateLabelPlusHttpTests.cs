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

/// <summary>
/// Ordinary POST /v1/teams/{teamId}/projects must honour labelplus_txt exactly
/// like Python's Project.create. This collection deliberately keeps the real
/// background JobWorker running so the single-target test observes the queued
/// intent being executed end-to-end, while the other cases assert that no job
/// is written at all.
/// </summary>
[CollectionDefinition("local-services-labelplus-create")]
public sealed class ProjectCreateLabelPlusCollection : ICollectionFixture<LocalServices>;

[Collection("local-services-labelplus-create")]
public sealed class ProjectCreateLabelPlusHttpTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    private const string ValidText =
        ">>>>>>>>[cover.png]<<<<<<<<\n" +
        "----------------[1]----------------[0.25,0.5,1]\n" +
        "第一行\n第二行\n" +
        "----------------[2]----------------[0.75,0.25,2]\n" +
        "\n";

    private const string InvalidText = "----------------[1]----------------[0,0,1]\n";

    [Fact]
    public async Task InvalidTextWritesNoProjectTargetOrJobEvenWithMultipleTargets()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var beforeProjects = await CountProjectsAsync(teamId);
        var beforeTargets = await CountAllAsync("target");
        var beforeJobs = await CountAllAsync("csharp_job");

        // Single target: Python parses before its first save.
        var single = await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN" }, InvalidText));
        await ErrorAsync(single, 4029);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));

        // Multiple targets must still parse first; a bad file may never create
        // rows just because the import itself would have been ineligible.
        var multiple = await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN", "en" }, InvalidText));
        await ErrorAsync(multiple, 4029);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));
    }

    [Fact]
    public async Task InvalidTextWinsOverSameSourceTargetAndValidTextStillReturns6002()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var beforeProjects = await CountProjectsAsync(teamId);
        var beforeTargets = await CountAllAsync("target");
        var beforeJobs = await CountAllAsync("csharp_job");

        // Python Project.create parses labelplus_txt (line 477) BEFORE it
        // rejects source == target (line 500), so the parse failure 4029 wins
        // over 6002 and still writes no row.
        var conflicting = await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "ja" }, InvalidText, sourceLanguage: "ja"));
        await ErrorAsync(conflicting, 4029);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));

        // Valid text with the same source and target keeps 6002.
        var sameLanguage = await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "ja" }, ValidText, sourceLanguage: "ja"));
        await ErrorAsync(sameLanguage, 6002);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));

        // Schema-level language validation still runs before the parser, as in
        // Python's CreateProjectSchema -> Project.create order: an unknown
        // target must report the field error, not 4029.
        var unknownLanguage = await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "not-a-language" }, InvalidText));
        await ErrorAsync(unknownLanguage, 2);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));
    }

    [Fact]
    public async Task ValidSingleTargetQueuesAndWorkerExecutesWithRealCounts()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));

        var response = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN" }, ValidText)));
        var projectId = ObjectId.Parse(response.GetProperty("project").GetProperty("id").GetString());
        // Project.create reloads after enqueue, so the response may already be
        // pending, running or finished; it must never be the untouched default.
        var responseStatus = response.GetProperty("project").GetProperty("import_from_labelplus_status").GetInt32();
        Assert.InRange(responseStatus, ProjectImportService.Pending, ProjectImportService.Succeeded);

        // The durable intent is visible even after the worker has completed it.
        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument { ["kind"] = ProjectImportService.JobKind, ["payload.project_id"] = projectId })
            .FirstOrDefaultAsync();
        Assert.NotNull(job);
        Assert.Equal(projectId, job!["payload"]["project_id"].AsObjectId);

        var project = await WaitForImportAsync(projectId);
        Assert.Equal(ProjectImportService.Succeeded, project.Integer("is"));
        Assert.Equal(0, project.Integer("ip"));
        Assert.Equal("", project.Text("it"));
        Assert.Equal(1, project.Integer("fc"));
        Assert.Equal(2, project.Integer("sc"));

        var file = await services.Store.Collection("file")
            .Find(new BsonDocument("p", projectId)).FirstAsync();
        Assert.Equal(2, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        var sourceIds = await services.Store.Collection("source")
            .Find(new BsonDocument("f", file["_id"]))
            .Project(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(1, await services.Store.Collection("translation")
            .CountDocumentsAsync(new BsonDocument("o",
                new BsonDocument("$in", new BsonArray(sourceIds.Select(source => source["_id"]))))));
        Assert.Equal(1, await services.Store.Collection("target")
            .CountDocumentsAsync(new BsonDocument("t", projectId)));
    }

    [Fact]
    public async Task EmptyAndMultipleTargetsDoNotQueueOrWriteImportFlags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));

        var emptyResponse = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN" }, "")));
        var emptyId = ObjectId.Parse(emptyResponse.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(ProjectImportService.Succeeded,
            emptyResponse.GetProperty("project").GetProperty("import_from_labelplus_status").GetInt32());
        var emptyProject = await ProjectAsync(emptyId);
        Assert.Equal(ProjectImportService.Succeeded, emptyProject.Integer("is"));
        Assert.Equal("", emptyProject.Text("it"));
        Assert.False(await HasJobAsync(emptyId));

        // Valid text with two targets is still parsed, but Python's
        // len(targets) == 1 guard keeps it out of the import queue.
        var multiResponse = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN", "en" }, ValidText)));
        var multiId = ObjectId.Parse(multiResponse.GetProperty("project").GetProperty("id").GetString());
        var multiProject = await ProjectAsync(multiId);
        Assert.Equal(ProjectImportService.Succeeded, multiProject.Integer("is"));
        Assert.Equal("", multiProject.Text("it"));
        Assert.Equal(2, await services.Store.Collection("target")
            .CountDocumentsAsync(new BsonDocument("t", multiId)));
        Assert.False(await HasJobAsync(multiId));
    }

    [Fact]
    public async Task TeamMemberWithoutCreatePermissionGets103AndWritesNoRows()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var member = await UserAsync();
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = teamId,
            ["user"] = member["_id"],
            ["base_tag"] = "member",
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });
        using var memberClient = Client(member);
        var beforeProjects = await CountProjectsAsync(teamId);
        var beforeTargets = await CountAllAsync("target");
        var beforeJobs = await CountAllAsync("csharp_job");

        var response = await memberClient.PostAsJsonAsync("/v1/teams/" + teamId + "/projects",
            ProjectForm(new[] { "zh-CN" }, ValidText));

        await ErrorAsync(response, 103);
        Assert.Equal(beforeProjects, await CountProjectsAsync(teamId));
        Assert.Equal(beforeTargets, await CountAllAsync("target"));
        Assert.Equal(beforeJobs, await CountAllAsync("csharp_job"));
    }

    // ---- helpers ----

    private static object ProjectForm(string[] targets, string labelplus, string sourceLanguage = "ja") => new
    {
        name = Name("project"),
        intro = "",
        allow_apply_type = 1,
        application_check_type = 2,
        source_language = sourceLanguage,
        target_languages = targets,
        labelplus_txt = labelplus
    };

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];

    private async Task<ObjectId> CreateTeamAsync(HttpClient client, string name)
    {
        var role = (await services.Store.Collection("team_role")
            .Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].ToString()!;
        var body = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name,
            intro = "",
            allow_apply_type = 1,
            application_check_type = 2,
            default_role = role
        }));
        return ObjectId.Parse(body.GetProperty("team").GetProperty("id").GetString());
    }

    private Task<BsonDocument> ProjectAsync(ObjectId id) => services.Store.Collection("project")
        .Find(new BsonDocument("_id", id)).FirstAsync();

    private async Task<BsonDocument> WaitForImportAsync(ObjectId projectId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var project = await ProjectAsync(projectId);
            if (project.Integer("is") is ProjectImportService.Succeeded or ProjectImportService.Error)
                return project;
            await Task.Delay(100);
        }
        var stalled = await ProjectAsync(projectId);
        Assert.Fail($"Import did not finish; is={stalled.Integer("is")}, ie={stalled.Integer("ie")}");
        return stalled;
    }

    private Task<bool> HasJobAsync(ObjectId projectId) => services.Store.Collection("csharp_job")
        .Find(new BsonDocument
        {
            ["kind"] = ProjectImportService.JobKind,
            ["payload.project_id"] = projectId
        }).AnyAsync();

    private Task<long> CountProjectsAsync(ObjectId teamId) => services.Store.Collection("project")
        .CountDocumentsAsync(new BsonDocument("t", teamId));

    private Task<long> CountAllAsync(string collection) => services.Store.Collection(collection)
        .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    private async Task<BsonDocument> UserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["n"] = name, ["ns"] = SearchText.Normalize(name), ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["e"] = name + "@example.test", ["p"] = Password,
            ["admin"] = true, ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
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
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetInt32());
    }
}
