using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// HTTP-level checks through the real endpoint pipeline and the shared
/// LabelRequestGuard. The verification snapshot wires these routes only in its
/// task-owned Program copy; the shared Program remains untouched.
/// </summary>
[Collection("local-services")]
public sealed class LabelSnapshotHttpTests(LocalServices services)
{
    [Fact]
    public async Task Merge_uses_legacy_target_id_query_and_rejects_explicit_null_maps()
    {
        var world = await WorldAsync(proofread: true);
        var outsider = await UserAsync("http-outsider");
        using var outsiderClient = ClientFor(outsider);
        using var denied = await outsiderClient.GetAsync(
            $"/v1/projects/{world.ProjectId}/label-merge");
        await AssertErrorAsync(denied, HttpStatusCode.BadRequest,
            "NoPermissionError", 103, "没有权限");

        using var wrongQuery = await world.Client.GetAsync(
            $"/v1/projects/{world.ProjectId}/label-merge?targetId={world.TargetId}");
        await AssertErrorAsync(wrongQuery, HttpStatusCode.BadRequest,
            "TargetNotExistError", 4024, "项目目标语言不存在");

        using var previewResponse = await world.Client.GetAsync(
            $"/v1/projects/{world.ProjectId}/label-merge?target_id={world.TargetId}");
        var preview = await JsonAsync(previewResponse, HttpStatusCode.OK);
        var token = preview.GetProperty("token").GetString()!;
        Assert.Equal(64, token.Length);
        var candidate = preview.GetProperty("candidates")[0];
        Assert.Equal(world.SourceId.ToString(),
            candidate.GetProperty("source_id").GetString());
        Assert.Equal(world.ProofreaderId.ToString(),
            candidate.GetProperty("user_id").GetString());
        Assert.Equal("校对稿", candidate.GetProperty("versions")[0]
            .GetProperty("content").GetString());

        foreach (var field in new[] { "versions", "selections" })
        {
            var body = new Dictionary<string, object?>
            {
                ["target_id"] = world.TargetId.ToString(),
                ["token"] = token,
                ["versions"] = field == "versions" ? null : new Dictionary<string, string>(),
                ["selections"] = field == "selections" ? null : new Dictionary<string, string>()
            };
            using var invalid = await world.Client.PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/label-merge", body);
            await AssertErrorAsync(invalid, HttpStatusCode.Conflict,
                "LabelOperationError", 1, "冲突选择数据无效");
        }

        Assert.Equal(0, await SnapshotCountAsync(world.ProjectId));

        var valid = new Dictionary<string, object?>
        {
            ["target_id"] = world.TargetId.ToString(),
            ["token"] = token,
            ["versions"] = new Dictionary<string, string>(),
            ["selections"] = new Dictionary<string, string>()
        };
        using var merged = await world.Client.PostAsJsonAsync(
            $"/v1/projects/{world.ProjectId}/label-merge", valid);
        var mergedBody = await JsonAsync(merged, HttpStatusCode.OK);
        Assert.False(string.IsNullOrWhiteSpace(mergedBody.GetProperty("snapshot_id").GetString()));
        Assert.Equal("1", RevisionHeader(merged));
        Assert.Equal(1, await SnapshotCountAsync(world.ProjectId));

        var row = await services.Store.Collection("translation")
            .Find(new BsonDocument
            {
                ["o"] = world.SourceId,
                ["t"] = world.TargetId,
                ["u"] = world.ProofreaderId
            })
            .FirstOrDefaultAsync();
        Assert.NotNull(row);
        Assert.Equal("校对稿", row.Text("c"));
        Assert.Equal("", row.Text("p"));
        Assert.True(row.Flag("s"));

        // The token is now stale. Use a matching revision header so this is the
        // token check rather than the guard's stale-revision check.
        using var staleRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/label-merge")
        {
            Content = JsonContent.Create(valid)
        };
        staleRequest.Headers.Add("X-Label-Revision", "1");
        using var staleResponse = await world.Client.SendAsync(staleRequest);
        await AssertErrorAsync(staleResponse, HttpStatusCode.Conflict,
            "LabelOperationError", 1, "预览后数据已改变，请重新确认冲突");
        Assert.Equal(1, await SnapshotCountAsync(world.ProjectId));
    }

