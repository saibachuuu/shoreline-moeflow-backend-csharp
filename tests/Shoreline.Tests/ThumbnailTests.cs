using ImageMagick;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;
using Shoreline.Imaging;

namespace Shoreline.Tests;

[Collection("local-services-thumbnail")]
public sealed class ThumbnailTests(LocalServicesWithoutWorker services) : IDisposable
{
    private readonly List<string> _tempRoots = [];
    private readonly CancellationToken ct = CancellationToken.None;
    private readonly string _run = Guid.NewGuid().ToString("N");

    private string Save(string name) => $"{_run}-{name}";
    private static string CoverKey(string saveName) =>
        $"files/cover-{Path.GetFileNameWithoutExtension(saveName)}.webp";
    private static string ResampleKey(string saveName) =>
        $"files/resample-{Path.GetFileNameWithoutExtension(saveName)}.webp";

    [Fact]
    public void Processor_Jpeg_ProducesCoverAndProportionalResample()
    {
        var root = TempRoot();
        var source = Path.Combine(root, "wide.jpg");
        File.WriteAllBytes(source, ImageBytes(2400, 1200, MagickFormat.Jpeg));

        var result = new ImageProcessor().Process(source, Path.Combine(root, "out"), ct);

        using var cover = new MagickImage(result.CoverPath);
        using var resample = new MagickImage(result.ResamplePath);
        Assert.Equal(MagickFormat.WebP, cover.Format);
        Assert.Equal(180u, cover.Width);
        Assert.Equal(140u, cover.Height);
        Assert.Equal(1920u, resample.Width);
        Assert.Equal(960u, resample.Height);
        Assert.True(result.CoverBytes > 0);
        Assert.True(result.ResampleBytes > 0);
        Assert.Equal(180, result.CoverWidth);
        Assert.Equal(140, result.CoverHeight);
        Assert.Equal(1920, result.ResampleWidth);
        Assert.Equal(960, result.ResampleHeight);
    }

    [Fact]
    public void Processor_SmallPng_KeepsOriginalResampleDimensionsAndRemovesAlpha()
    {
        var root = TempRoot();
        var source = Path.Combine(root, "small-alpha.png");
        File.WriteAllBytes(source, ImageBytes(800, 600, MagickFormat.Png, alpha: true));

        var result = new ImageProcessor().Process(source, Path.Combine(root, "out"), ct);

        using var cover = new MagickImage(result.CoverPath);
        using var resample = new MagickImage(result.ResamplePath);
        Assert.Equal(180u, cover.Width);
        Assert.Equal(140u, cover.Height);
        Assert.Equal(800u, resample.Width);
        Assert.Equal(600u, resample.Height);
        Assert.False(cover.HasAlpha);
        Assert.False(resample.HasAlpha);
    }

