using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

/// <summary>
/// ThumbnailStatusReconciler rewrites stalled thumbnail rows off the request
/// path. These tests await the reconciler directly (it is internal-visible);
/// the fire-and-forget trigger only wraps it.
/// </summary>
[Collection("local-services-thumbnail")]
public sealed class ThumbnailReconcilerTests(LocalServicesWithoutWorker services)
{
    private readonly CancellationToken ct = CancellationToken.None;
    private readonly string _run = Guid.NewGuid().ToString("N");

    private async Task<BsonDocument> InsertRowAsync(
        ObjectId projectId, string saveName, int status, DateTime? thumbnailTime, string error = "")
    {
        var row = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = saveName,
            ["t"] = FileService.TypeImage,
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["sa"] = saveName,
            ["th"] = status,
            ["the"] = error,
        };
        if (thumbnailTime is not null)
        {
            row["tht"] = thumbnailTime.Value;
        }

        await services.Store.Collection("file").InsertOneAsync(row, cancellationToken: ct);
        return row;
    }

    private async Task<BsonDocument> LoadRowAsync(ObjectId fileId) =>
        await services.Store.Collection("file").Find(Builders<BsonDocument>.Filter.Eq("_id", fileId))
            .FirstAsync(ct);

    private async Task PutObjectAsync(string key)
    {
        var storage = services.App.Services.GetRequiredService<ObjectStorage>();
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        await storage.PutAsync(key, content, "image/webp", ct);
    }

    private ThumbnailStatusReconciler Reconciler() => new(
        services.App.Services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System);

    [Fact]
    public async Task Reconcile_PromotesStaleRowWhenAnObjectExists()
    {
        var projectId = ObjectId.GenerateNewId();
        var saveName = $"{_run}-a.jpg";
        var row = await InsertRowAsync(
            projectId, saveName, ThumbnailService.ThumbnailGenerating, DateTime.UtcNow.AddHours(-1));
        // The reconciler must probe the default storage for the cover.
        await PutObjectAsync($"files/cover-{_run}-a.webp");

        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, saveName, null);

        var after = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(ThumbnailService.ThumbnailSucceeded, after.Integer("th"));
        Assert.False(after.Contains("the"));
        Assert.False(after.Flag("thk"));
        Assert.True(after.Contains("tht"));
    }

    [Fact]
    public async Task Reconcile_FailsStaleRowOnceWhenObjectsAreMissing()
    {
        var projectId = ObjectId.GenerateNewId();
        var saveName = $"{_run}-b.jpg";
        var row = await InsertRowAsync(
            projectId, saveName, ThumbnailService.ThumbnailGenerating, DateTime.UtcNow.AddHours(-1));

        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, saveName, null);

        var first = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(ThumbnailService.ThumbnailFailed, first.Integer("th"));
        Assert.Equal(ThumbnailStatusReconciler.ReconcileFailureMessage, first.Text("the"));
        Assert.True(first.Flag("thk"));
        Assert.True(first.Contains("tht"));
        var settledAt = first["tht"].ToUniversalTime();

        // The probe-off flag makes the second pass a no-op: the row keeps its
        // settled timestamp instead of being probed on every list request.
        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, saveName, null);
        var second = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(settledAt, second["tht"].ToUniversalTime());
        Assert.Equal(ThumbnailService.ThumbnailFailed, second.Integer("th"));
    }

    [Fact]
    public async Task Reconcile_SkipsRowsTouchedByALiveWorker()
    {
        var projectId = ObjectId.GenerateNewId();
        var saveName = $"{_run}-c.jpg";
        var row = await InsertRowAsync(
            projectId, saveName, ThumbnailService.ThumbnailGenerating, DateTime.UtcNow.AddMinutes(-1));

        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, saveName, null);

        var after = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(ThumbnailService.ThumbnailGenerating, after.Integer("th"));
        Assert.False(after.Contains("thk"));
    }

    [Fact]
    public async Task Reconcile_SkipsRowsReplacedByAReupload()
    {
        var projectId = ObjectId.GenerateNewId();
        var saveName = $"{_run}-d.jpg";
        var row = await InsertRowAsync(
            projectId, saveName, ThumbnailService.ThumbnailGenerating, DateTime.UtcNow.AddHours(-1));

        // The trigger snapshot names the pre-reupload save_name.
        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, $"{_run}-old.jpg", null);

        var after = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(ThumbnailService.ThumbnailGenerating, after.Integer("th"));
        Assert.False(after.Contains("thk"));
    }

    [Fact]
    public async Task Reconcile_MarksWorkerFailedRowWithoutTouchingItsReason()
    {
        var projectId = ObjectId.GenerateNewId();
        var saveName = $"{_run}-e.jpg";
        var row = await InsertRowAsync(
            projectId, saveName, ThumbnailService.ThumbnailFailed,
            DateTime.UtcNow.AddHours(-2), error: "worker explosion");

        await Reconciler().ReconcileAsync(row["_id"].AsObjectId, projectId, saveName, null);

        var after = await LoadRowAsync(row["_id"].AsObjectId);
        Assert.Equal(ThumbnailService.ThumbnailFailed, after.Integer("th"));
        Assert.Equal("worker explosion", after.Text("the"));
        Assert.True(after.Flag("thk"));
    }
}
