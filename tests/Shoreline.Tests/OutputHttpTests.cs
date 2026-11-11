using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Formats;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Real HTTP acceptance for the three Python output routes. These use a real
/// WebApplicationFactory/Program, real Mongo and the local S3-compatible test
/// store. The background JobWorker is removed so the test can invoke the real
/// registered output handler deterministically.
/// </summary>
public sealed class OutputHttpTests
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task Http_requires_token_permission_and_rejects_foreign_target()
    {
        await using var world = await World.CreateAsync();

        using var anonymous = world.App.CreateClient();
        var unauthorized = await anonymous.GetAsync(
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(1003, await ErrorCodeAsync(unauthorized));

        var forbidden = await world.SendAsync(HttpMethod.Get,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            client: world.StrangerClient);
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
        Assert.Equal(103, await ErrorCodeAsync(forbidden));

        var foreign = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.ForeignTargetId}/outputs",
            new { type = 0 });
        Assert.Equal(4024, await ErrorCodeAsync(foreign));

        var empty = await world.SendAsync(HttpMethod.Get,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await JsonAsync(empty)).EnumerateArray());
        Assert.Equal("0", empty.Headers.GetValues("X-Label-Revision").Single());
    }

    [Fact]
    public async Task Http_team_outputs_requires_auto_admin_permission_and_queues_all_targets()
    {
        await using var world = await World.CreateAsync();

        using var anonymous = world.App.CreateClient();
        var unauthorized = await anonymous.PostAsync($"/v1/teams/{world.TeamId}/outputs", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(1003, await ErrorCodeAsync(unauthorized));

        var forbidden = await world.SendAsync(HttpMethod.Post,
            $"/v1/teams/{world.TeamId}/outputs", client: world.StrangerClient);
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
        Assert.Equal(103, await ErrorCodeAsync(forbidden));

        var created = await world.SendAsync(HttpMethod.Post, $"/v1/teams/{world.TeamId}/outputs");
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        Assert.Equal("Export task created successfully.", (await JsonAsync(created)).GetProperty("message").GetString());
        Assert.Equal(2, await world.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("p", world.ProjectId)));
        Assert.Equal(2, await world.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = OutputService.JobKind,
                ["payload.project_id"] = world.ProjectId,
            }));
    }

    [Fact]
    public async Task Http_create_is_queued_then_real_handler_produces_archive()
    {
        await using var world = await World.CreateAsync();

        var created = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0, file_ids_include = Array.Empty<string>(), file_ids_exclude = Array.Empty<string>() });
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var queued = await JsonAsync(created);
        var outputId = ObjectId.Parse(queued.GetProperty("id").GetString());
        Assert.Equal(0, queued.GetProperty("status").GetInt32());
        Assert.Equal(0, queued.GetProperty("type").GetInt32());
        Assert.Equal(6, queued.GetProperty("status_details").GetArrayLength());

        var row = await LoadAsync(world, "output", outputId);
        Assert.Equal(0, row.Integer("s"));
        Assert.Equal(0L, row["sb"].ToInt64());
        Assert.Equal("", row.Text("file_name"));

        var job = await world.Store.Collection("csharp_job").Find(
            new BsonDocument("payload.output_id", outputId)).FirstAsync();
        Assert.Equal("pending", job["state"].AsString);
        Assert.Equal("output_project", job["kind"].AsString);
        Assert.Empty((await world.Storage.ListAsync(
            "outputs/" + outputId + "/", null, 100, CancellationToken.None)).Objects);

        await world.InvokeOutputJobAsync(job);

        var listed = await world.SendAsync(HttpMethod.Get,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var completed = Assert.Single((await JsonAsync(listed)).EnumerateArray());
        Assert.Equal(4, completed.GetProperty("status").GetInt32());
        var completedRow = await LoadAsync(world, "output", outputId);
        var fileName = completedRow.Text("file_name");
        Assert.EndsWith(".zip", fileName);
        Assert.True(completed.GetProperty("storage_bytes").GetInt64() > 0);
        var link = completed.GetProperty("link").GetString();
        Assert.False(string.IsNullOrWhiteSpace(link));
        Assert.Contains("response-content-disposition=attachment", link);

        var finalKey = "outputs/" + outputId + "/" + fileName;
        Assert.NotNull(await world.Storage.HeadAsync(finalKey, CancellationToken.None));
        var objectDownload = await world.Storage.GetAsync(finalKey, CancellationToken.None);
        using var archive = new ZipArchive(objectDownload.Content, ZipArchiveMode.Read, leaveOpen: false);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("translations.txt", names);
        Assert.Contains("project.json", names);
        Assert.Contains("images/page.png", names);
        using var reader = new StreamReader(archive.GetEntry("translations.txt")!.Open(), Encoding.UTF8);
        var labelPlus = await reader.ReadToEndAsync();
        Assert.StartsWith(LabelPlusWriter.Header, labelPlus, StringComparison.Ordinal);
        Assert.Contains(">>>>>>>>[page.png]<<<<<<<<\r\n", labelPlus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_validates_schema_cooldown_and_all_target_skip()
    {
        await using var world = await World.CreateAsync();

        var badType = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 2 });
        Assert.Equal(2, await ErrorCodeAsync(badType));

        var badId = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0, file_ids_include = new[] { "not-an-object-id" } });
        Assert.Equal(2, await ErrorCodeAsync(badId));

        var first = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0 });
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var tooFast = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0 });
        Assert.Equal(9002, await ErrorCodeAsync(tooFast));

        var all = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/outputs");
        Assert.True(all.StatusCode == HttpStatusCode.OK, await all.Content.ReadAsStringAsync());
        var created = (await JsonAsync(all)).EnumerateArray().ToArray();
        Assert.Single(created);
        Assert.Equal(world.SecondTargetId.ToString(),
            created[0].GetProperty("target").GetProperty("id").GetString());

        Assert.Equal(1, await world.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("t", world.TargetId)));
        Assert.Equal(1, await world.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("t", world.SecondTargetId)));
    }

    [Fact]
    public async Task Http_epoch_headers_and_restore_stale_mark_error_without_publish()
    {
        await using var world = await World.CreateAsync();
        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_revision", 7L)));

        var missing = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0 });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(1, await ErrorCodeAsync(missing));

        var stale = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0 }, revision: 6);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await ErrorCodeAsync(stale));

        var accepted = await world.SendAsync(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs",
            new { type = 0 }, revision: 7);
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        Assert.Equal("7", accepted.Headers.GetValues("X-Label-Revision").Single());
        var outputId = ObjectId.Parse((await JsonAsync(accepted)).GetProperty("id").GetString());

        // Simulate a restore completing between queueing and the worker claim.
        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_revision", 8L)));
        var job = await world.Store.Collection("csharp_job").Find(
            new BsonDocument("payload.output_id", outputId)).FirstAsync();
        await world.InvokeOutputJobAsync(job);

        var row = await LoadAsync(world, "output", outputId);
        Assert.Equal(OutputStatus.Error, row.Integer("s"));
        Assert.Equal("stale_revision", row.Text("job_error"));
        Assert.Empty((await world.Storage.ListAsync(
            "outputs/" + outputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    private sealed class World : IAsyncDisposable
    {
        public required string DatabaseName { get; init; }
        public required MongoStore Store { get; init; }
        public required WebApplicationFactory<Program> App { get; init; }
        public required HttpClient Http { get; init; }
        public required HttpClient StrangerClient { get; init; }
        public required ObjectStorage Storage { get; init; }
        public required ObjectId TeamId { get; init; }
        public required ObjectId ProjectId { get; init; }
        public required ObjectId TargetId { get; init; }
        public required ObjectId SecondTargetId { get; init; }
        public required ObjectId ForeignTargetId { get; init; }

        public static async Task<World> CreateAsync()
        {
            var database = "shoreline_csharp_output_http_" + Guid.NewGuid().ToString("N");
            var settings = Settings(database);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var store = new MongoStore(configuration);
            await store.InitializeAsync(CancellationToken.None);
            var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                builder.ConfigureTestServices(services =>
                {
                    // HTTP tests invoke the real handler explicitly; the worker would race.
                    services.RemoveAll<IHostedService>();
                });
            });
            var http = app.CreateClient();
            var stranger = app.CreateClient();

            var now = DateTime.UtcNow;
            var userId = ObjectId.GenerateNewId();
            var strangerId = ObjectId.GenerateNewId();
            var projectId = ObjectId.GenerateNewId();
            var teamId = ObjectId.GenerateNewId();
            var languageId = ObjectId.GenerateNewId();
            var setRoleId = ObjectId.GenerateNewId();
            var setId = ObjectId.GenerateNewId();
            var targetId = ObjectId.GenerateNewId();
            var secondTargetId = ObjectId.GenerateNewId();
            var foreignProjectId = ObjectId.GenerateNewId();
            var foreignTargetId = ObjectId.GenerateNewId();
            var fileId = ObjectId.GenerateNewId();

            await store.Collection("user").InsertManyAsync([
                User(userId, "http-output-" + Guid.NewGuid().ToString("N")),
                User(strangerId, "http-stranger-" + Guid.NewGuid().ToString("N"))
            ]);
            await store.Collection("language").DeleteManyAsync(new BsonDocument("co", "zh-hans"));
            await store.Collection("language").InsertOneAsync(new BsonDocument
            {
                ["_id"] = languageId, ["c"] = "简体中文", ["e"] = "Chinese",
                ["co"] = "zh-hans", ["gt"] = "zh-hans", ["go"] = "zh-hans",
                ["n"] = false, ["s"] = 1L
            });
            await store.Collection("project_set").InsertOneAsync(new BsonDocument
            {
                ["_id"] = setId, ["n"] = "默认作品集", ["d"] = true,
                ["ct"] = now, ["et"] = now
            });
            await store.Collection("team").InsertOneAsync(new BsonDocument
            {
                ["_id"] = teamId, ["n"] = "HTTP output team", ["ou"] = userId,
                ["ct"] = now, ["et"] = now
            });
            await store.Collection("team_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["team"] = teamId,
                ["user"] = userId, ["base_tag"] = "creator", ["tags"] = new BsonArray(),
                ["status"] = "active", ["create_time"] = now, ["edit_time"] = now
            });
            // app/models/identity_tag.py tag_definition lets a per-team policy
            // override the creator tag. Grant AUTO explicitly so this HTTP
            // path stays exercised; the shipped defaults omit it.
            await store.Collection("identity_tag_policy").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("team", teamId),
                Builders<BsonDocument>.Update
                    .Set("team_tags.creator", new BsonDocument
                    {
                        ["name"] = "creator", ["code"] = "creator",
                        ["permissions"] = new BsonArray { "team:ACCESS", "team:AUTO_BECOME_PROJECT_ADMIN" }
                    })
                    .SetOnInsert("team", teamId),
                new UpdateOptions { IsUpsert = true });
            await store.Collection("project_role")
                .DeleteManyAsync(new BsonDocument("m_o", "translator"));
            await store.Collection("project_role").InsertOneAsync(new BsonDocument
            {
                ["_id"] = setRoleId, ["m_o"] = "translator", ["m_n"] = "翻译",
                ["m_l"] = 1, ["m_i"] = "", ["m_p"] = new BsonArray(),
                ["m_s"] = true, ["m_c"] = now
            });
            await store.Collection("project").InsertOneAsync(new BsonDocument
            {
                ["_id"] = projectId, ["n"] = "HTTP output project", ["i"] = "",
                ["st"] = 0, ["t"] = teamId, ["ou"] = userId, ["ps"] = setId,
                ["dr"] = setRoleId, ["ol"] = languageId, ["m_a"] = 1, ["m_t"] = 1,
                ["label_revision"] = 0L, ["ct"] = now, ["m_e"] = now
            });
            await store.Collection("project_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(), ["project"] = projectId,
                ["user"] = userId, ["status"] = "active", ["tags"] = new BsonArray { "creator" }
            });
            await store.Collection("target").InsertManyAsync([
                new BsonDocument
                {
                    ["_id"] = targetId, ["t"] = projectId, ["l"] = languageId,
                    ["tsc"] = 0, ["csc"] = 0, ["ct"] = now, ["et"] = now, ["i"] = ""
                },
                new BsonDocument
                {
                    ["_id"] = secondTargetId, ["t"] = projectId, ["l"] = languageId,
                    ["tsc"] = 0, ["csc"] = 0, ["ct"] = now, ["et"] = now, ["i"] = ""
                },
                new BsonDocument
                {
                    ["_id"] = foreignTargetId, ["t"] = foreignProjectId, ["l"] = languageId,
                    ["tsc"] = 0, ["csc"] = 0, ["ct"] = now, ["et"] = now, ["i"] = ""
                }
            ]);
            await store.Collection("file").InsertOneAsync(new BsonDocument
            {
                ["_id"] = fileId, ["n"] = "page.png", ["t"] = 2, ["p"] = projectId,
                ["a"] = new BsonArray(), ["ac"] = true, ["sa"] = "page.png",
                ["dn"] = "page.png", ["sn"] = "page.png", ["et"] = now
            });
            var storage = app.Services.GetRequiredService<ObjectStorage>();
            await using (var image = new MemoryStream(Encoding.UTF8.GetBytes("not-a-real-png")))
                await storage.PutAsync("files/page.png", image, "image/png", CancellationToken.None);

            var token = app.Services.GetRequiredService<LegacyToken>().Issue(userId.ToString(), Password);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var strangerToken = app.Services.GetRequiredService<LegacyToken>()
                .Issue(strangerId.ToString(), Password);
            stranger.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", strangerToken);

            return new World
            {
                DatabaseName = database, Store = store, App = app, Http = http,
                StrangerClient = stranger, Storage = storage, TeamId = teamId, ProjectId = projectId,
                TargetId = targetId, SecondTargetId = secondTargetId,
                ForeignTargetId = foreignTargetId
            };
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path,
            object? body = null, long? revision = null, HttpClient? client = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (revision is not null)
                request.Headers.Add("X-Label-Revision", revision.Value.ToString());
            return await (client ?? Http).SendAsync(request);
        }

        public async Task InvokeOutputJobAsync(BsonDocument job)
        {
            var handler = App.Services.GetServices<IJobHandler>()
                .Single(candidate => candidate.Kind == OutputService.JobKind);
            await handler.ExecuteAsync(job["payload"].AsBsonDocument, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            StrangerClient.Dispose();
            await App.DisposeAsync();
            await new MongoClient("mongodb://127.0.0.1:" + TestEnvironment.MongoPort).DropDatabaseAsync(DatabaseName);
        }

        private static BsonDocument User(ObjectId id, string name) => new()
        {
            ["_id"] = id, ["n"] = name, ["ns"] = name, ["as"] = new BsonArray(),
            ["l"] = "auto", ["p"] = Password, ["e"] = name + "@example.test",
            ["admin"] = false, ["b"] = false, ["c"] = DateTime.UtcNow
        };

        private static Dictionary<string, string?> Settings(string database) => new()
        {
            ["Mongo:Uri"] = "mongodb://127.0.0.1:" + TestEnvironment.MongoPort + "/" + database,
            ["Auth:Secret"] = "output-http-test-secret-0123456789abcdef",
            ["Auth:AdminEmail"] = "owner@example.test",
            ["Notifications:WorkerEnabled"] = "false",
            ["Archive:AllowedHosts:0"] = "api.example.test",
            ["Storage:Endpoint"] = "http://127.0.0.1:" + TestEnvironment.S3Port,
            ["Storage:Bucket"] = "shoreline-test",
            ["Storage:AccessKey"] = "local-test-only",
            ["Storage:SecretKey"] = "local-disposable-test-secret",
            ["Storage:ForcePathStyle"] = "true",
            ["Mail:Enabled"] = "true",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = TestEnvironment.SmtpPort.ToString(),
            ["Mail:StartTls"] = "false",
            ["Mail:From"] = "noreply@example.test"
        };
    }

    private static Task<BsonDocument> LoadAsync(World world, string collection, ObjectId id) =>
        world.Store.Collection(collection).Find(new BsonDocument("_id", id)).FirstAsync();

    private static async Task<int> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        return body.GetProperty("code").GetInt32();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}