    [Fact]
    public void Processor_RejectsSvgAndOversizedSource()
    {
        var root = TempRoot();
        var svg = Path.Combine(root, "source.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"></svg>");

        var unsupported = Assert.Throws<ImageProcessingException>(
            () => new ImageProcessor().Process(svg, Path.Combine(root, "out"), ct));
        Assert.Contains(unsupported.Failure,
            new[] { ImageProcessingFailure.FormatNotSupported, ImageProcessingFailure.DecodeFailed });

        var jpeg = Path.Combine(root, "byte-limit.jpg");
        File.WriteAllBytes(jpeg, ImageBytes(100, 100, MagickFormat.Jpeg));
        var tooLarge = Assert.Throws<ImageProcessingException>(
            () => new ImageProcessor(new ImageProcessingOptions { MaxSourceBytes = 8 })
                .Process(jpeg, Path.Combine(root, "out2"), ct));
        Assert.Equal(ImageProcessingFailure.SourceTooLarge, tooLarge.Failure);
    }

    [Fact]
    public async Task Schedule_EnqueuesDurableJobBeforeAdvertisingQueuing()
    {
        var project = await ProjectAsync();
        var save = Save("schedule.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        var service = Service(storage);

        Assert.True(await service.ScheduleAsync(file, project, null, ct));

        var row = await LoadFileAsync(file);
        var job = await LatestJobAsync(file);
        var payload = job["payload"].AsBsonDocument;
        Assert.Equal(ThumbnailService.ThumbnailQueuing, row.Integer("th"));
        Assert.Equal(string.Empty, row.Text("the"));
        Assert.False(row.Contains("thumbnail_run"));
        Assert.Equal("pending", job.Text("state"));
        Assert.Equal(0, job.Integer("attempts"));
        Assert.Equal("create_thumbnail", job.Text("kind"));
        Assert.Equal(file.ToString(), payload.Text("file_id"));
        Assert.Equal(project.ToString(), payload.Text("project_id"));
        Assert.Equal(0L, payload["label_revision"].ToInt64());
        Assert.Equal(save, payload.Text("save_name"));
        Assert.Equal(32, payload.Text("run_id").Length);
    }

    [Fact]
    public async Task Run_GeneratesWebpObjectsAndUpdatesBytesAndProject()
    {
        var project = await ProjectAsync();
        var save = Save("generated.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(2400, 1200, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        var row = await LoadFileAsync(file);
        var projectRow = await LoadProjectAsync(project);
        Assert.Equal(ThumbnailService.ThumbnailSucceeded, row.Integer("th"));
        Assert.Equal(string.Empty, row.Text("the"));
        Assert.True(row.Integer("cover_bytes") > 0);
        Assert.True(row.Integer("resample_bytes") > 0);
        Assert.Equal(row.Integer("cover_bytes"), projectRow.Integer("cover_bytes"));
        Assert.Equal(row.Integer("resample_bytes"), projectRow.Integer("resample_bytes"));
        Assert.Equal(
            (long)Math.Round(
                (row.Integer("cover_bytes") + row.Integer("resample_bytes")) / 1024d,
                MidpointRounding.ToEven),
            projectRow.Integer("fs"));

        var coverKey = CoverKey(save);
        var resampleKey = ResampleKey(save);
        Assert.NotNull(await storage.HeadAsync(coverKey, ct));
        Assert.NotNull(await storage.HeadAsync(resampleKey, ct));
        using var downloaded = await storage.GetAsync(coverKey, ct);
        using var cover = new MagickImage(downloaded.Content);
        Assert.Equal(180u, cover.Width);
        Assert.Equal(140u, cover.Height);
        Assert.Null(await storage.HeadAsync($"files/.thumbnail-staging/{payload.Text("run_id")}/cover.webp", ct));
    }

    [Fact]
    public async Task Run_PooledProject_ReadsSourceAndPublishesToPoolBackend()
    {
        var sourceId = await InsertStorageSourceAsync();
        var project = await ProjectAsync(csid: sourceId.ToString());
        var save = Save("pooled-generated.jpg");
        var file = await FileAsync(project, save);
        var router = services.App.Services.GetRequiredService<ProjectStorageRouter>();
        var projectDocument = new BsonDocument { ["_id"] = project, ["csid"] = sourceId };

        await using (var lease = await router.ForOriginalAsync(projectDocument, ct))
        {
            await PutAsync(lease.Backend, $"files/{save}",
                ImageBytes(2400, 1200, MagickFormat.Jpeg));
        }

        var service = Service(Storage());
        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        Assert.Equal(ThumbnailService.ThumbnailSucceeded, (await LoadFileAsync(file)).Integer("th"));
        var coverKey = CoverKey(save);
        var resampleKey = ResampleKey(save);
        Assert.Null(await Storage().HeadAsync(coverKey, ct));
        Assert.Null(await Storage().HeadAsync(resampleKey, ct));
        await using (var lease = await router.ForOriginalAsync(projectDocument, ct))
        {
            Assert.NotNull(await lease.Backend.HeadAsync(coverKey, ct));
            Assert.NotNull(await lease.Backend.HeadAsync(resampleKey, ct));
            Assert.Null(await lease.Backend.HeadAsync(
                $"files/.thumbnail-staging/{payload.Text("run_id")}/cover.webp", ct));
        }
    }

    [Fact]
    public async Task Run_SmallImage_LeavesResampleAtOriginalSize()
    {
        var project = await ProjectAsync();
        var save = Save("small.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(800, 600, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        using var downloaded = await storage.GetAsync(ResampleKey(save), ct);
        using var resample = new MagickImage(downloaded.Content);
        Assert.Equal(800u, resample.Width);
        Assert.Equal(600u, resample.Height);
    }

    [Fact]
    public async Task Run_MissingSource_MarksTerminalFailureAndCleansTemp()
    {
        var project = await ProjectAsync();
        var file = await FileAsync(project, "missing.jpg");
        var service = Service(Storage(), out var tempRoot);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        var row = await LoadFileAsync(file);
        Assert.Equal(ThumbnailService.ThumbnailFailed, row.Integer("th"));
        Assert.Equal(ThumbnailService.SourceMissingError, row.Text("the"));
        Assert.False(Directory.Exists(Path.Combine(tempRoot, payload.Text("run_id"))));
    }

    [Fact]
    public async Task Run_StaleRevision_DoesNotPublish()
    {
        var project = await ProjectAsync();
        var save = Save("stale.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await services.Store.Collection("project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", project),
            Builders<BsonDocument>.Update.Set("label_revision", 1),
            cancellationToken: ct);
        await service.RunAsync(payload, 1, ct);

        Assert.Equal(ThumbnailService.ThumbnailQueuing, (await LoadFileAsync(file)).Integer("th"));
        Assert.Null(await storage.HeadAsync(CoverKey(save), ct));
    }

    [Fact]
    public async Task Run_ReuploadedSaveName_DoesNotPublishOldObjects()
    {
        var project = await ProjectAsync();
        var save = Save("reupload.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", file),
            Builders<BsonDocument>.Update.Set("sa", Save("reupload-v2.jpg")),
            cancellationToken: ct);
        await service.RunAsync(payload, 1, ct);

        Assert.Equal(ThumbnailService.ThumbnailQueuing, (await LoadFileAsync(file)).Integer("th"));
        Assert.Null(await storage.HeadAsync(CoverKey(save), ct));
    }

    [Fact]
    public async Task Run_DuplicateWorker_SkipsSucceededRun()
    {
        var project = await ProjectAsync();
        var save = Save("duplicate.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);
        var first = await LoadFileAsync(file);
        await service.RunAsync(payload, 2, ct);
        var second = await LoadFileAsync(file);

        Assert.Equal(ThumbnailService.ThumbnailSucceeded, second.Integer("th"));
        Assert.Equal(first.Integer("cover_bytes"), second.Integer("cover_bytes"));
        Assert.Equal(first.Integer("resample_bytes"), second.Integer("resample_bytes"));
    }

    [Fact]
    public async Task Run_RecomputesProjectTotalsFromFilesInsteadOfDoubleCounting()
    {
        var project = await ProjectAsync(coverBytes: 900, resampleBytes: 1700, fs: 3);
        var save = Save("crash.jpg");
        var file = await FileAsync(project, save, coverBytes: 1000, resampleBytes: 2000);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(2400, 1000, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        var row = await LoadFileAsync(file);
        var projectRow = await LoadProjectAsync(project);
        Assert.Equal(row.Integer("cover_bytes"), projectRow.Integer("cover_bytes"));
        Assert.Equal(row.Integer("resample_bytes"), projectRow.Integer("resample_bytes"));
        Assert.Equal(
            (long)Math.Round(
                (row.Integer("cover_bytes") + row.Integer("resample_bytes")) / 1024d,
                MidpointRounding.ToEven),
            projectRow.Integer("fs"));
    }

    [Fact]
    public async Task Schedule_UnknownStorageSource_FailsClosedWithoutQueuing()
    {
        var project = await ProjectAsync(csid: "pool-1");
        var file = await FileAsync(project, "pool.jpg");
        var service = Service(Storage());

        var error = await Assert.ThrowsAsync<ApiError>(
            () => service.ScheduleAsync(file, project, null, ct));
        Assert.Equal(6, error.Code);
        Assert.Equal(409, error.Status);
        Assert.Equal("Storage source is unavailable", error.Message);
        Assert.Equal(ThumbnailService.ThumbnailUnknown, (await LoadFileAsync(file)).Integer("th"));
    }

    [Fact]
    public async Task Schedule_NonImage_FailsWithLegacyTypeMessage()
    {
        var project = await ProjectAsync();
        var file = await FileAsync(project, "text.txt", type: 3);
        var service = Service(Storage());

        var error = await Assert.ThrowsAsync<ApiError>(
            () => service.ScheduleAsync(file, project, null, ct));
        Assert.Equal(108, error.Code);
        Assert.Equal("文件类型不支持此操作: 仅图片文件可生成缩略图", error.Message);
        Assert.Equal(ThumbnailService.ThumbnailUnknown, (await LoadFileAsync(file)).Integer("th"));
    }

    [Fact]
    public async Task ScheduleProject_QueuesOnlyActivatedImages()
    {
        var project = await ProjectAsync();
        var active = await FileAsync(project, Save("active.jpg"));
        var inactive = await FileAsync(project, Save("inactive.jpg"), activated: false);
        var text = await FileAsync(project, Save("note.txt"), type: 3);
        var service = Service(Storage());

        var count = await service.ScheduleProjectAsync(project, ct);

        Assert.Equal(1, count);
        Assert.Equal(ThumbnailService.ThumbnailQueuing, (await LoadFileAsync(active)).Integer("th"));
        Assert.Equal(ThumbnailService.ThumbnailUnknown, (await LoadFileAsync(inactive)).Integer("th"));
        Assert.Equal(ThumbnailService.ThumbnailUnknown, (await LoadFileAsync(text)).Integer("th"));
        Assert.Equal(1, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.project_id"] = project.ToString()
            }));
    }

    [Fact]
    public async Task LocalStorage_EndToEnd_WritesFinalObjectsUnderRoot()
    {
        var root = TempRoot();
        var config = LocalConfig(root);
        using var storage = new ObjectStorage(config);
        var save = Save("local.jpg");
        await PutAsync(storage, $"files/{save}", ImageBytes(2400, 1200, MagickFormat.Jpeg));
        var project = await ProjectAsync();
        var file = await FileAsync(project, save);
        var service = Service(storage, config, TempRoot());

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await service.RunAsync(payload, 1, ct);

        var cover = Path.Combine(root, "files", $"cover-{Path.GetFileNameWithoutExtension(save)}.webp");
        var resample = Path.Combine(root, "files", $"resample-{Path.GetFileNameWithoutExtension(save)}.webp");
        Assert.True(File.Exists(cover));
        Assert.True(File.Exists(resample));
        Assert.Equal(ThumbnailService.ThumbnailSucceeded, (await LoadFileAsync(file)).Integer("th"));
    }

    [Fact]
    public async Task OutputStorageFailure_LeavesGeneratingThenTerminalAtAttemptLimit()
    {
        var root = TempRoot();
        var config = LocalConfig(root);
        using var storage = new ObjectStorage(config);
        var save = Save("blocked.jpg");
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        Directory.CreateDirectory(Path.Combine(root, "files",
            $"cover-{Path.GetFileNameWithoutExtension(save)}.webp"));
        var project = await ProjectAsync();
        var file = await FileAsync(project, save);
        var service = Service(storage, config, TempRoot(), maxAttempts: 4);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RunAsync(payload, 1, ct));
        Assert.Equal(ThumbnailService.ThumbnailGenerating, (await LoadFileAsync(file)).Integer("th"));

        await service.RunAsync(payload, 4, ct);
        var row = await LoadFileAsync(file);
        Assert.Equal(ThumbnailService.ThumbnailFailed, row.Integer("th"));
        Assert.Equal(ThumbnailService.TransientFailureError, row.Text("the"));
    }

    [Fact]
    public async Task JobHandler_PassesAttemptsContextToThumbnailService()
    {
        var root = TempRoot();
        var config = LocalConfig(root);
        using var storage = new ObjectStorage(config);
        var save = Save("handler_blocked.jpg");
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        // Block cover upload so it triggers transient error handling
        Directory.CreateDirectory(Path.Combine(root, "files",
            $"cover-{Path.GetFileNameWithoutExtension(save)}.webp"));
        var project = await ProjectAsync();
        var file = await FileAsync(project, save);
        var service = Service(storage, config, TempRoot(), maxAttempts: 4);
        var payload = await ScheduleAndPayloadAsync(service, file, project);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(service);
        var provider = serviceCollection.BuildServiceProvider();
        var handler = new ThumbnailJobHandler(provider.GetRequiredService<IServiceScopeFactory>());

        // Execute via IJobHandler overload with attempts = 4
        var jobId = ObjectId.GenerateNewId();
        await handler.ExecuteAsync(jobId, 4, payload, ct);

        var row = await LoadFileAsync(file);
        Assert.Equal(ThumbnailService.ThumbnailFailed, row.Integer("th"));
        Assert.Equal(ThumbnailService.TransientFailureError, row.Text("the"));
    }

    [Fact]
    public async Task CanceledRun_DoesNotPublish()
    {
        var project = await ProjectAsync();
        var save = Save("canceled.jpg");
        var file = await FileAsync(project, save);
        var storage = Storage();
        await PutAsync(storage, $"files/{save}", ImageBytes(300, 200, MagickFormat.Jpeg));
        var service = Service(storage);

        var payload = await ScheduleAndPayloadAsync(service, file, project);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunAsync(payload, 1, canceled.Token));

        Assert.Equal(ThumbnailService.ThumbnailQueuing, (await LoadFileAsync(file)).Integer("th"));
        Assert.Null(await storage.HeadAsync(CoverKey(save), ct));
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- fixtures

    private ThumbnailService Service(
        ObjectStorage storage,
        IConfiguration? baseConfiguration = null,
        string? tempRoot = null,
        int maxAttempts = 4)
        => Service(storage, out _, baseConfiguration, tempRoot, maxAttempts);

    private ThumbnailService Service(
        ObjectStorage storage,
        out string tempRoot,
        IConfiguration? baseConfiguration = null,
        string? requestedTempRoot = null,
        int maxAttempts = 4)
    {
        tempRoot = requestedTempRoot ?? TempRoot();
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration ?? services.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Thumbnails:TempRoot"] = tempRoot,
                ["Thumbnails:MaxAttempts"] = maxAttempts.ToString(),
                ["Thumbnails:RunLeaseMinutes"] = "5"
            })
            .Build();
        var clock = TimeProvider.System;
        return new ThumbnailService(
            services.Store,
            storage,
            new JobQueue(services.Store, clock),
            new ProjectLabelLock(services.Store, clock),
            new LabelRequestContext(),
            clock,
            configuration,
            new ImageProcessor(),
            router: services.App.Services.GetRequiredService<ProjectStorageRouter>());
    }

