using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class MitHttpTests(LocalServices services) : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    private string CreateTempStorageRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mit_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    private WebApplicationFactory<Program> CreateEnabledApp(string storageRoot) =>
        services.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mit:StorageRoot"] = storageRoot
            })));

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Ignored in cleanup
            }
        }
    }

    [Fact]
    public async Task MitEndpoints_WhenDisabled_Returns404()
    {
        using var client = services.App.CreateClient();

        var imagesRes = await client.PostAsync("/v1/mit/images", null);
        Assert.Equal(HttpStatusCode.NotFound, imagesRes.StatusCode);

        var imageTasksPostRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new { filename = "test.png" });
        Assert.Equal(HttpStatusCode.NotFound, imageTasksPostRes.StatusCode);

        var imageTasksGetRes = await client.GetAsync("/v1/mit/image-tasks/task-123");
        Assert.Equal(HttpStatusCode.NotFound, imageTasksGetRes.StatusCode);

        var translateTasksPostRes = await client.PostAsJsonAsync("/v1/mit/translate-tasks", new { text = "hello" });
        Assert.Equal(HttpStatusCode.NotFound, translateTasksPostRes.StatusCode);

        var translateTasksGetRes = await client.GetAsync("/v1/mit/translate-tasks/task-123");
        Assert.Equal(HttpStatusCode.NotFound, translateTasksGetRes.StatusCode);
    }

    [Fact]
    public async Task MitEndpoints_UploadImage_ValidationAndSuccess()
    {
        var storageRoot = CreateTempStorageRoot();
        using var app = CreateEnabledApp(storageRoot);
        using var client = app.CreateClient();

        // 1. 无文件请求
        var emptyRes = await client.PostAsync("/v1/mit/images", null);
        Assert.Equal(HttpStatusCode.BadRequest, emptyRes.StatusCode);
        var emptyJson = await emptyRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UploadFileNotFoundError", emptyJson.GetProperty("error").GetString());
        Assert.Equal(101, emptyJson.GetProperty("code").GetInt32());

        // 2. 表单中无 file 字段
        using (var noFileForm = new MultipartFormDataContent())
        {
            noFileForm.Add(new StringContent("some-data"), "other_field");
            var res = await client.PostAsync("/v1/mit/images", noFileForm);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("UploadFileNotFoundError", json.GetProperty("error").GetString());
            Assert.Equal(101, json.GetProperty("code").GetInt32());
        }

        // 3. 非法后缀（.txt）
        using (var txtForm = new MultipartFormDataContent())
        {
            txtForm.Add(new ByteArrayContent([1, 2, 3]), "file", "document.txt");
            var res = await client.PostAsync("/v1/mit/images", txtForm);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("UploadFileNotFoundError", json.GetProperty("error").GetString());
            Assert.Equal(101, json.GetProperty("code").GetInt32());
        }

        // 4. 空文件内容（0 字节）
        using (var zeroByteForm = new MultipartFormDataContent())
        {
            zeroByteForm.Add(new ByteArrayContent([]), "file", "empty.png");
            var res = await client.PostAsync("/v1/mit/images", zeroByteForm);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("UploadFileNotFoundError", json.GetProperty("error").GetString());
        }

        // 5. 成功上传合法图片（.png）
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
        using (var validForm = new MultipartFormDataContent())
        {
            validForm.Add(new ByteArrayContent(pngBytes), "file", "manga.png");
            var res = await client.PostAsync("/v1/mit/images", validForm);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            var filename = json.GetProperty("filename").GetString();
            Assert.NotNull(filename);
            Assert.EndsWith(".png", filename, StringComparison.OrdinalIgnoreCase);

            var savedPath = Path.Combine(storageRoot, filename);
            Assert.True(File.Exists(savedPath));
            var savedBytes = await File.ReadAllBytesAsync(savedPath);
            Assert.Equal(pngBytes, savedBytes);
        }
    }

    [Fact]
    public async Task MitEndpoints_ImageTask_ValidationAndPathTraversal_Rejected()
    {
        var storageRoot = CreateTempStorageRoot();
        using var app = CreateEnabledApp(storageRoot);
        using var client = app.CreateClient();

        // 1. 缺失 filename
        var noFileRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new { task_name = "mit_detect_text" });
        Assert.Equal(HttpStatusCode.BadRequest, noFileRes.StatusCode);
        var noFileJson = await noFileRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", noFileJson.GetProperty("error").GetString());
        Assert.Equal(2, noFileJson.GetProperty("code").GetInt32());

        // 2. 路径穿越：相对路径 ../
        var traversalRes1 = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = "../secret.txt",
            task_name = "mit_detect_text"
        });
        Assert.Equal(HttpStatusCode.BadRequest, traversalRes1.StatusCode);
        var traversalJson1 = await traversalRes1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", traversalJson1.GetProperty("error").GetString());

        // 3. 路径穿越：..\\
        var traversalRes2 = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = @"..\..\secret.txt",
            task_name = "mit_detect_text"
        });
        Assert.Equal(HttpStatusCode.BadRequest, traversalRes2.StatusCode);
        var traversalJson2 = await traversalRes2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", traversalJson2.GetProperty("error").GetString());

        // 4. 路径穿越：绝对路径超出 storageRoot
        var outOfRootPath = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/passwd";
        var traversalRes3 = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = outOfRootPath,
            task_name = "mit_detect_text"
        });
        Assert.Equal(HttpStatusCode.BadRequest, traversalRes3.StatusCode);
        var traversalJson3 = await traversalRes3.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", traversalJson3.GetProperty("error").GetString());

        // 5. 找不到文件（合法文件名但不存在）
        var notFoundRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = "does_not_exist.png",
            task_name = "mit_detect_text"
        });
        Assert.Equal(HttpStatusCode.BadRequest, notFoundRes.StatusCode);
        var notFoundJson = await notFoundRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", notFoundJson.GetProperty("error").GetString());
        Assert.Equal("File not found", notFoundJson.GetProperty("message").GetString());

        // 6. 先上传一个真实文件，测试非法 task_name
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "sample.jpg");
        var uploadRes = await client.PostAsync("/v1/mit/images", form);
        var uploadJson = await uploadRes.Content.ReadFromJsonAsync<JsonElement>();
        var uploadedFilename = uploadJson.GetProperty("filename").GetString()!;

        var invalidTaskRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = uploadedFilename,
            task_name = "invalid_task_type"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidTaskRes.StatusCode);
        var invalidTaskJson = await invalidTaskRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidateError", invalidTaskJson.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MitEndpoints_ImageTask_DetectText_And_Status_Query_Lifecycle()
    {
        var storageRoot = CreateTempStorageRoot();
        using var app = CreateEnabledApp(storageRoot);
        using var client = app.CreateClient();

        // 1. 上传图片
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3, 4]), "file", "page.png");
        var uploadRes = await client.PostAsync("/v1/mit/images", form);
        Assert.Equal(HttpStatusCode.OK, uploadRes.StatusCode);
        var uploadJson = await uploadRes.Content.ReadFromJsonAsync<JsonElement>();
        var filename = uploadJson.GetProperty("filename").GetString()!;

        // 2. 派发 mit_detect_text 任务
        var taskPostRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename,
            task_name = "mit_detect_text",
            detect_size = 1280
        });
        Assert.Equal(HttpStatusCode.OK, taskPostRes.StatusCode);
        var taskPostJson = await taskPostRes.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = taskPostJson.GetProperty("task_id").GetString()!;
        Assert.False(string.IsNullOrEmpty(taskId));

        // 验证 MongoDB csharp_job 存储
        var jobObjectId = ObjectId.Parse(taskId);
        var jobDoc = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", jobObjectId))
            .FirstOrDefaultAsync();
        Assert.NotNull(jobDoc);
        Assert.Equal("mit_detect_text", jobDoc["kind"].AsString);
        Assert.Equal("pending", jobDoc["state"].AsString);
        Assert.Equal(1280, jobDoc["payload"]["detect_size"].AsInt32);
        Assert.EndsWith(filename, jobDoc["payload"]["image_path"].AsString);

        // 3. 查询 pending 状态
        var getPendingRes = await client.GetAsync($"/v1/mit/image-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getPendingRes.StatusCode);
        var getPendingJson = await getPendingRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getPendingJson.GetProperty("task_id").GetString());
        Assert.Equal("pending", getPendingJson.GetProperty("status").GetString());

        // 4. 模拟任务执行完成（done）
        var resultDoc = new BsonDocument
        {
            ["textlines"] = new BsonArray
            {
                new BsonDocument
                {
                    ["text"] = "Detected Manga Bubble",
                    ["pts"] = new BsonArray { new BsonArray { 10, 10 }, new BsonArray { 50, 50 } }
                }
            }
        };
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", jobObjectId),
            Builders<BsonDocument>.Update.Set("state", "done").Set("result", resultDoc));

        // 5. 查询 success 状态
        var getSuccessRes = await client.GetAsync($"/v1/mit/image-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getSuccessRes.StatusCode);
        var getSuccessJson = await getSuccessRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getSuccessJson.GetProperty("task_id").GetString());
        Assert.Equal("success", getSuccessJson.GetProperty("status").GetString());
        var resultEl = getSuccessJson.GetProperty("result");
        var textlines = resultEl.GetProperty("textlines");
        Assert.Equal("Detected Manga Bubble", textlines[0].GetProperty("text").GetString());

        // 6. 模拟任务失败（failed）
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", jobObjectId),
            Builders<BsonDocument>.Update.Set("state", "failed").Set("error", "CUDA out of memory"));

        // 7. 查询 fail 状态
        var getFailRes = await client.GetAsync($"/v1/mit/image-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getFailRes.StatusCode);
        var getFailJson = await getFailRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getFailJson.GetProperty("task_id").GetString());
        Assert.Equal("fail", getFailJson.GetProperty("status").GetString());
        Assert.Equal("CUDA out of memory", getFailJson.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MitEndpoints_TranslateTask_And_Status_Query_Lifecycle()
    {
        var storageRoot = CreateTempStorageRoot();
        using var app = CreateEnabledApp(storageRoot);
        using var client = app.CreateClient();

        // 1. 提交翻译任务
        var taskPostRes = await client.PostAsJsonAsync("/v1/mit/translate-tasks", new
        {
            query = "こんにちは",
            translator = "gpt3.5",
            target_lang = "CHS"
        });
        Assert.Equal(HttpStatusCode.OK, taskPostRes.StatusCode);
        var taskPostJson = await taskPostRes.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = taskPostJson.GetProperty("task_id").GetString()!;
        Assert.False(string.IsNullOrEmpty(taskId));

        // 2. 查询 pending 状态
        var getPendingRes = await client.GetAsync($"/v1/mit/translate-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getPendingRes.StatusCode);
        var getPendingJson = await getPendingRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getPendingJson.GetProperty("task_id").GetString());
        Assert.Equal("pending", getPendingJson.GetProperty("status").GetString());

        // 3. 模拟任务完成并写入翻译结果
        var jobObjectId = ObjectId.Parse(taskId);
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", jobObjectId),
            Builders<BsonDocument>.Update.Set("state", "done").Set("result", new BsonString("你好")));

        // 4. 查询 success 状态
        var getSuccessRes = await client.GetAsync($"/v1/mit/translate-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getSuccessRes.StatusCode);
        var getSuccessJson = await getSuccessRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getSuccessJson.GetProperty("task_id").GetString());
        Assert.Equal("success", getSuccessJson.GetProperty("status").GetString());
        Assert.Equal("你好", getSuccessJson.GetProperty("result").GetString());
    }

    [Fact]
    public async Task MitEndpoints_UnknownTaskId_ReturnsPendingStatus()
    {
        var storageRoot = CreateTempStorageRoot();
        using var app = CreateEnabledApp(storageRoot);
        using var client = app.CreateClient();

        // 合法格式但不存在的 ObjectId
        var randomId = ObjectId.GenerateNewId().ToString();
        var res1 = await client.GetAsync($"/v1/mit/image-tasks/{randomId}");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var json1 = await res1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(randomId, json1.GetProperty("task_id").GetString());
        Assert.Equal("pending", json1.GetProperty("status").GetString());

        // 任意字符串格式的 ID
        var randomStringId = "celery-task-random-uuid-99999";
        var res2 = await client.GetAsync($"/v1/mit/translate-tasks/{randomStringId}");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var json2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(randomStringId, json2.GetProperty("task_id").GetString());
        Assert.Equal("pending", json2.GetProperty("status").GetString());
    }
}
