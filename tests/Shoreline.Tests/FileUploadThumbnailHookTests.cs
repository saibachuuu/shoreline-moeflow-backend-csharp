using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;
using Shoreline.Imaging;

namespace Shoreline.Tests;

[CollectionDefinition("local-services-upload-thumbnail")]
public sealed class UploadThumbnailServicesCollection : ICollectionFixture<LocalServicesWithoutWorker>;

/// <summary>
/// D1 upload -> G4 thumbnail scheduling parity: Python
/// File.upload_real_file queues local thumbnails for every image upload
/// (app/models/file.py:867-891). The request must not generate Pillow work
/// inline and must leave an honest status when scheduling fails.
/// </summary>
[Collection("local-services-upload-thumbnail")]
public sealed class FileUploadThumbnailHookTests(LocalServicesWithoutWorker services)
{
    private readonly CancellationToken ct = CancellationToken.None;

    [Fact]
    public async Task ImageUpload_QueuesLocalThumbnailAfterMetadataPersisted()
    {
        var clock = new TestClock();
        var context = new LabelRequestContext();
        var locks = new ProjectLabelLock(services.Store, clock);
        var thumbnails = Thumbnails(clock, locks, context);
        var project = await ProjectAsync();
        var files = Files(clock, locks, context, thumbnails);

        var data = await files.UploadAsync(project, "upload.png", Form("upload.png", [1, 2, 3, 4]),
            null, ct);

        var file = await services.Store.Collection("file")
            .Find(new BsonDocument
            {
                ["p"] = project["_id"],
                ["n"] = "upload.png"
            }).FirstAsync(ct);
        Assert.Equal(data["id"], file["_id"].AsObjectId.ToString());
        Assert.Equal(ThumbnailService.ThumbnailQueuing, file.Integer("th"));
        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument
            {
                ["kind"] = ThumbnailService.JobKind,
                ["payload.file_id"] = file["_id"].AsObjectId.ToString()
            }).FirstAsync(ct);
        Assert.Equal("pending", job.Text("state"));
        Assert.Equal(file.Text("sa"), job["payload"].AsBsonDocument.Text("save_name"));
    }

    [Fact]
    public async Task ImageUpload_SchedulerFailure_DoesNotFailOrFakeQueuing()
    {
        var clock = new TestClock();
        var context = new LabelRequestContext();
        var locks = new ProjectLabelLock(services.Store, clock);
        var project = await ProjectAsync();
        var files = Files(clock, locks, context, new FailingScheduler());

        var data = await files.UploadAsync(project, "broken.png", Form("broken.png", [1, 2, 3]),
            null, ct);

        var file = await services.Store.Collection("file")
            .Find(new BsonDocument { ["p"] = project["_id"], ["n"] = "broken.png" }).FirstAsync(ct);
        Assert.Equal(data["id"], file["_id"].AsObjectId.ToString());
        Assert.Equal(ThumbnailService.ThumbnailUnknown, file.Integer("th"));
        Assert.NotNull(await services.App.Services.GetRequiredService<ObjectStorage>()
            .HeadAsync(FileService.FilePrefix + file.Text("sa"), ct));
    }

    private FileService Files(
        TestClock clock,
        ProjectLabelLock locks,
        LabelRequestContext context,
        IThumbnailScheduler scheduler) => new(
        services.Store,
        services.App.Services.GetRequiredService<ProjectService>(),
        services.App.Services.GetRequiredService<IdentityPermissions>(),
        services.App.Services.GetRequiredService<ObjectStorage>(),
        services.Configuration,
        clock,
        new FileMutationGate(),
        null,
        locks,
        context,
        services.App.Services.GetRequiredService<ProjectStorageRouter>(),
        scheduler);

    private ThumbnailService Thumbnails(
        TestClock clock,
        ProjectLabelLock locks,
        LabelRequestContext context) => new(
        services.Store,
        services.App.Services.GetRequiredService<ObjectStorage>(),
        new JobQueue(services.Store, clock),
        locks,
        context,
        clock,
        services.Configuration,
        new ImageProcessor(),
        router: services.App.Services.GetRequiredService<ProjectStorageRouter>());

    private async Task<BsonDocument> ProjectAsync()
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "upload-project-" + id,
            ["ns"] = "upload-project-" + id,
            ["t"] = ObjectId.GenerateNewId(),
            ["st"] = 0,
            ["label_revision"] = 0L,
            ["fo"] = 0,
            ["fc"] = 0,
            ["fs"] = new BsonInt64(0),
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["m_e"] = DateTime.UtcNow,
            ["dr"] = 1,
            ["m_a"] = 1,
            ["m_t"] = 1
        };
        await services.Store.Collection("project").InsertOneAsync(document, cancellationToken: ct);
        return document;
    }

    private static IFormFile Form(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

    private sealed class FailingScheduler : IThumbnailScheduler
    {
        public Task<bool> ScheduleAsync(ObjectId fileId, ObjectId projectId,
            long? expectedLabelRevision, CancellationToken ct) =>
            throw new InvalidOperationException("queue unavailable");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