    [Fact]
    public async Task Restore_returns_revision_header_and_stale_header_is_409_json()
    {
        var world = await WorldAsync();
        var snapshotId = await CreateSnapshotAsync(world, "恢复快照");

        using var first = await RestoreAsync(world, snapshotId, null);
        await JsonAsync(first, HttpStatusCode.OK);
        Assert.Equal("1", RevisionHeader(first));
        Assert.Equal(2, await SnapshotCountAsync(world.ProjectId));

        using var stale = await RestoreAsync(world, snapshotId, "0");
        await AssertErrorAsync(stale, HttpStatusCode.Conflict,
            "LabelBusyError", 1,
            "项目标签正在操作或等待恢复，请刷新后重试: 标签数据已被批量修改，请重新打开编辑页面");
        Assert.Equal("1", RevisionHeader(stale));
        Assert.Equal(2, await SnapshotCountAsync(world.ProjectId));

        using var retried = await RestoreAsync(world, snapshotId, "1");
        await JsonAsync(retried, HttpStatusCode.OK);
        Assert.Equal("2", RevisionHeader(retried));
        Assert.Equal(3, await SnapshotCountAsync(world.ProjectId));
    }

    [Fact]
    public async Task Recovery_journal_blocks_ordinary_http_and_recover_clears_it()
    {
        var world = await WorldAsync();
        var snapshotId = await CreateSnapshotAsync(world, "恢复日志");
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_recovery",
                ObjectId.Parse(snapshotId))));

        using var blocked = await world.Client.GetAsync($"/v1/projects/{world.ProjectId}");
        await AssertErrorAsync(blocked, HttpStatusCode.Conflict,
            "LabelBusyError", 1, "项目标签正在操作或等待恢复，请刷新后重试");

        using var recovered = await world.Client.PostAsJsonAsync(
            $"/v1/projects/{world.ProjectId}/label-recover",
            new Dictionary<string, object?>());
        var body = await JsonAsync(recovered, HttpStatusCode.OK);
        Assert.True(body.GetProperty("recovered").GetBoolean());
        Assert.Equal("1", RevisionHeader(recovered));

        var project = await services.Store.Collection("project")
            .Find(new BsonDocument("_id", world.ProjectId)).FirstAsync();
        Assert.False(project.Contains("label_recovery"));
        Assert.Equal(1, project["label_revision"].ToInt64());

        using var allowed = await world.Client.GetAsync($"/v1/projects/{world.ProjectId}/targets");
        var targets = await JsonAsync(allowed, HttpStatusCode.OK);
        Assert.Equal(1, targets.GetProperty("count").GetInt32());
        Assert.Equal("1", RevisionHeader(allowed));
        await AssertNoLockRowAsync(world.ProjectId);
    }

    [Fact]
    public async Task Snapshot_list_revision_headers_state_and_shared_lock_are_http_visible()
    {
        var world = await WorldAsync();
        await CreateSnapshotAsync(world, "第一条");
        await CreateSnapshotAsync(world, "第二条");

        using var listed = await world.Client.GetAsync(
            $"/v1/projects/{world.ProjectId}/snapshots?page=1&limit=1");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("2", listed.Headers.GetValues("X-Pagination-Count").Single());
        Assert.Equal("1", listed.Headers.GetValues("X-Pagination-Page").Single());
        Assert.Equal("1", listed.Headers.GetValues("X-Pagination-Limit").Single());
        Assert.Equal("2", listed.Headers.GetValues("X-Pagination-Page-Count").Single());
        var page = await JsonAsync(listed, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Array, page.ValueKind);
        Assert.Equal(1, page.GetArrayLength());
        var item = page[0];
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
        Assert.True(DateTime.TryParse(item.GetProperty("created").GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Assert.Contains(item.GetProperty("name").GetString(), new[] { "第一条", "第二条" });
        Assert.Equal(1, item.GetProperty("counts").GetProperty("files").GetInt32());

        using var stateResponse = await world.Client.GetAsync(
            $"/v1/projects/{world.ProjectId}/label-state");
        var state = await JsonAsync(stateResponse, HttpStatusCode.OK);
        var capabilities = state.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("create").GetBoolean());
        Assert.True(capabilities.GetProperty("restore").GetBoolean());
        Assert.True(capabilities.GetProperty("clear_proofread").GetBoolean());
        Assert.True(capabilities.GetProperty("merge").GetBoolean());
        Assert.True(state.GetProperty("has_source_content").GetBoolean());
        Assert.Equal(1, state.GetProperty("counts").GetProperty("sources").GetInt32());
        Assert.False(state.GetProperty("recovery_required").GetBoolean());
        Assert.Equal("0", RevisionHeader(stateResponse));
        await AssertNoLockRowAsync(world.ProjectId);
    }

    private async Task<HttpWorld> WorldAsync(bool proofread = false)
    {
        var actor = await UserAsync("http-actor");
        var teamId = ObjectId.GenerateNewId();
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = teamId,
            ["n"] = "http-team-" + teamId
        });
        var projectId = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "HTTP 快照项目",
            ["st"] = 0,
            ["ou"] = actor["_id"],
            ["t"] = teamId,
            ["label_revision"] = 0L,
            ["m_e"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["user"] = actor["_id"],
            ["status"] = "active",
            ["tags"] = new BsonArray { "creator" }
        });

        var languageId = ObjectId.GenerateNewId();
        await services.Store.Collection("language").InsertOneAsync(new BsonDocument
        {
            ["_id"] = languageId,
            ["co"] = "zh-" + languageId,
            ["n"] = "中文"
        });
        var fileId = ObjectId.GenerateNewId();
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = fileId,
            ["n"] = "01.png",
            ["t"] = 2,
            ["p"] = projectId,
            ["a"] = new BsonArray(),
            ["ac"] = true,
            ["sc"] = 1,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["sa"] = true,
            ["fs"] = 10,
            ["md"] = "md5",
            ["v"] = 1,
            ["et"] = DateTime.UtcNow,
            ["label_epoch"] = 0L
        });
        var targetId = ObjectId.GenerateNewId();
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = targetId,
            ["t"] = projectId,
            ["l"] = languageId,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow,
            ["i"] = "",
            ["label_epoch"] = 0L
        });
        await services.Store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = fileId,
            ["t"] = targetId,
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = DateTime.UtcNow,
            ["label_epoch"] = 0L
        });
        var sourceId = ObjectId.GenerateNewId();
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = sourceId,
            ["f"] = fileId,
            ["r"] = 0,
            ["c"] = "原文",
            ["x"] = 0.0,
            ["y"] = 0.0,
            ["v"] = new BsonArray(),
            ["p"] = 0,
            ["lf"] = false,
            ["b"] = false,
            ["cm"] = false,
            ["pt"] = new BsonArray(),
            ["ct"] = DateTime.UtcNow,
            ["e"] = DateTime.UtcNow,
            ["label_epoch"] = 0L
        });

        ObjectId? proofreaderId = null;
        if (proofread)
        {
            var proofreader = await UserAsync("http-proofreader");
            proofreaderId = proofreader["_id"].AsObjectId;
            await services.Store.Collection("translation").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = sourceId,
                ["t"] = targetId,
                ["c"] = "原译",
                ["u"] = actor["_id"],
                ["p"] = "校对稿",
                ["pr"] = proofreaderId.Value,
                ["s"] = false,
                ["mt"] = false,
                ["ct"] = DateTime.UtcNow,
                ["e"] = DateTime.UtcNow,
                ["label_epoch"] = 0L
            });
        }

        var client = ClientFor(actor);
        return new HttpWorld(client, projectId, fileId, targetId, sourceId, proofreaderId);
    }

    private HttpClient ClientFor(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> UserAsync(string prefix)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = prefix + "-" + id,
            ["e"] = prefix + "-" + id + "@example.test",
            ["as"] = new BsonArray(),
            ["l"] = "auto",
            ["ns"] = prefix,
            ["asrch"] = false,
            ["admin"] = false,
            ["b"] = false,
            ["p"] = "http-password-hash"
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private static async Task<string> CreateSnapshotAsync(HttpWorld world, string name)
    {
        using var response = await world.Client.PostAsJsonAsync(
            $"/v1/projects/{world.ProjectId}/snapshots",
            new Dictionary<string, object?> { ["name"] = name });
        var body = await JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("0", RevisionHeader(response));
        var id = body.GetProperty("snapshot_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    private static async Task<HttpResponseMessage> RestoreAsync(
        HttpWorld world, string snapshotId, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/projects/{world.ProjectId}/snapshots/{snapshotId}/restore")
        {
            Content = JsonContent.Create(new Dictionary<string, object?>())
        };
        if (revision is not null) request.Headers.Add("X-Label-Revision", revision);
        return await world.Client.SendAsync(request);
    }

    private async Task<long> SnapshotCountAsync(ObjectId projectId) =>
        await services.Store.Collection("project_label_snapshot")
            .CountDocumentsAsync(new BsonDocument("project", projectId));

    private async Task AssertNoLockRowAsync(ObjectId projectId) =>
        Assert.Equal(0, await services.Store.Collection("project_label_lock")
            .CountDocumentsAsync(new BsonDocument("_id", projectId)));

    private static string? RevisionHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Label-Revision", out var values)
            ? values.FirstOrDefault()
            : null;

    private static async Task<JsonElement> JsonAsync(
        HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(text));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response, HttpStatusCode status,
        string error, int code, string message)
    {
        var body = await JsonAsync(response, status);
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.Equal(code, body.GetProperty("code").GetInt32());
        Assert.Equal(message, body.GetProperty("message").GetString());
    }

    private sealed record HttpWorld(
        HttpClient Client, ObjectId ProjectId, ObjectId FileId,
        ObjectId TargetId, ObjectId SourceId, ObjectId? ProofreaderId) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
