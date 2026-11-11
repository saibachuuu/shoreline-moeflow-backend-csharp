using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Modules.Mit;

namespace Shoreline.Api.Features;

/// <summary>
/// Conditional MIT routes copied from Python's <c>manga-image-translator</c>
/// blueprint. The legacy implementation is not part of either backend: Python's
/// <c>app/tasks/mit.py</c> bodies are stubs and the real work lives in an
/// external <c>manga_translator/moeflow_worker.py</c> Celery worker on the
/// <c>mit</c> queue. C# jobs are normally executed by the in-process
/// <see cref="Jobs.JobWorker"/>, which claims registered
/// <see cref="Jobs.IJobHandler.Kind"/> values exclusively, so the routes stay
/// unmapped unless a consumer exists. Two consumers are accepted:
/// <list type="bullet">
/// <item>in-process <see cref="Jobs.IJobHandler"/> implementations for all of
/// <see cref="RequiredJobKinds"/>; or</item>
/// <item>the opt-in Celery bridge (<see cref="Modules.Mit.MitBridgePublisher"/>),
/// enabled by <c>RabbitMq:Enabled=true</c> plus a non-empty <c>RabbitMq:Url</c>,
/// which publishes Celery protocol v2 messages to the durable <c>mit</c> queue
/// and polls <c>celery_taskmeta</c> for results.</item>
/// </list>
/// Without either consumer the routes must fail closed instead of accepting
/// work that can never run.
/// </summary>
public static class MitEndpoints
{
    /// <summary>Job kinds the MIT routes can enqueue.</summary>
    public static IReadOnlyList<string> RequiredJobKinds { get; } =
        ["mit_detect_text", "mit_ocr", "mit_translate"];

    public static void AddMitServices(this IServiceCollection services)
    {
        services.AddScoped<MitService>();
    }

    /// <summary>
    /// Returns the required job kinds that have no registered
    /// <see cref="IJobHandler"/>. An empty result means every queued MIT job
    /// would be claimed by the in-process worker.
    /// </summary>
    public static string[] FindMissingConsumers(IServiceProvider services)
    {
        var registered = services.GetServices<IJobHandler>()
            .Select(handler => handler.Kind)
            .ToHashSet(StringComparer.Ordinal);
        return RequiredJobKinds.Where(kind => !registered.Contains(kind)).ToArray();
    }

    public static void MapMitEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/mit/images", async (
            HttpRequest request,
            MitService mitService,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                throw MitService.UploadFileNotFound();
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                throw MitService.UploadFileNotFound();
            }

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext))
            {
                throw MitService.UploadFileNotFound();
            }

            ext = ext.ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif"))
            {
                throw MitService.UploadFileNotFound();
            }

            var filename = await mitService.SaveUploadedImageAsync(file, ext, ct);
            return Results.Ok(new Dictionary<string, object?> { ["filename"] = filename });
        });

        app.MapPost("/v1/mit/image-tasks", async (
            HttpContext context,
            MitService mitService,
            CancellationToken ct) =>
        {
            var body = await ReadJsonBodyAsync(context.Request, ct);
            if (body.ValueKind != JsonValueKind.Object)
            {
                throw ApiError.Invalid("Invalid request body");
            }

            if (!body.TryGetProperty("filename", out var fileProp) ||
                fileProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(fileProp.GetString()))
            {
                throw ApiError.Invalid("Filename required");
            }

            var filename = fileProp.GetString()!;
            if (!mitService.TryResolveSafePath(filename, out var safePath))
            {
                throw ApiError.Invalid("Invalid filename");
            }

            if (!File.Exists(safePath))
            {
                throw ApiError.Invalid("File not found");
            }

            if (!body.TryGetProperty("task_name", out var taskProp) ||
                taskProp.ValueKind != JsonValueKind.String)
            {
                throw ApiError.Invalid("Invalid task name");
            }

            var taskName = taskProp.GetString()!;
            if (taskName is not ("mit_detect_text" or "mit_ocr"))
            {
                throw ApiError.Invalid("Invalid task name");
            }

            var taskId = await mitService.EnqueueImageTaskAsync(taskName, safePath, body, ct);
            return Results.Ok(new Dictionary<string, object?> { ["task_id"] = taskId });
        });

        app.MapGet("/v1/mit/image-tasks/{taskId}", async (
            string taskId,
            MitService mitService,
            CancellationToken ct) =>
        {
            var status = await mitService.GetTaskStatusAsync(taskId, ct);
            return Results.Ok(status);
        });

        app.MapPost("/v1/mit/translate-tasks", async (
            HttpContext context,
            MitService mitService,
            CancellationToken ct) =>
        {
            var body = await ReadJsonBodyAsync(context.Request, ct);
            if (body.ValueKind != JsonValueKind.Object)
            {
                throw ApiError.Invalid("Invalid request body");
            }

            var taskId = await mitService.EnqueueTranslateTaskAsync(body, ct);
            return Results.Ok(new Dictionary<string, object?> { ["task_id"] = taskId });
        });

        app.MapGet("/v1/mit/translate-tasks/{taskId}", async (
            string taskId,
            MitService mitService,
            CancellationToken ct) =>
        {
            var status = await mitService.GetTaskStatusAsync(taskId, ct);
            return Results.Ok(status);
        });
    }

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw ApiError.Invalid("Empty request body");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("Invalid JSON");
        }
    }
}