    private ObjectStorage Storage() => services.App.Services.GetRequiredService<ObjectStorage>();

    private async Task<ObjectId> ProjectAsync(
        string? csid = null,
        long coverBytes = 0,
        long resampleBytes = 0,
        long fs = 0,
        long revision = 0)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "thumbnail-project-" + id.ToString(),
            ["st"] = 0,
            ["label_revision"] = revision,
            ["cover_bytes"] = new BsonInt64(coverBytes),
            ["resample_bytes"] = new BsonInt64(resampleBytes),
            ["fs"] = new BsonInt64(fs)
        };
        if (csid is not null) document["csid"] = csid;
        await services.Store.Collection("project").InsertOneAsync(document, cancellationToken: ct);
        return id;
    }

    private async Task<ObjectId> FileAsync(
        ObjectId project,
        string saveName,
        int type = 2,
        bool activated = true,
        long coverBytes = 0,
        long resampleBytes = 0)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
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
            ["cover_bytes"] = new BsonInt64(coverBytes),
            ["resample_bytes"] = new BsonInt64(resampleBytes),
            ["label_epoch"] = 0L
        };
        await services.Store.Collection("file").InsertOneAsync(document, cancellationToken: ct);
        return id;
    }

    private async Task<BsonDocument> LoadFileAsync(ObjectId id)
        => await services.Store.Collection("file")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstAsync(ct);

    private async Task<BsonDocument> LoadProjectAsync(ObjectId id)
        => await services.Store.Collection("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstAsync(ct);

    private async Task<BsonDocument> LatestJobAsync(ObjectId file)
        => await services.Store.Collection("csharp_job")
            .Find(new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = file.ToString()
            })
            .Sort(Builders<BsonDocument>.Sort.Descending("created_at"))
            .FirstAsync(ct);

    private async Task<BsonDocument> ScheduleAndPayloadAsync(
        ThumbnailService service,
        ObjectId file,
        ObjectId project)
    {
        Assert.True(await service.ScheduleAsync(file, project, null, ct));
        return (await LatestJobAsync(file))["payload"].AsBsonDocument;
    }

    private static async Task PutAsync(ObjectStorage storage, string key, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        await storage.PutAsync(key, stream, "application/octet-stream", CancellationToken.None);
    }

    private static async Task PutAsync(IStorageBackend storage, string key, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        await storage.PutAsync(key, stream, "application/octet-stream", CancellationToken.None);
    }

    private async Task<ObjectId> InsertStorageSourceAsync()
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await services.Store.Collection("storage_pool_source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "thumbnail-source-" + id,
            ["e"] = true,
            ["ep"] = services.Configuration["Storage:Endpoint"] ?? "http://127.0.0.1:29128",
            ["b"] = services.Configuration["Storage:Bucket"] ?? "shoreline-test",
            ["sp"] = "thumbnail-pool/" + Guid.NewGuid().ToString("N") + "/",
            ["r"] = "us-east-1",
            ["as"] = "path",
            ["vs"] = true,
            ["ak"] = services.Configuration["Storage:AccessKey"] ?? "local-test-only",
            ["sk"] = services.Configuration["Storage:SecretKey"] ?? "local-disposable-test-secret",
            ["pd"] = "",
            ["rm"] = "",
            ["iad"] = 90,
            ["mo"] = 2000,
            ["hd"] = false,
            ["ct"] = now,
            ["ut"] = now,
        }, cancellationToken: ct);
        return id;
    }

    private string TempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "shoreline-g4-thumbnail-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static IConfiguration LocalConfig(string root)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = "local",
                ["Storage:Root"] = root
            })
            .Build();

    private static byte[] ImageBytes(
        int width,
        int height,
        MagickFormat format,
        bool alpha = false)
    {
        using var image = new MagickImage(MagickColors.SeaGreen, (uint)width, (uint)height);
        if (alpha)
        {
            image.Alpha(AlphaAction.Activate);
        }

        image.Format = format;
        if (format == MagickFormat.Jpeg)
        {
            image.Quality = 90;
        }

        return image.ToByteArray();
    }
}
