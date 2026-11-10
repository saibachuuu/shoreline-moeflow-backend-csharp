using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Real HTTP acceptance for the E1 label routes. Each Fact gets its own random
/// database and its own WebApplicationFactory so source ranks and background
/// timestamps cannot leak between tests.
/// </summary>
public sealed class LabelHttpTests
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task Http_requires_token_and_maps_missing_resources_to_legacy_codes()
    {
        await using var world = await HttpWorld.CreateAsync();

        using var anonymous = world.App.CreateClient();
        var unauthorized = await anonymous.GetAsync(
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(1003, await ErrorCodeAsync(unauthorized));

        var unknownFile = ObjectId.GenerateNewId();
        var missingFile = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{unknownFile}/sources?target_id={world.TargetId}");
        Assert.Equal(8001, await ErrorCodeAsync(missingFile));

        var invalidFile = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/not-an-object-id/sources?target_id={world.TargetId}");
        Assert.Equal(8001, await ErrorCodeAsync(invalidFile));

        var missingSource = await world.SendAsync(HttpMethod.Put,
            $"/v1/sources/{ObjectId.GenerateNewId()}", new { content = "x" });
        Assert.Equal(8005, await ErrorCodeAsync(missingSource));

        var missingTranslation = await world.SendAsync(HttpMethod.Put,
            $"/v1/translations/{ObjectId.GenerateNewId()}", new { content = "x" });
        Assert.Equal(104, await ErrorCodeAsync(missingTranslation));

        var existing = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}");
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(world.ProjectId.ToString(), existing.Headers.GetValues("X-Label-Project").Single());
        Assert.Equal("0", existing.Headers.GetValues("X-Label-Revision").Single());
    }

    [Fact]
    public async Task Http_source_translation_select_proofread_tip_and_mine_others_list()
    {
        await using var world = await HttpWorld.CreateAsync();

        var first = await world.CreateSourceAsync("first");
        var second = await world.CreateSourceAsync("second");

        var translationResponse = await world.SendAsync(HttpMethod.Post,
            $"/v1/sources/{first}/translations",
            new { content = "mine", target_id = world.TargetId.ToString() });
        Assert.Equal(HttpStatusCode.OK, translationResponse.StatusCode);
        var translation = await ReadJsonAsync(translationResponse);
        var translationId = translation.GetProperty("id").GetString()!;
        Assert.Equal(first.ToString(), translation.GetProperty("source_id").GetString());
        Assert.Equal("mine", translation.GetProperty("content").GetString());

        var selected = await world.SendAsync(HttpMethod.Put,
            $"/v1/translations/{translationId}", new { selected = true });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var selectedBody = await ReadJsonAsync(selected);
        Assert.True(selectedBody.GetProperty("selected").GetBoolean());

        var proofread = await world.SendAsync(HttpMethod.Put,
            $"/v1/translations/{translationId}", new { proofread_content = "checked" });
        Assert.Equal(HttpStatusCode.OK, proofread.StatusCode);
        var proofreadBody = await ReadJsonAsync(proofread);
        Assert.Equal("checked", proofreadBody.GetProperty("proofread_content").GetString());
        Assert.Equal(world.UserId.ToString(),
            proofreadBody.GetProperty("proofreader").GetProperty("id").GetString());

        var tip = await world.SendAsync(HttpMethod.Post,
            $"/v1/sources/{first}/tips",
            new { content = "note", target_id = world.TargetId.ToString() });
        Assert.Equal(HttpStatusCode.OK, tip.StatusCode);
        var tipBody = await ReadJsonAsync(tip);
        Assert.Equal("note", tipBody.GetProperty("content").GetString());
        Assert.True(tipBody.TryGetProperty("edit_time", out _));

        var other = await world.AddUserAsync("other");
        await world.SendAsync(HttpMethod.Post, $"/v1/sources/{first}/translations",
            new { content = "theirs", target_id = world.TargetId.ToString() }, client: other.Client);
        await world.SendAsync(HttpMethod.Post, $"/v1/sources/{second}/translations",
            new { content = "elsewhere", target_id = world.TargetId.ToString() }, client: other.Client);

        var list = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}&paging=false");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = (await ReadJsonAsync(list)).EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var one = rows.Single(row => row.GetProperty("content").GetString() == "first");
        Assert.Equal("mine", one.GetProperty("my_translation").GetProperty("content").GetString());
        var others = one.GetProperty("translations").EnumerateArray().ToArray();
        Assert.Single(others);
        Assert.Equal("theirs", others[0].GetProperty("content").GetString());
        Assert.Single(one.GetProperty("tips").EnumerateArray());

        var paged = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}&page=1&limit=1");
        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        Assert.Equal("1", paged.Headers.GetValues("X-Pagination-Page").Single());
        Assert.Equal("1", paged.Headers.GetValues("X-Pagination-Limit").Single());
        Assert.Equal("2", paged.Headers.GetValues("X-Pagination-Count").Single());
        Assert.Equal("2", paged.Headers.GetValues("X-Pagination-Page-Count").Single());
        Assert.Single((await ReadJsonAsync(paged)).EnumerateArray());

        var cache = await world.LoadAsync("file_target_cache", world.CacheId);
        Assert.Equal(2, cache.Integer("ts"));
        Assert.Equal(1, cache.Integer("cs"));
        Assert.Equal(1, await world.Store.Collection("file_target_cache")
            .CountDocumentsAsync(new BsonDocument("f", world.FileId)));
    }

    [Fact]
    public async Task Http_rejects_invalid_schema_and_non_permission_without_writing()
    {
        await using var world = await HttpWorld.CreateAsync();
        var source = await world.CreateSourceAsync("editable");

        var badPosition = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "x", position_type = 3 });
        Assert.Equal(400, (int)badPosition.StatusCode);
        Assert.Equal(2, await ErrorCodeAsync(badPosition));

        var badRange = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "x", x = 1.5 });
        Assert.Equal(2, await ErrorCodeAsync(badRange));

        var emptyEdit = await world.SendAsync(HttpMethod.Put,
            $"/v1/sources/{source}", new { });
        Assert.Equal(2, await ErrorCodeAsync(emptyEdit));

        var missingTarget = await world.SendAsync(HttpMethod.Post,
            $"/v1/sources/{source}/translations",
            new { content = "x", target_id = ObjectId.GenerateNewId().ToString() });
        Assert.Equal(4024, await ErrorCodeAsync(missingTarget));

        var checkOnly = await world.AddUserAsync("check-only", tags: ["checkonly"], checkPolicy: true);
        var denied = await checkOnly.Client.PostAsync(
            $"/v1/files/{world.FileId}/sources",
            JsonContent.Create(new { content = "denied" }));
        Assert.Equal(400, (int)denied.StatusCode);
        Assert.Equal(103, await ErrorCodeAsync(denied));
        Assert.Equal(0, await world.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", world.FileId).Add("c", "denied")));
    }

    [Fact]
    public async Task Http_revision_and_recovery_guard_semantics()
    {
        await using var world = await HttpWorld.CreateAsync();
        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_revision", 1L)));
        await world.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("label_epoch", 1L)));

        var missingHeader = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "missing" });
        Assert.Equal(409, (int)missingHeader.StatusCode);
        Assert.Equal("LabelBusyError", (await ReadJsonAsync(missingHeader)).GetProperty("error").GetString());

        var staleHeader = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "stale" }, revision: 0);
        Assert.Equal(409, (int)staleHeader.StatusCode);

        var correctHeader = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "correct" }, revision: 1);
        Assert.Equal(HttpStatusCode.OK, correctHeader.StatusCode);

        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_recovery", ObjectId.GenerateNewId())));
        var recoveryRead = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}");
        Assert.Equal(409, (int)recoveryRead.StatusCode);

        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$unset", new BsonDocument("label_recovery", 1)));
        var afterRecovery = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}");
        Assert.Equal(HttpStatusCode.OK, afterRecovery.StatusCode);

        await world.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("label_epoch", 0L)));
        var staleRow = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "stale-row" }, revision: 1);
        Assert.Equal(409, (int)staleRow.StatusCode);
    }

    [Fact]
    public async Task Http_concurrent_requests_share_lock_and_update_canonical_cache()
    {
        await using var world = await HttpWorld.CreateAsync();

        var creates = await Task.WhenAll(
            world.SendAsync(HttpMethod.Post, $"/v1/files/{world.FileId}/sources",
                new { content = "concurrent-a" }),
            world.SendAsync(HttpMethod.Post, $"/v1/files/{world.FileId}/sources",
                new { content = "concurrent-b" }));
        Assert.All(creates, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var sourceIds = await Task.WhenAll(creates.Select(async response =>
            (await ReadJsonAsync(response)).GetProperty("id").GetString()!));
        Assert.Equal(2, sourceIds.Distinct().Count());

        var sources = await world.Store.Collection("source")
            .Find(new BsonDocument("f", world.FileId))
            .Sort(new BsonDocument("r", 1)).ToListAsync();
        Assert.Equal(new long[] { 0, 1 }, sources.Select(row => row.Integer("r")).ToArray());
        Assert.Equal(2, (await world.LoadAsync("file", world.FileId)).Integer("sc"));
        Assert.Equal(2, (await world.LoadAsync("project", world.ProjectId)).Integer("sc"));

        var translations = await Task.WhenAll(sourceIds.Select(sourceId =>
            world.SendAsync(HttpMethod.Post, $"/v1/sources/{sourceId}/translations",
                new { content = "concurrent", target_id = world.TargetId.ToString() })));
        Assert.All(translations, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var cache = await world.LoadAsync("file_target_cache", world.CacheId);
        Assert.Equal(2, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
        Assert.Equal(1, await world.Store.Collection("file_target_cache")
            .CountDocumentsAsync(new BsonDocument("_id", world.CacheId)));
        Assert.Equal(2, (await world.LoadAsync("file", world.FileId)).Integer("tsc"));
        Assert.Equal(2, (await world.LoadAsync("project", world.ProjectId)).Integer("tsc"));
        Assert.Equal(2, (await world.LoadAsync("target", world.TargetId)).Integer("tsc"));
        Assert.Equal(0, await world.Store.Collection("project_label_lock")
            .CountDocumentsAsync(new BsonDocument("_id", world.ProjectId)));
    }

    [Fact]
    public async Task Http_header_failure_does_not_abandon_owned_lock()
    {
        await using var world = await HttpWorld.CreateAsync(failRevisionHeaders: true);

        var response = await world.SendAsync(HttpMethod.Post,
            $"/v1/files/{world.FileId}/sources", new { content = "header-failure" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await world.Store.Collection("project_label_lock")
            .CountDocumentsAsync(new BsonDocument("_id", world.ProjectId)));

        var next = await world.SendAsync(HttpMethod.Get,
            $"/v1/files/{world.FileId}/sources?target_id={world.TargetId}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(0, await world.Store.Collection("project_label_lock")
            .CountDocumentsAsync(new BsonDocument("_id", world.ProjectId)));
    }

    private static async Task<int> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class TokenClient(HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;
        public void Dispose() => Client.Dispose();
    }

    private sealed class HttpWorld : IAsyncDisposable
    {
        public required string DatabaseName { get; init; }
        public required MongoStore Store { get; init; }
        public required WebApplicationFactory<Program> App { get; init; }
        public required HttpClient Http { get; init; }
        public required ObjectId UserId { get; init; }
        public required ObjectId ProjectId { get; init; }
        public required ObjectId TeamId { get; init; }
        public required ObjectId FileId { get; init; }
        public required ObjectId TargetId { get; init; }
        public required ObjectId CacheId { get; init; }
        public required string Token { get; init; }

        public static async Task<HttpWorld> CreateAsync(bool failRevisionHeaders = false)
        {
            var database = "shoreline_csharp_label_http_" + Guid.NewGuid().ToString("N");
            var settings = Settings(database);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var store = new MongoStore(configuration);
            await store.InitializeAsync(CancellationToken.None);

            var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                if (failRevisionHeaders)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<LabelRequestGuardHooks>();
                        services.AddSingleton(new LabelRequestGuardHooks
                        {
                            BeforeRevisionHeadersAsync = (_, _) =>
                                throw new IOException("injected revision header failure")
                        });
                    });
                }
            });

            var http = app.CreateClient();
            var userId = ObjectId.GenerateNewId();
            var projectId = ObjectId.GenerateNewId();
            var teamId = ObjectId.GenerateNewId();
            var fileId = ObjectId.GenerateNewId();
            var targetId = ObjectId.GenerateNewId();
            var languageId = ObjectId.GenerateNewId();
            var cacheId = ObjectId.GenerateNewId();
            var now = DateTime.UtcNow;
            var userName = "http-" + Guid.NewGuid().ToString("N");
            var user = new BsonDocument
            {
                ["_id"] = userId,
                ["n"] = userName,
                ["ns"] = userName,
                ["as"] = new BsonArray(),
                ["l"] = "auto",
                ["p"] = Password,
                ["e"] = userName + "@example.test",
                ["admin"] = false,
                ["b"] = false,
                ["c"] = now
            };
            await store.Collection("user").InsertOneAsync(user);
            await store.Collection("project").InsertOneAsync(new BsonDocument
            {
                ["_id"] = projectId,
                ["n"] = "HTTP label project",
                ["st"] = BsonInt32.Create(0),
                ["t"] = teamId,
                ["ou"] = userId,
                ["label_revision"] = 0L,
                ["m_e"] = now
            });
            await store.Collection("project_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["project"] = projectId,
                ["user"] = userId,
                ["status"] = "active",
                ["tags"] = new BsonArray { "creator" }
            });
            await store.Collection("language").InsertOneAsync(new BsonDocument
            {
                ["_id"] = languageId,
                ["c"] = "简体中文",
                ["e"] = "Chinese",
                ["co"] = "zh-" + Guid.NewGuid().ToString("N"),
                ["gt"] = "zh-" + Guid.NewGuid().ToString("N"),
                ["go"] = "zh-" + Guid.NewGuid().ToString("N"),
                ["n"] = false,
                ["s"] = 1L
            });
            await store.Collection("target").InsertOneAsync(new BsonDocument
            {
                ["_id"] = targetId,
                ["t"] = projectId,
                ["l"] = languageId,
                ["tsc"] = 0,
                ["csc"] = 0,
                ["ct"] = now,
                ["et"] = now,
                ["i"] = "",
                ["label_epoch"] = 0L
            });
            await store.Collection("file").InsertOneAsync(new BsonDocument
            {
                ["_id"] = fileId,
                ["n"] = "page.png",
                ["t"] = BsonInt32.Create(2),
                ["p"] = projectId,
                ["a"] = new BsonArray(),
                ["ac"] = true,
                ["sc"] = 0,
                ["tsc"] = 0,
                ["csc"] = 0,
                ["et"] = now,
                ["label_epoch"] = 0L
            });
            await store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
            {
                ["_id"] = cacheId,
                ["f"] = fileId,
                ["t"] = targetId,
                ["ts"] = 0,
                ["cs"] = 0,
                ["e"] = now,
                ["label_epoch"] = 0L
            });

            var token = app.Services.GetRequiredService<LegacyToken>().Issue(userId.ToString(), Password);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            return new HttpWorld
            {
                DatabaseName = database,
                Store = store,
                App = app,
                Http = http,
                UserId = userId,
                ProjectId = projectId,
                TeamId = teamId,
                FileId = fileId,
                TargetId = targetId,
                CacheId = cacheId,
                Token = token
            };
        }

        public async Task<ObjectId> CreateSourceAsync(string content)
        {
            var response = await SendAsync(HttpMethod.Post,
                $"/v1/files/{FileId}/sources", new { content });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return ObjectId.Parse((await ReadJsonAsync(response)).GetProperty("id").GetString());
        }

        public async Task<TokenClient> AddUserAsync(
            string name, string[]? tags = null, bool checkPolicy = false)
        {
            var id = ObjectId.GenerateNewId();
            var userName = name + "-" + Guid.NewGuid().ToString("N");
            await Store.Collection("user").InsertOneAsync(new BsonDocument
            {
                ["_id"] = id,
                ["n"] = userName,
                ["ns"] = userName,
                ["as"] = new BsonArray(),
                ["l"] = "auto",
                ["p"] = Password,
                ["e"] = userName + "@example.test",
                ["admin"] = false,
                ["b"] = false,
                ["c"] = DateTime.UtcNow
            });
            await Store.Collection("project_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["project"] = ProjectId,
                ["user"] = id,
                ["status"] = "active",
                ["tags"] = new BsonArray(tags ?? new[] { "creator" })
            });
            if (checkPolicy)
            {
                await Store.Collection("identity_tag_policy").InsertOneAsync(new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["team"] = TeamId,
                    ["project_tags"] = new BsonDocument
                    {
                        ["checkonly"] = new BsonDocument
                        {
                            ["permissions"] = new BsonArray
                            {
                                "project:ACCESS",
                                "project:CHECK_TRA"
                            }
                        }
                    }
                });
            }
            var token = App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), Password);
            var client = App.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return new TokenClient(client);
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body = null,
            long? revision = null,
            HttpClient? client = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (revision is not null)
                request.Headers.Add("X-Label-Revision", revision.Value.ToString(CultureInfo.InvariantCulture));
            return await (client ?? Http).SendAsync(request);
        }

        public Task<BsonDocument> LoadAsync(string collection, ObjectId id) =>
            Store.Collection(collection).Find(new BsonDocument("_id", id)).FirstAsync();

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
            await new MongoClient("mongodb://127.0.0.1:" + TestEnvironment.MongoPort).DropDatabaseAsync(DatabaseName);
        }

        private static Dictionary<string, string?> Settings(string database) => new()
        {
            ["Mongo:Uri"] = "mongodb://127.0.0.1:" + TestEnvironment.MongoPort + "/" + database,
            ["Auth:Secret"] = "label-http-test-secret-0123456789abcdef",
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
}
