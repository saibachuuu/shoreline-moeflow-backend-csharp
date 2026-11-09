using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// These HTTP tests require the isolated G4 snapshot Program wiring
/// (AddLabelServices/AddThumbnailServices/MapThumbnailEndpoints). The shared
/// Program is intentionally untouched until main reviews this slice.
/// </summary>
[Collection("local-services")]
public sealed class ThumbnailHttpTests(LocalServices services)
{
    private readonly CancellationToken ct = CancellationToken.None;

    [Fact]
    public async Task FileThumbnail_MissingToken_ReturnsLegacyNeedToken()
    {
        var project = await ProjectAsync();
        var file = await FileAsync(project, "no-token.jpg");

        var response = await services.App.CreateClient()
            .PostAsync($"/v1/files/{file}/thumbnail", null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1003, (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task FileThumbnail_ChecksAccessTypeAndQueuesJob()
    {
        var project = await ProjectAsync();
        var image = await FileAsync(project, "http-image.jpg");
        var text = await FileAsync(project, "http-note.txt", type: 3);
        var (_, outsider) = await UserAsync("file-outsider@example.test");
        var (member, memberClient) = await UserAsync("file-member@example.test");
        await GrantAsync(member, project, []);

        var denied = await outsider.PostAsync($"/v1/files/{image}/thumbnail", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(103, deniedBody.GetProperty("code").GetInt32());
        Assert.Equal("您没有此项目的访问权限", deniedBody.GetProperty("message").GetString());

        var wrongType = await memberClient.PostAsync($"/v1/files/{text}/thumbnail", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        var typeBody = await wrongType.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(108, typeBody.GetProperty("code").GetInt32());
        Assert.Equal("文件类型不支持此操作: 仅图片文件可生成缩略图", typeBody.GetProperty("message").GetString());

        var queued = await memberClient.PostAsync($"/v1/files/{image}/thumbnail", null, ct);
        queued.EnsureSuccessStatusCode();
        var queuedBody = await queued.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("缩略图生成任务已触发", queuedBody.GetProperty("message").GetString());
        Assert.Equal(1, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = image.ToString()
            }));
    }

    [Fact]
    public async Task ProjectThumbnails_RequiresChangeAndQueuesActivatedImages()
    {
        var project = await ProjectAsync();
        var active = await FileAsync(project, "project-active.jpg");
        var inactive = await FileAsync(project, "project-inactive.jpg", activated: false);
        var text = await FileAsync(project, "project-note.txt", type: 3);
        var (member, memberClient) = await UserAsync("project-member@example.test");
        await GrantAsync(member, project, []);

        var denied = await memberClient.PostAsync($"/v1/projects/{project}/thumbnails", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("code").GetInt32());

        var (creator, creatorClient) = await UserAsync("project-creator@example.test");
        await GrantAsync(creator, project, ["creator"]);
        var queued = await creatorClient.PostAsync($"/v1/projects/{project}/thumbnails", null, ct);
        queued.EnsureSuccessStatusCode();
        var body = await queued.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("已为 1 张图片触发缩略图生成任务", body.GetProperty("message").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(1, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = active.ToString()
            }));
        Assert.Equal(0, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = inactive.ToString()
            }));
        Assert.Equal(0, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = text.ToString()
            }));
    }

    [Fact]
    public async Task FileThumbnail_StoragePool_FailsClosedWith409()
    {
        var project = await ProjectAsync(csid: "pool-http");
        var image = await FileAsync(project, "pool-http.jpg");
        var (creator, client) = await UserAsync("pool-creator@example.test");
        await GrantAsync(creator, project, ["creator"]);

        var response = await client.PostAsync($"/v1/files/{image}/thumbnail", null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(2, body.GetProperty("code").GetInt32());
        Assert.Equal(
            "当前切片尚未接线存储池路由，无法在迁移后的存储池项目上执行文件操作",
            body.GetProperty("message").GetString());
        Assert.Equal(0, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = image.ToString()
            }));
    }

    [Fact]
    public async Task FileThumbnail_UnknownFile_ReturnsLegacyFileNotExist()
    {
        var (_, client) = await UserAsync("missing-file@example.test");

        var response = await client.PostAsync("/v1/files/not-an-object-id/thumbnail", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(8001, body.GetProperty("code").GetInt32());
        Assert.Equal("文件/文件夹不存在，可能已被删除", body.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------- fixtures

    private async Task<(BsonDocument User, HttpClient Client)> UserAsync(string email)
    {
        var id = ObjectId.GenerateNewId();
        var password = PasswordHash.Create("test-password");
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["e"] = email,
            ["n"] = email,
            ["p"] = password,
            ["admin"] = false,
            ["c"] = DateTime.UtcNow
        };
        await services.Store.Collection("user").InsertOneAsync(user, cancellationToken: ct);
        var token = services.App.Services.GetRequiredService<LegacyToken>()
            .Issue(id.ToString(), password);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, client);
    }

    private async Task GrantAsync(BsonDocument user, ObjectId project, string[] tags)
    {
        await services.Store.Collection("project_member").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["user"] = user["_id"].AsObjectId,
                ["project"] = project,
                ["status"] = "active",
                ["tags"] = new BsonArray(tags)
            },
            cancellationToken: ct);
    }

    private async Task<ObjectId> ProjectAsync(string? csid = null)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "http-project-" + id.ToString(),
            ["t"] = ObjectId.GenerateNewId(),
            ["st"] = 0,
            ["label_revision"] = 0,
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fs"] = new BsonInt64(0)
        };
        if (csid is not null) document["csid"] = csid;
        await services.Store.Collection("project").InsertOneAsync(document, cancellationToken: ct);
        return id;
    }

    private async Task<ObjectId> FileAsync(
        ObjectId project,
        string saveName,
        int type = 2,
        bool activated = true)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("file").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = id,
                ["p"] = project,
                ["n"] = saveName,
                ["t"] = type,
                ["ac"] = activated,
                ["sa"] = saveName,
                ["th"] = ThumbnailService.ThumbnailUnknown,
                ["the"] = string.Empty,
                ["tht"] = DateTime.UtcNow,
                ["cover_bytes"] = new BsonInt64(0),
                ["resample_bytes"] = new BsonInt64(0),
                ["label_epoch"] = 0L
            },
            cancellationToken: ct);
        return id;
    }
}
