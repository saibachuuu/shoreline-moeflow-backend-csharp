using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class FileHttpTests(LocalServices services)
{
    [Fact]
    public async Task MissingTokenLegacyAndSchemaErrors()
    {
        var owner = await CreateUserAsync();
        var project = await CreateProjectAsync(owner.User);
        using var anonymous = services.App.CreateClient();
        using var client = Client(owner.User);

        using var missing = await anonymous.GetAsync($"/v1/projects/{project}/files");
        Assert.Equal(1003, await ErrorCodeAsync(missing));

        using var projectMissing = await client.GetAsync($"/v1/projects/{ObjectId.GenerateNewId()}/files");
        Assert.Equal(4001, await ErrorCodeAsync(projectMissing));

        using var fileMissing = await client.GetAsync($"/v1/files/{ObjectId.GenerateNewId()}");
        Assert.Equal(8001, await ErrorCodeAsync(fileMissing));

        using var conflict = await client.GetAsync($"/v1/projects/{project}/files?only_folder=1&only_file=1");
        var conflictBody = await BodyAsync(conflict);
        Assert.Equal(2, conflictBody.GetProperty("code").GetInt32());
        Assert.Equal("can not use `only_file` and `only_folder` at one time",
            conflictBody.GetProperty("message").GetString());

        using var sort = await client.GetAsync($"/v1/projects/{project}/files?order_by=bogus");
        Assert.Equal(2, await ErrorCodeAsync(sort));

        using var list = await client.GetAsync($"/v1/projects/{project}/files");
        list.EnsureSuccessStatusCode();
        // Shared Pagination.From: legacy APIKIT default is 30, max 100.
        Assert.Equal("30", Assert.Single(list.Headers.GetValues("X-Pagination-Limit")));
    }

    [Fact]
    public async Task NoAccessForeignParentAndForeignTargetAreRejected()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var project = await CreateProjectAsync(owner.User);
        var otherProject = await CreateProjectAsync(stranger.User);
        var foreignFolder = ObjectId.GenerateNewId();
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = foreignFolder,
            ["n"] = "foreign",
            ["t"] = 1,
            ["p"] = otherProject,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["sn"] = "foreign",
            ["dn"] = "",
            ["ac"] = true,
        });
        var foreignTarget = ObjectId.GenerateNewId();
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = foreignTarget,
            ["t"] = otherProject,
            ["l"] = ObjectId.GenerateNewId(),
            ["tsc"] = 0,
            ["csc"] = 0,
        });

        using var strangerClient = Client(stranger.User);
        using var denied = await strangerClient.GetAsync($"/v1/projects/{project}/files");
        Assert.Equal(103, await ErrorCodeAsync(denied));

        using var ownerClient = Client(owner.User);
        using var upload = await UploadAsync(ownerClient, project, "foreign-parent.png", new byte[] { 1, 2, 3 },
            foreignFolder.ToString());
        Assert.Equal(8002, await ErrorCodeAsync(upload));

        using var target = await ownerClient.GetAsync($"/v1/projects/{project}/files?target={foreignTarget}");
        Assert.Equal(4024, await ErrorCodeAsync(target));
    }

    [Fact]
    public async Task UploadGetOverwriteDeleteThroughHttp()
    {
        var owner = await CreateUserAsync();
        var project = await CreateProjectAsync(owner.User);
        using var client = Client(owner.User);

        using var created = await UploadAsync(client, project, "http.png", new byte[] { 1, 2, 3, 4, 5 });
        created.EnsureSuccessStatusCode();
        var body = await BodyAsync(created);
        var id = body.GetProperty("id").GetString()!;
        Assert.False(body.GetProperty("upload_overwrite").GetBoolean());
        Assert.Equal(1, body.GetProperty("file_size").GetInt64());

        using var get = await client.GetAsync($"/v1/files/{id}");
        get.EnsureSuccessStatusCode();
        var detail = await BodyAsync(get);
        Assert.Equal("http.png", detail.GetProperty("name").GetString());
        Assert.Equal(project.ToString(), detail.GetProperty("project_id").GetString());

        using var overwritten = await UploadAsync(client, project, "http.png", new byte[] { 1, 2, 3, 4, 5, 6 });
        overwritten.EnsureSuccessStatusCode();
        var overwriteBody = await BodyAsync(overwritten);
        Assert.Equal(id, overwriteBody.GetProperty("id").GetString());
        Assert.True(overwriteBody.GetProperty("upload_overwrite").GetBoolean());
        Assert.NotEqual(body.GetProperty("save_name").GetString(), overwriteBody.GetProperty("save_name").GetString());

        using var delete = await client.DeleteAsync($"/v1/files/{id}");
        delete.EnsureSuccessStatusCode();
        Assert.Equal("Removed", (await BodyAsync(delete)).GetProperty("message").GetString());
        using var gone = await client.GetAsync($"/v1/files/{id}");
        Assert.Equal(8001, await ErrorCodeAsync(gone));
    }

    [Fact]
    public async Task OrderRevisionHeadersAndRecoveryGuard()
    {
        var owner = await CreateUserAsync();
        var project = await CreateProjectAsync(owner.User, labelRevision: 7);
        await InsertRootImageAsync(project, "001.png", "000001");
        using var client = Client(owner.User);

        using var order = await client.GetAsync($"/v1/projects/{project}/files/order");
        order.EnsureSuccessStatusCode();
        Assert.Equal("7", Assert.Single(order.Headers.GetValues("X-Label-Revision")));

        using var staleHeader = new HttpRequestMessage(HttpMethod.Put, $"/v1/projects/{project}/files/order")
        {
            Content = JsonContent.Create(new { file_ids = Array.Empty<string>(), version = "stale" }),
        };
        staleHeader.Headers.Add("X-Label-Revision", "6");
        using var stale = await client.SendAsync(staleHeader);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await ErrorCodeAsync(stale));

        using var staleVersion = new HttpRequestMessage(HttpMethod.Put, $"/v1/projects/{project}/files/order")
        {
            Content = JsonContent.Create(new { file_ids = Array.Empty<string>(), version = "stale" }),
        };
        staleVersion.Headers.Add("X-Label-Revision", "7");
        using var staleBody = await client.SendAsync(staleVersion);
        Assert.Equal(400, (int)staleBody.StatusCode);
        var stalePayload = await BodyAsync(staleBody);
        Assert.Equal(2, stalePayload.GetProperty("code").GetInt32());
        Assert.Contains("文件列表或顺序已变化", stalePayload.GetProperty("message").GetString());

        await services.Store.Collection("project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", project),
            Builders<BsonDocument>.Update.Set("label_recovery", new BsonDocument("owner", "test")));
        using var blocked = await client.GetAsync($"/v1/projects/{project}/files");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(1, await ErrorCodeAsync(blocked));
    }

    [Fact]
    public async Task ServiceBorrowsMiddlewareLabelSessionWithoutDeadlock()
    {
        var owner = await CreateUserAsync();
        var project = await CreateProjectAsync(owner.User);
        using var client = Client(owner.User);
        // The guard owns a label session for this request; FileService must
        // borrow it instead of acquiring a second lock.
        using var response = await UploadAsync(client, project, "borrowed.png", new byte[] { 9, 9, 9 });
        response.EnsureSuccessStatusCode();
        var body = await BodyAsync(response);
        Assert.Equal("borrowed.png", body.GetProperty("name").GetString());

        var reloaded = await services.Store.Collection("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", project)).SingleAsync();
        Assert.Equal(1L, reloaded.GetValue("fc", 0).ToInt64());
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].AsObjectId.ToString(), user["p"].AsString));
        return client;
    }

    private async Task<(BsonDocument User, ObjectId Project)> CreateUserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["e"] = id + "@example.test",
            ["n"] = "u" + id.ToString()[18..],
            ["ns"] = "u" + id.ToString()[18..],
            ["p"] = PasswordHash.Create("test-password"),
            ["c"] = DateTime.UtcNow,
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return (user, ObjectId.Empty);
    }

    private async Task<ObjectId> CreateProjectAsync(BsonDocument owner, long labelRevision = 0)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "p" + id.ToString()[18..],
            ["ns"] = "p" + id.ToString()[18..],
            ["t"] = BsonNull.Value,
            ["st"] = 0,
            ["label_revision"] = labelRevision,
            ["fo"] = 0,
            ["fc"] = 0,
            ["fs"] = 0L,
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = id,
            ["user"] = owner["_id"].AsObjectId,
            ["ik"] = "user:" + owner["_id"].AsObjectId,
            ["display_name"] = owner["n"].AsString,
            ["dns"] = owner["n"].AsString,
            ["tags"] = new BsonArray { "creator" },
            ["status"] = "active",
            ["create_time"] = DateTime.UtcNow,
            ["edit_time"] = DateTime.UtcNow,
            ["version"] = 1,
        });
        return id;
    }

    private async Task InsertRootImageAsync(ObjectId project, string name, string sortName)
    {
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["t"] = 2,
            ["p"] = project,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["mo"] = BsonNull.Value,
            ["sn"] = sortName,
            ["dn"] = "",
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = 1L,
            ["cover_bytes"] = 0L,
            ["resample_bytes"] = 0L,
            ["fn"] = 1,
            ["ac"] = true,
            ["v"] = 1,
            ["et"] = DateTime.UtcNow,
        });
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, ObjectId project,
        string name, byte[] bytes, string? parentId = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", name);
        if (parentId is not null)
            form.Add(new StringContent(parentId, Encoding.UTF8), "parent_id");
        return await client.PostAsync($"/v1/projects/{project}/files", form);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<int> ErrorCodeAsync(HttpResponseMessage response)
        => (await BodyAsync(response)).GetProperty("code").GetInt32();
}
