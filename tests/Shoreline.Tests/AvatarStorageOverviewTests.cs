using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

// Avatar and admin-storage-overview HTTP evidence is intentionally snapshot-only:
// the shared Program.cs is not wired in this slice. The archive copy adds
// AddAvatarServices/MapAvatarEndpoints and
// AddAdminStorageOverviewServices/MapAdminStorageOverviewEndpoints.
[Collection("local-services")]
public sealed class AvatarStorageOverviewTests(LocalServices services)
{
    [Fact]
    public async Task UserAvatarUploadStoresPrefixAndRemovesOldObject()
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        user["a"] = "old.jpg";
        await services.Store.Collection("user").UpdateOneAsync(
            new BsonDocument("_id", user["_id"]),
            new BsonDocument("$set", new BsonDocument("a", "old.jpg")));
        await local.Storage.PutAsync("user-avatars/old.jpg",
            new MemoryStream(Encoding.UTF8.GetBytes("old")), "image/jpeg", CancellationToken.None);
        var service = Service(local);

        var result = await service.UploadAsync(user, UploadFile("new"), "user", null, CancellationToken.None);

        Assert.Equal("修改成功", result.Message);
        Assert.StartsWith("http://files.example.test/user-avatars/", result.Avatar);
        var saved = await FindAsync("user", user["_id"]);
        var key = saved.Text("a");
        Assert.EndsWith(".jpg", key);
        Assert.NotEqual("old.jpg", key);
        Assert.True(File.Exists(Path.Combine(local.Root, "user-avatars", key)));
        Assert.Null(await local.Storage.HeadAsync("user-avatars/old.jpg"));
        Assert.EndsWith("/" + key, result.Avatar);
    }

    [Fact]
    public async Task TeamAvatarUploadRequiresChangeAndStoresTeamPrefix()
    {
        using var local = new LocalAvatarStorage();
        var actor = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, actor, "creator");
        var service = Service(local);

        var result = await service.UploadAsync(actor, UploadFile("team"), "team",
            team["_id"].ToString(), CancellationToken.None);

        Assert.Equal("修改成功", result.Message);
        Assert.StartsWith("http://files.example.test/team-avatars/", result.Avatar);
        var saved = await FindAsync("team", team["_id"]);
        Assert.EndsWith(".jpg", saved.Text("a"));
        Assert.True(File.Exists(Path.Combine(local.Root, "team-avatars", saved.Text("a"))));
    }

    [Fact]
    public async Task UserAvatarIgnoresSuppliedId()
    {
        using var local = new LocalAvatarStorage();
        var actor = await UserAsync();
        var other = await UserAsync();
        var service = Service(local);

        await service.UploadAsync(actor, UploadFile("me"), "user", other["_id"].ToString(), CancellationToken.None);

        Assert.NotEmpty((await FindAsync("user", actor["_id"])).Text("a"));
        Assert.Empty((await FindAsync("user", other["_id"])).Text("a"));
    }

    [Fact]
    public async Task MissingFileReturnsLegacyCode109()
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            Service(local).UploadAsync(user, null, "user", null, CancellationToken.None));
        Assert.Equal(109, error.Code);
        Assert.Equal("上传未包含文件: 请选择图片", error.Message);
    }

    [Fact]
    public async Task UnsupportedTypeReturnsLegacyCode111()
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            Service(local).UploadAsync(user, UploadFile("x"), "group", null, CancellationToken.None));
        Assert.Equal(111, error.Code);
        Assert.Equal("请求参数错误: 不支持的头像类型", error.Message);
    }

    [Fact]
    public async Task MissingOrInvalidTeamIdReturns3001()
    {
        using var local = new LocalAvatarStorage();
        var actor = await UserAsync();
        var service = Service(local);

        var invalid = await Assert.ThrowsAsync<ApiError>(() =>
            service.UploadAsync(actor, UploadFile("x"), "team", "not-an-object-id", CancellationToken.None));
        Assert.Equal(3001, invalid.Code);

        var missing = await Assert.ThrowsAsync<ApiError>(() =>
            service.UploadAsync(actor, UploadFile("x"), "team", ObjectId.GenerateNewId().ToString(), CancellationToken.None));
        Assert.Equal(3001, missing.Code);
    }

    [Fact]
    public async Task TeamAvatarWithoutChangePermissionReturns103()
    {
        using var local = new LocalAvatarStorage();
        var actor = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, actor, "member");
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            Service(local).UploadAsync(actor, UploadFile("x"), "team", team["_id"].ToString(), CancellationToken.None));
        Assert.Equal(103, error.Code);
    }

    [Fact]
    public async Task UploadFailurePreservesExistingAvatar()
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        user["a"] = "keep.jpg";
        await services.Store.Collection("user").UpdateOneAsync(
            new BsonDocument("_id", user["_id"]),
            new BsonDocument("$set", new BsonDocument("a", "keep.jpg")));

        // Make the owned root inaccessible as a directory; the local backend
        // fails while creating the avatar parent, before touching the owner.
        Directory.Delete(local.Root, recursive: true);
        await File.WriteAllTextAsync(local.Root, "blocked");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            Service(local).UploadAsync(user, UploadFile("x"), "user", null, CancellationToken.None));

        Assert.Equal("keep.jpg", (await FindAsync("user", user["_id"])).Text("a"));
    }

    [Fact]
    public async Task PersistFailureDeletesNewObjectAndDoesNotLeaveDanglingReference()
    {
        using var local = new LocalAvatarStorage();
        // An authenticated actor normally exists. This synthetic actor exercises
        // the concurrent-delete/failed-update path after a successful upload.
        var ghost = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "ghost",
            ["a"] = ""
        };
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            Service(local).UploadAsync(ghost, UploadFile("x"), "user", null, CancellationToken.None));
        Assert.Equal(1005, error.Code);

        var page = await local.Storage.ListAsync("user-avatars/", null, 100, CancellationToken.None);
        Assert.Empty(page.Objects);
    }

    [Theory]
    [InlineData("new-avatars/", null, "new-avatars/")]
    [InlineData(null, "legacy-avatars/", "legacy-avatars/")]
    [InlineData(null, null, "user-avatars/")]
    public async Task UploadedPrefixIsSharedByUserInfoAndPresence(
        string? userAvatarPrefix, string? legacyAvatarPrefix, string expectedPrefix)
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        using var app = LocalApp(local.Root, userAvatarPrefix, legacyAvatarPrefix);
        var client = Client(app, user);

        var uploadBody = await BodyAsync(await client.PutAsync("/v1/avatar", Multipart("avatar", "user")));
        var saved = (await FindAsync("user", user["_id"])).Text("a");
        var expectedKey = expectedPrefix + saved;
        Assert.EndsWith("/" + expectedKey, uploadBody.GetProperty("avatar").GetString());

        var info = await BodyAsync(await client.GetAsync("/v1/user/info"));
        Assert.EndsWith("/" + expectedKey, info.GetProperty("avatar").GetString());

        var projectId = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "presence-" + projectId.ToString()[12..],
            ["t"] = BsonNull.Value
        });
        await BodyAsync(await client.PostAsync(
            $"/v1/projects/{projectId}/presence/heartbeat",
            new StringContent("{}", Encoding.UTF8, "application/json")));
        var presence = await BodyAsync(await client.GetAsync($"/v1/projects/{projectId}/presence"));
        var active = presence.GetProperty("active_users").EnumerateArray().Single();
        Assert.EndsWith("/" + expectedKey, active.GetProperty("avatar").GetString());
    }

    [Fact]
    public async Task PersistExceptionAfterWriteRetainsNewObjectAndReference()
    {
        using var local = new LocalAvatarStorage();
        var user = await UserAsync();
        user["a"] = "old.jpg";
        await services.Store.Collection("user").UpdateOneAsync(
            new BsonDocument("_id", user["_id"]),
            new BsonDocument("$set", new BsonDocument("a", "old.jpg")));
        await local.Storage.PutAsync("user-avatars/old.jpg", new MemoryStream([1, 2, 3]), "image/jpeg",
            CancellationToken.None);

        AvatarPersistAsync writeThenThrow = async (collection, filter, update, token) =>
        {
            await collection.UpdateOneAsync(filter, update, cancellationToken: token);
            throw new TimeoutException("simulated lost acknowledgement");
        };

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            Service(local, writeThenThrow).UploadAsync(
                user, UploadFile("new"), "user", null, CancellationToken.None));
        Assert.Contains("lost acknowledgement", error.Message);

        var saved = (await FindAsync("user", user["_id"])).Text("a");
        Assert.NotEqual("old.jpg", saved);
        Assert.True(System.IO.File.Exists(Path.Combine(local.Root, "user-avatars", saved)));
        Assert.True(System.IO.File.Exists(Path.Combine(local.Root, "user-avatars", "old.jpg")));
    }

    [Fact]
    public void StorageOverviewS3ShapeRedactsSecretsAndEndpointPath()
    {
        var view = new AdminStorageOverviewService(Config(new()
        {
            ["Storage:Type"] = "R2",
            ["Storage:Endpoint"] = "https://accountid.r2.cloudflarestorage.com:8443/account/path",
            ["Storage:Bucket"] = "bucket",
            ["Storage:AccessKey"] = "AKIA-ACCESS-SECRET",
            ["Storage:SecretKey"] = "SECRET-KEY-VALUE",
            ["Storage:KeyPrefix"] = "storage/",
            ["Storage:ViaCdn"] = "true",
            ["Storage:Domain"] = "https://cdn.example.test"
        })).View();

        Assert.Equal("OSS", view["storage_type"]);
        Assert.Equal("S3", view["bucket_style"]);
        Assert.Equal("R2", view["bucket_style_raw"]);
        Assert.Equal("accountid.r2.cloudflarestorage.com", view["endpoint_host"]);
        Assert.Equal("bucket", view["bucket"]);
        Assert.Equal("storage/", view["key_prefix"]);
        Assert.Equal(true, view["via_cdn"]);
        Assert.Equal("https://cdn.example.test", view["cdn_domain"]);
        Assert.Equal(false, Capabilities(view)["server_side_image_processing"]);
        Assert.Equal(false, Capabilities(view)["cdn_url_auth"]);
        Assert.Equal(true, view["requires_local_thumbnail"]);
        Assert.Equal("", view["warning"]);

        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("AKIA-ACCESS-SECRET", json);
        Assert.DoesNotContain("SECRET-KEY-VALUE", json);
        Assert.DoesNotContain(":8443", json);
        Assert.DoesNotContain("/account/path", json);
        Assert.DoesNotContain("access_key", json);
        Assert.DoesNotContain("secret_key", json);
    }

    [Fact]
    public void StorageOverviewUnknownStyleWarnsExactly()
    {
        var view = new AdminStorageOverviewService(Config(new()
        {
            ["Storage:Type"] = "mystery",
            ["Storage:Endpoint"] = "https://s3.example.test"
        })).View();

        Assert.Equal("OSS", view["storage_type"]);
        Assert.Equal("S3", view["bucket_style"]);
        Assert.Equal("mystery", view["bucket_style_raw"]);
        Assert.Equal(
            "OSS_BUCKET_STYLE='mystery' 不是已知取值，已按标准 S3 处理。可用取值：S3 / ALI。",
            view["warning"]);
    }

    [Fact]
    public void StorageOverviewAliyunEndpointAsS3WarnsExactly()
    {
        var view = new AdminStorageOverviewService(Config(new()
        {
            ["Storage:Type"] = "S3",
            ["Storage:Endpoint"] = "https://oss-cn-hangzhou.aliyuncs.com"
        })).View();

        Assert.Equal("S3", view["bucket_style"]);
        Assert.Equal(
            "配置危险：endpoint 指向阿里云，但储存风格被解析为标准 S3。阿里云请显式设置 OSS_BUCKET_STYLE=ALI，否则签名方式与图片处理都会失效。",
            view["warning"]);
    }

    [Fact]
    public void StorageOverviewLocalShapeMatchesLegacyOverview()
    {
        var root = Path.Combine(Path.GetTempPath(), "shoreline-overview-" + Guid.NewGuid().ToString("N"));
        var view = new AdminStorageOverviewService(Config(new()
        {
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = root,
            ["Storage:Domain"] = "http://files.example.test",
            ["Storage:FilePrefix"] = "files",
            ["Storage:OutputPrefix"] = "outputs"
        })).View();

        Assert.Equal("LOCAL_STORAGE", view["storage_type"]);
        Assert.Null(view["bucket_style"]);
        Assert.Equal("", view["bucket_style_raw"]);
        Assert.Equal("", view["endpoint_host"]);
        Assert.Equal("", view["bucket"]);
        Assert.Equal("", view["key_prefix"]);
        Assert.Equal(false, view["via_cdn"]);
        Assert.Equal(true, view["requires_local_thumbnail"]);
        Assert.Equal("files/", Prefixes(view)["files"]);
        Assert.Equal("outputs/", Prefixes(view)["outputs"]);
    }

    [Fact]
    public void StorageOverviewAliShapeIsReportedButBackendIsUnsupported()
    {
        var view = new AdminStorageOverviewService(Config(new()
        {
            ["Storage:Type"] = "ALI",
            ["Storage:Endpoint"] = "https://oss-cn-hangzhou.aliyuncs.com",
            ["Storage:Bucket"] = "bucket",
            ["Storage:KeyPrefix"] = "ignored/"
        })).View();

        Assert.Equal("OSS", view["storage_type"]);
        Assert.Equal("ALI", view["bucket_style"]);
        Assert.Equal("", view["key_prefix"]);
        Assert.Equal(true, Capabilities(view)["server_side_image_processing"]);
        Assert.Equal(true, Capabilities(view)["cdn_url_auth"]);
        Assert.Equal(false, view["requires_local_thumbnail"]);
        Assert.Equal("", view["warning"]);
    }

    [Fact]
    public async Task AvatarHttpRequiresTokenAndStoresUserAvatar()
    {
        var root = Path.Combine(Path.GetTempPath(), "shoreline-avatar-http-" + Guid.NewGuid().ToString("N"));
        using var factory = LocalApp(root);
        var user = await UserAsync();

        using var anonymous = factory.CreateClient();
        using var missing = await anonymous.PutAsync("/v1/avatar", Multipart("x", "user"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(1003, (await ErrorBodyAsync(missing)).GetProperty("code").GetInt32());

        using var client = Client(factory, user);
        using var response = await client.PutAsync("/v1/avatar", Multipart("avatar", "user"));
        var body = await BodyAsync(response);
        Assert.Equal("修改成功", body.GetProperty("message").GetString());
        var key = (await FindAsync("user", user["_id"])).Text("a");
        Assert.True(File.Exists(Path.Combine(root, "user-avatars", key)));
        Assert.EndsWith("/user-avatars/" + key, body.GetProperty("avatar").GetString());
    }

    [Fact]
    public async Task AdminStorageOverviewRequiresAdminAndRedactsSecrets()
    {
        var admin = await UserAsync(admin: true);
        var normal = await UserAsync();
        using var normalClient = Client(services.App, normal);
        using var denied = await normalClient.GetAsync("/v1/admin/storage/default");
        Assert.Equal(103, (await ErrorBodyAsync(denied)).GetProperty("code").GetInt32());

        using var adminClient = Client(services.App, admin);
        using var response = await adminClient.GetAsync("/v1/admin/storage/default");
        var body = await BodyAsync(response);
        var names = body.EnumerateObject().Select(p => p.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[]
        {
            "bucket", "bucket_style", "bucket_style_raw", "capabilities", "cdn_domain",
            "endpoint_host", "key_prefix", "prefixes", "requires_local_thumbnail",
            "storage_type", "via_cdn", "warning"
        }.OrderBy(name => name), names);
        Assert.Equal("OSS", body.GetProperty("storage_type").GetString());
        Assert.DoesNotContain(":", body.GetProperty("endpoint_host").GetString());
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("access_key", raw);
        Assert.DoesNotContain("secret_key", raw);
    }

    [Fact]
    public async Task SiteHomepageOldAndNewAliasesReturnSameBody()
    {
        using var oldResponse = await services.Http.GetAsync("/v1/homepage");
        using var newResponse = await services.Http.GetAsync("/v1/site/homepage");
        Assert.True(oldResponse.IsSuccessStatusCode);
        Assert.True(newResponse.IsSuccessStatusCode);
        var oldBody = await oldResponse.Content.ReadAsStringAsync();
        var newBody = await newResponse.Content.ReadAsStringAsync();
        Assert.Equal(oldBody, newBody);
        using var document = JsonDocument.Parse(newBody);
        Assert.True(document.RootElement.TryGetProperty("html", out _));
    }

    // ---- helpers ---------------------------------------------------------

    private AvatarService Service(LocalAvatarStorage local, AvatarPersistAsync? persist = null) => new(
        services.Store,
        services.App.Services.GetRequiredService<IdentityPermissions>(),
        local.Storage,
        local.Configuration,
        NullLogger<AvatarService>.Instance)
    {
        PersistAsync = persist
    };

    private static IFormFile UploadFile(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "avatar.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private static MultipartFormDataContent Multipart(string content, string type)
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        body.Add(file, "file", "avatar.jpg");
        body.Add(new StringContent(type), "type");
        return body;
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, object?> Capabilities(Dictionary<string, object?> view) =>
        (Dictionary<string, object?>)view["capabilities"]!;

    private static Dictionary<string, object?> Prefixes(Dictionary<string, object?> view) =>
        (Dictionary<string, object?>)view["prefixes"]!;

    private WebApplicationFactory<Program> LocalApp(
        string root, string? userAvatarPrefix = "user-avatars/", string? avatarPrefix = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = root,
            ["Storage:Domain"] = "http://files.example.test",
            ["Storage:FilePrefix"] = "files/",
            ["Storage:OutputPrefix"] = "outputs/",
            ["Storage:TeamAvatarPrefix"] = "team-avatars/"
        };
        if (userAvatarPrefix is not null) settings["Storage:UserAvatarPrefix"] = userAvatarPrefix;
        if (avatarPrefix is not null) settings["Storage:AvatarPrefix"] = avatarPrefix;
        return services.App.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings)));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> FindAsync(string collection, BsonValue id) =>
        await services.Store.Collection(collection)
            .Find(new BsonDocument("_id", id))
            .FirstAsync();

    private async Task<BsonDocument> UserAsync(bool admin = false)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = name + "@example.test",
            ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = admin,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> TeamAsync()
    {
        var team = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "team_" + ObjectId.GenerateNewId().ToString()[12..],
            ["c"] = DateTime.UtcNow
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task MemberAsync(BsonDocument team, BsonDocument user, string baseTag) =>
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ErrorBodyAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class LocalAvatarStorage : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "shoreline-avatar-" + Guid.NewGuid().ToString("N"));

        public IConfiguration Configuration { get; }
        public ObjectStorage Storage { get; }

        public LocalAvatarStorage()
        {
            Directory.CreateDirectory(Root);
            Configuration = Config(new()
            {
                ["Storage:Type"] = "Local",
                ["Storage:Root"] = Root,
                ["Storage:Domain"] = "http://files.example.test",
                ["Storage:UserAvatarPrefix"] = "user-avatars/",
                ["Storage:TeamAvatarPrefix"] = "team-avatars/"
            });
            Storage = new ObjectStorage(Configuration);
        }

        public void Dispose()
        {
            Storage.Dispose();
            try
            {
                if (System.IO.File.Exists(Root)) System.IO.File.Delete(Root);
                else if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