public sealed class MitService(
    MongoStore store,
    JobQueue jobs,
    IConfiguration configuration,
    ILogger<MitService> logger,
    MitBridgeSettings bridgeSettings,
    MitBridgePublisher bridgePublisher,
    MitBridgeResultReader bridgeResults)
{
    public static ApiError UploadFileNotFound() =>
        new("UploadFileNotFoundError", 101, "Please select an image file", 400);

    public string GetStorageRoot()
    {
        var root = configuration["Mit:StorageRoot"]
            ?? Environment.GetEnvironmentVariable("MIT_STORAGE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || root == "/MIT_STORAGE_ROOT_UNDEFINED")
        {
            throw new InvalidOperationException("MIT_STORAGE_ROOT is not configured.");
        }
        return root;
    }

    public bool TryResolveSafePath(string filename, [NotNullWhen(true)] out string? safePath)
    {
        safePath = null;
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains('\0'))
        {
            return false;
        }

        try
        {
            var storageRoot = GetStorageRoot();
            var fullRoot = Path.GetFullPath(storageRoot);
            var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;

            string targetPath;
            if (Path.IsPathRooted(filename))
            {
                targetPath = Path.GetFullPath(filename);
            }
            else
            {
                targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, filename));
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!targetPath.StartsWith(normalizedRoot, comparison))
            {
                return false;
            }

            safePath = targetPath;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve path for {Filename}", filename);
            return false;
        }
    }

    public async Task<string> SaveUploadedImageAsync(IFormFile file, string extension, CancellationToken ct)
    {
        var storageRoot = GetStorageRoot();
        Directory.CreateDirectory(storageRoot);

        var safeFileName = $"{Guid.NewGuid():N}{extension}";
        var targetPath = Path.Combine(storageRoot, safeFileName);

        await using var stream = File.Create(targetPath);
        await file.CopyToAsync(stream, ct);
        return safeFileName;
    }

    public async Task<string> EnqueueImageTaskAsync(
        string taskName,
        string imagePath,
        JsonElement taskParams,
        CancellationToken ct)
    {
        var payload = new BsonDocument();
        if (taskName == "mit_detect_text")
        {
            payload["detector_key"] = "default";
            payload["detect_size"] = 2560;
            payload["text_threshold"] = 0.5;
            payload["box_threshold"] = 0.7;
            payload["unclip_ratio"] = 2.3;
            payload["invert"] = false;
            payload["gamma_correct"] = false;
            payload["rotate"] = false;
            payload["verbose"] = true;
        }
        else if (taskName == "mit_ocr")
        {
            payload["ocr_key"] = "48px";
            payload["verbose"] = true;
        }

        foreach (var prop in taskParams.EnumerateObject())
        {
            if (prop.Name is "filename" or "task_name") continue;
            payload[prop.Name] = ConvertJsonElementToBsonValue(prop.Value);
        }

        payload["image_path"] = imagePath;

        if (bridgeSettings.IsBridgeEnabled)
        {
            // 方案 A: Celery 直发。image_path 是位置参数（args[0]），其余
            // 进 kwargs；Python 侧按 `tasks.mit.*` 签名绑定。
            var bridgeArgs = new List<object?> { imagePath };
            var bridgeKwargs = new Dictionary<string, object?>();
            foreach (var element in payload)
            {
                if (element.Name == "image_path") continue;
                bridgeKwargs[element.Name] = MitBridgeBson.ToNetObject(element.Value);
            }
            return await bridgePublisher.PublishAsync(taskName, bridgeArgs, bridgeKwargs, ct);
        }

        var jobId = await jobs.EnqueueAsync(taskName, payload, ct);
        return jobId.ToString();
    }

    public async Task<string> EnqueueTranslateTaskAsync(JsonElement taskParams, CancellationToken ct)
    {
        var payload = new BsonDocument();
        foreach (var prop in taskParams.EnumerateObject())
        {
            payload[prop.Name] = ConvertJsonElementToBsonValue(prop.Value);
        }

        if (bridgeSettings.IsBridgeEnabled)
        {
            // 方案 A: translate 是纯 kwargs 任务（spike §1.2），args 为空。
            var bridgeKwargs = new Dictionary<string, object?>();
            foreach (var element in payload)
            {
                bridgeKwargs[element.Name] = MitBridgeBson.ToNetObject(element.Value);
            }
            return await bridgePublisher.PublishAsync(
                MitBridgeEnvelope.TranslateKind, [], bridgeKwargs, ct);
        }

        var jobId = await jobs.EnqueueAsync("mit_translate", payload, ct);
        return jobId.ToString();
    }

    public async Task<Dictionary<string, object?>> GetTaskStatusAsync(string taskId, CancellationToken ct)
    {
        // 方案 A: 结果以 celery_taskmeta 为准（worker 的 MongoBackend 回写）；
        // 1 秒级短轮询语义保持：查一次即返，无结果按 pending 处理。
        if (bridgeSettings.IsBridgeEnabled)
        {
            return await bridgeResults.GetStatusAsync(taskId, ct);
        }

        if (!ObjectId.TryParse(taskId, out var objectId))
        {
            return new Dictionary<string, object?>
            {
                ["task_id"] = taskId,
                ["status"] = "pending"
            };
        }

        var job = await store.Collection("csharp_job")
            .Find(new BsonDocument("_id", objectId))
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            return new Dictionary<string, object?>
            {
                ["task_id"] = taskId,
                ["status"] = "pending"
            };
        }

        var state = job.GetValue("state", "pending").AsString;
        var res = new Dictionary<string, object?>
        {
            ["task_id"] = taskId
        };

        switch (state)
        {
            case "done":
                res["status"] = "success";
                res["result"] = job.Contains("result")
                    ? ConvertBsonValueToNetObject(job["result"])
                    : new Dictionary<string, object?>();
                break;
            case "failed":
                res["status"] = "fail";
                res["message"] = job.Contains("error")
                    ? job["error"].AsString
                    : (job.Contains("message") ? job["message"].AsString : "Task failed");
                break;
            default:
                res["status"] = "pending";
                break;
        }

        return res;
    }

    private static BsonValue ConvertJsonElementToBsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new BsonString(element.GetString() ?? string.Empty),
        JsonValueKind.Number => element.TryGetInt64(out var l)
            ? (l is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)l) : new BsonInt64(l))
            : new BsonDouble(element.GetDouble()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        JsonValueKind.Null or JsonValueKind.Undefined => BsonNull.Value,
        JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(ConvertJsonElementToBsonValue)),
        JsonValueKind.Object => ConvertJsonObjectToBsonDocument(element),
        _ => BsonNull.Value
    };

    private static BsonDocument ConvertJsonObjectToBsonDocument(JsonElement element)
    {
        var doc = new BsonDocument();
        foreach (var prop in element.EnumerateObject())
        {
            doc[prop.Name] = ConvertJsonElementToBsonValue(prop.Value);
        }
        return doc;
    }

    private static object? ConvertBsonValueToNetObject(BsonValue value) =>
        MitBridgeBson.ToNetObject(value);
}
