using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.StoragePool;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class StoragePoolTests(LocalServices services)
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private StoragePoolRuntime Runtime() => new(services);

    [Fact]
    public async Task SourceCreateProbesBeforePersistSoUnreachableEndpointIsNotSaved()
    {
        using var runtime = Runtime();
        var name = "unreachable-" + Guid.NewGuid().ToString("N");
        var data = Json($$"""
            {"name":"{{name}}","endpoint":"http://127.0.0.1:1","bucket":"shoreline-test",
             "sub_path":"probe/","access_key_id":"local-test-only","secret_access_key":"local-disposable-test-secret"}
            """);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Sources.CreateAsync(data, ObjectId.GenerateNewId(), CancellationToken.None));
        Assert.Equal(4, error.Code);
        Assert.False(await runtime.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("n", name)).AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SourceVerifySslFalseIsRejectedBeforeProbeAndPersist()
    {
        using var runtime = Runtime();
        var name = "verify-" + Guid.NewGuid().ToString("N");
        var data = Json($$"""
            {"name":"{{name}}","endpoint":"http://127.0.0.1:{{TestEnvironment.S3Port}}","bucket":"shoreline-test",
             "sub_path":"probe/","verify_ssl":false,"access_key_id":"local-test-only",
             "secret_access_key":"local-disposable-test-secret"}
            """);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Sources.CreateAsync(data, ObjectId.GenerateNewId(), CancellationToken.None));
        Assert.Equal(4, error.Code);
        Assert.False(await runtime.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("n", name)).AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SourceUpdateBlankCredentialsPreservesStoredSecret()
    {
        using var runtime = Runtime();
        var id = await runtime.InsertSourceAsync();
        var before = await runtime.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("_id", id)).FirstAsync(CancellationToken.None);
        var updated = await runtime.Sources.UpdateAsync(id.ToString(),
            Json("""{"name":"renamed","access_key_id":"","secret_access_key":""}"""),
            ObjectId.GenerateNewId(), CancellationToken.None);
        var after = await runtime.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("_id", id)).FirstAsync(CancellationToken.None);
        Assert.Equal("renamed", updated["name"].AsString);
        Assert.Equal(before["sk"].AsString, after["sk"].AsString);
        Assert.Equal(before["ak"].AsString, after["ak"].AsString);
        Assert.DoesNotContain("secret", updated.ToJson());
    }

    [Fact]
    public async Task SourceWithDataRejectsLocationChange()
    {
        using var runtime = Runtime();
        var id = await runtime.InsertSourceAsync();
        await runtime.Store.Collection("storage_pool_source").UpdateOneAsync(
            new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("hd", true)),
            cancellationToken: CancellationToken.None);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Sources.UpdateAsync(id.ToString(), Json("""{"bucket":"other-bucket"}"""),
                ObjectId.GenerateNewId(), CancellationToken.None));
        Assert.Equal(3, error.Code);
    }

    [Fact]
    public async Task SourceDeleteRejectsActiveMoveTarget()
    {
        using var runtime = Runtime();
        var id = await runtime.InsertSourceAsync();
        await runtime.Store.Collection("storage_transfer").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["s"] = ObjectId.GenerateNewId(),
            ["ts"] = id,
            ["p"] = ObjectId.GenerateNewId(),
            ["st"] = 0,
            ["dir"] = 2,
            ["ct"] = DateTime.UtcNow,
        }, cancellationToken: CancellationToken.None);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Sources.DeleteAsync(id.ToString(), CancellationToken.None));
        Assert.Equal(6, error.Code);
    }

    [Fact]
    public async Task RouterUsesBorrowedDefaultOnlyForEmptyCsid()
    {
        using var runtime = Runtime();
        var defaultProject = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = BsonNull.Value };
        var lease = await runtime.Router.ForOriginalAsync(defaultProject);
        Assert.NotNull(lease.Backend);
        await lease.DisposeAsync();
        await runtime.Default.PutAsync("router/default-" + Guid.NewGuid().ToString("N"),
            new MemoryStream(Encoding.UTF8.GetBytes("ok")), "text/plain", CancellationToken.None);
    }

    [Fact]
    public async Task RouterForProjectOrNullKeepsNonPoolProjectOnLegacyDefaultPath()
    {
        using var runtime = Runtime();
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = BsonNull.Value };
        Assert.Null(await runtime.Router.ForProjectOrNullAsync(project));
        var empty = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = "" };
        Assert.Null(await runtime.Router.ForProjectOrNullAsync(empty));
        var key = "router/non-pool-" + Guid.NewGuid().ToString("N");
        await using (var direct = await runtime.Router.ForDefaultAsync())
        {
            await direct.Backend.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("default")),
                "text/plain", CancellationToken.None);
        }
        using var download = await runtime.Default.GetAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        Assert.Equal("default", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Fact]
    public async Task RouterFailsClosedForNonEmptyUnknownSource()
    {
        using var runtime = Runtime();
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = ObjectId.GenerateNewId() };
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Router.ForOriginalAsync(project).AsTask());
        Assert.Equal(6, error.Code);
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task RouterRoutesOriginalResampleAndOutputThroughRegisteredResolver()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        runtime.Registry.Register(new StoragePoolSourceResolver(runtime.Store, runtime.Factory));
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["csid"] = sourceId };
        await using var original = await runtime.Router.ForOriginalAsync(project);
        await using var resample = await runtime.Router.ForResampleAsync(project);
        await using var output = await runtime.Router.ForOutputAsync(project);
        Assert.NotNull(original.Backend);
        Assert.NotNull(resample.Backend);
        Assert.NotNull(output.Backend);
    }

    [Fact]
    public async Task SourceDescriptorAndApiViewDoNotRenderCredentials()
    {
        using var runtime = Runtime();
        var id = await runtime.InsertSourceAsync();
        var source = await runtime.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("_id", id)).FirstAsync(CancellationToken.None);
        var descriptor = StoragePoolSourceService.Descriptor(source);
        Assert.DoesNotContain("local-disposable-test-secret", descriptor.ToString());
        Assert.DoesNotContain("local-test-only", descriptor.ToString());
        var api = StoragePoolSourceService.ToApi(source);
        Assert.DoesNotContain("secret_access_key", api.ToJson());
        Assert.Contains("...", api["access_key_id"].AsString);
    }

    [Fact]
    public async Task PrepareBuildsBoundedManifestAndIsIdempotentForActiveTransfer()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var fileId = ObjectId.GenerateNewId();
        await runtime.InsertImageFileAsync(projectId, fileId, "original.png");
        var outputId = ObjectId.GenerateNewId();
        await runtime.Store.Collection("output").InsertOneAsync(new BsonDocument
        {
            ["_id"] = outputId, ["p"] = projectId, ["file_name"] = "result.png", ["sb"] = 12L,
        }, cancellationToken: CancellationToken.None);

        var first = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var second = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        Assert.Equal(first["_id"], second["_id"]);
        var rows = await runtime.Store.Collection("storage_transfer_object")
            .Find(new BsonDocument("t", first["_id"])).ToListAsync(CancellationToken.None);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row["kd"].AsString == "original");
        Assert.Contains(rows, row => row["kd"].AsString == "resample");
        Assert.Contains(rows, row => row["kd"].AsString == "output");
    }

    [Fact]
    public async Task ConcurrentSameRequestReusesOneActiveTransfer()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");

        var tasks = Enumerable.Range(0, 4).Select(_ =>
            runtime.Engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionIn,
                null, CancellationToken.None));
        var transfers = await Task.WhenAll(tasks);

        Assert.Single(transfers.Select(row => row["_id"]).Distinct());
        Assert.Equal(1, await runtime.Store.Collection("storage_transfer")
            .CountDocumentsAsync(new BsonDocument
            {
                ["p"] = projectId,
                ["st"] = new BsonDocument { ["$gte"] = 0, ["$lt"] = 4 },
            }, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task DifferentEndpointAuthorityFallsBackToStreamCopy()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync("stream/", "http://127.1:" + TestEnvironment.S3Port);
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var bytes = Encoding.UTF8.GetBytes("stream-fallback");
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream(bytes), "image/png",
            CancellationToken.None);

        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);

        var done = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        Assert.Equal(StoragePoolConstants.Done, done["st"].ToInt32());
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(sourceId, project["csid"].AsObjectId);
        var source = await runtime.GetSourceAsync(sourceId);
        using var lease = runtime.Factory.CreateLease(StoragePoolSourceService.Descriptor(source));
        using var download = await lease.Backend.GetAsync("files/" + saveName, CancellationToken.None);
        using var memory = new MemoryStream();
        await download.Content.CopyToAsync(memory);
        Assert.Equal(bytes, memory.ToArray());
    }

    [Fact]
    public async Task TransferInCopiesSwitchesPointerAndDeletesDefaultSource()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var bytes = Encoding.UTF8.GetBytes("original-image");
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream(bytes), "image/png", CancellationToken.None);

        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var done = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(4, done["st"].ToInt32());
        Assert.Equal(sourceId, project["csid"].AsObjectId);
        Assert.Null(await runtime.Default.HeadAsync("files/" + saveName, CancellationToken.None));
        await using var target = runtime.Factory.CreateLease(StoragePoolSourceService.Descriptor(
            await runtime.GetSourceAsync(sourceId)));
        Assert.NotNull(await target.Backend.HeadAsync("files/" + saveName, CancellationToken.None));
    }

    [Fact]
    public async Task TransferOutOverwritesDefaultDecoyAfterReadingSourceBucket()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(csid: sourceId);
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var sourceBytes = Encoding.UTF8.GetBytes("from-source");
        var decoyBytes = Encoding.UTF8.GetBytes("decoy-default");
        await runtime.PutSourceAsync(sourceId, "files/" + saveName, sourceBytes);
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream(decoyBytes), "image/png", CancellationToken.None);

        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 1, null, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.True(project["csid"].IsBsonNull);
        using var download = await runtime.Default.GetAsync("files/" + saveName, CancellationToken.None);
        using var copy = new MemoryStream();
        await download.Content.CopyToAsync(copy, CancellationToken.None);
        Assert.Equal(sourceBytes, copy.ToArray());
        await using var source = runtime.Factory.CreateLease(StoragePoolSourceService.Descriptor(
            await runtime.GetSourceAsync(sourceId)));
        Assert.Null(await source.Backend.HeadAsync("files/" + saveName, CancellationToken.None));
    }

    [Fact]
    public async Task TransferMoveCopiesToTargetAndDeletesOrigin()
    {
        using var runtime = Runtime();
        var originId = await runtime.InsertSourceAsync();
        var targetId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(csid: originId);
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var bytes = Encoding.UTF8.GetBytes("move-me");
        await runtime.PutSourceAsync(originId, "files/" + saveName, bytes);
        await runtime.PutSourceAsync(targetId, "files/" + saveName, Encoding.UTF8.GetBytes("decoy"));

        var transfer = await runtime.Engine.PrepareAsync(projectId, originId, 2, targetId, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(targetId, project["csid"].AsObjectId);
        await using var target = runtime.Factory.CreateLease(StoragePoolSourceService.Descriptor(
            await runtime.GetSourceAsync(targetId)));
        using var download = await target.Backend.GetAsync("files/" + saveName, CancellationToken.None);
        using var copy = new MemoryStream();
        await download.Content.CopyToAsync(copy, CancellationToken.None);
        Assert.Equal(bytes, copy.ToArray());
        await using var origin = runtime.Factory.CreateLease(StoragePoolSourceService.Descriptor(
            await runtime.GetSourceAsync(originId)));
        Assert.Null(await origin.Backend.HeadAsync("files/" + saveName, CancellationToken.None));
    }

    [Fact]
    public async Task MissingResampleIsSkippedWhileOriginalStillCompletes()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream([1, 2, 3]), "image/png",
            CancellationToken.None);
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var done = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        Assert.Equal(4, done["st"].ToInt32());
        Assert.Equal(1, done["skipped"].ToInt32());
    }

    [Fact]
    public async Task MissingOriginalFailsWithoutSwitchingProjectPointer()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var failed = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(5, failed["st"].ToInt32());
        Assert.True(project["csid"].IsBsonNull);
        Assert.Contains(await runtime.Store.Collection("storage_transfer_object")
            .Find(new BsonDocument("t", transfer["_id"])).ToListAsync(CancellationToken.None),
            row => row["st"].ToInt32() == 4);
    }

    [Fact]
    public async Task ProjectEditTimeChangeAbortsBeforePointerSwitch()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream([9]), "image/png",
            CancellationToken.None);
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        await runtime.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument("m_e", DateTime.UtcNow)), cancellationToken: CancellationToken.None);
        await runtime.Engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var aborted = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(6, aborted["st"].ToInt32());
        Assert.True(project["csid"].IsBsonNull);
        Assert.NotNull(await runtime.Default.HeadAsync("files/" + saveName, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingRunLockPreventsWorkerReplay()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var id = transfer["_id"].AsObjectId;
        await runtime.Store.Collection("storage_transfer_run_lock").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id, ["o"] = "other-worker", ["ct"] = DateTime.UtcNow,
        }, cancellationToken: CancellationToken.None);
        await runtime.Engine.RunAsync(id, CancellationToken.None);
        Assert.Equal(0, (await runtime.GetTransferAsync(id))["st"].ToInt32());
    }

    [Fact]
    public async Task RetryFailedResetsOnlyFailedRowsAndCompletes()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var id = transfer["_id"].AsObjectId;
        await runtime.Engine.RunAsync(id, CancellationToken.None);
        Assert.Equal(5, (await runtime.GetTransferAsync(id))["st"].ToInt32());
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream([1]), "image/png",
            CancellationToken.None);
        await runtime.Engine.ResetForRetryAsync(id, CancellationToken.None);
        var retried = await runtime.GetTransferAsync(id);
        Assert.Equal(0, retried["st"].ToInt32());
        Assert.Contains(await runtime.Store.Collection("storage_transfer_object")
            .Find(new BsonDocument("t", id)).ToListAsync(CancellationToken.None),
            row => row["st"].ToInt32() == 0);
        await runtime.Engine.RunAsync(id, CancellationToken.None);
        Assert.Equal(4, (await runtime.GetTransferAsync(id))["st"].ToInt32());
    }

    [Fact]
    public async Task RetryAbortedRebuildsManifestAndCompletes()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream([1]), "image/png",
            CancellationToken.None);
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var id = transfer["_id"].AsObjectId;
        await runtime.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument("m_e", DateTime.UtcNow)), cancellationToken: CancellationToken.None);
        await runtime.Engine.RunAsync(id, CancellationToken.None);
        Assert.Equal(6, (await runtime.GetTransferAsync(id))["st"].ToInt32());
        await runtime.Engine.ResetForRetryAsync(id, CancellationToken.None);
        await runtime.Engine.RunAsync(id, CancellationToken.None);
        Assert.Equal(4, (await runtime.GetTransferAsync(id))["st"].ToInt32());
    }

    [Fact]
    public async Task CancelOnlyPendingAndDeleteRejectsResidualWarning()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var id = transfer["_id"].AsObjectId;
        await runtime.Engine.CancelAsync(id, CancellationToken.None);
        Assert.Equal(7, (await runtime.GetTransferAsync(id))["st"].ToInt32());
        await Assert.ThrowsAsync<ApiError>(() => runtime.Engine.CancelAsync(id, CancellationToken.None));
        await runtime.Store.Collection("storage_transfer").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument
            {
                ["st"] = 4,
                ["le"] = "residual warning",
            }), cancellationToken: CancellationToken.None);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Engine.DeleteAsync(id, CancellationToken.None));
        Assert.Equal(7, error.Code);
        await runtime.Store.Collection("storage_transfer").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument("le", "")), cancellationToken: CancellationToken.None);
        await runtime.Engine.DeleteAsync(id, CancellationToken.None);
        Assert.False(await runtime.Store.Collection("storage_transfer")
            .Find(new BsonDocument("_id", id)).AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProjectLockRejectsActiveTransferThenAllowsAfterCancel()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId, 0, null, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Projects.LockAsync(projectId.ToString(), Json("""{"locked":true}"""), CancellationToken.None));
        Assert.Equal(5, error.Code);
        await runtime.Engine.CancelAsync(transfer["_id"].AsObjectId, CancellationToken.None);
        var locked = await runtime.Projects.LockAsync(projectId.ToString(),
            Json("""{"locked":true}"""), CancellationToken.None);
        Assert.True(locked["storage_pool_locked"].AsBoolean);
        var unlocked = await runtime.Projects.LockAsync(projectId.ToString(),
            Json("""{"locked":false}"""), CancellationToken.None);
        Assert.False(unlocked["storage_pool_locked"].AsBoolean);
    }

    [Fact]
    public async Task DirtyActiveTransferRowsBlockUniqueIndexPreflight()
    {
        var baseUri = services.Configuration["Mongo:Uri"]!;
        var database = "shoreline_storage_pool_dirty_" + Guid.NewGuid().ToString("N");
        var uri = new MongoUrlBuilder(baseUri) { DatabaseName = database }.ToMongoUrl().ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = uri })
            .Build();
        var store = new MongoStore(configuration);
        var projectId = ObjectId.GenerateNewId();
        await store.Collection(StoragePoolConstants.TransferCollection).InsertManyAsync(new[]
        {
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["p"] = projectId, ["st"] = 0 },
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["p"] = projectId, ["st"] = 2 },
        }, cancellationToken: CancellationToken.None);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                StoragePoolRegistration.EnsureNoDuplicateActiveTransfersAsync(
                    store, CancellationToken.None));
            Assert.Contains("more than one active transfer", error.Message);
        }
        finally
        {
            await new MongoClient(baseUri).DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task LockServiceAllowsExpiredTakeoverAndOwnerOnlyRelease()
    {
        using var runtime = Runtime();
        var projectId = ObjectId.GenerateNewId();
        var transfer = ObjectId.GenerateNewId();
        await runtime.Store.Collection("storage_pool_lock").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId, ["t"] = transfer, ["o"] = "old", ["ea"] = DateTime.UtcNow.AddSeconds(-1),
        }, cancellationToken: CancellationToken.None);
        Assert.True(await runtime.Locks.AcquireAsync(projectId, transfer, "new", 1800, CancellationToken.None));
        await runtime.Locks.ReleaseAsync(projectId, transfer, "old", CancellationToken.None);
        Assert.NotNull(await runtime.Store.Collection("storage_pool_lock")
            .Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(CancellationToken.None));
        await runtime.Locks.ReleaseAsync(projectId, transfer, "new", CancellationToken.None);
        Assert.Null(await runtime.Store.Collection("storage_pool_lock")
            .Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanDisabledDoesNotQueueOrPrepareTransfers()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        await runtime.InsertProjectAsync(editTime: DateTime.UtcNow.AddDays(-100));
        await runtime.Settings.UpdateAsync(Json("""{"scan_enabled":false,"run_enabled":true}"""),
            ObjectId.GenerateNewId(), CancellationToken.None);
        var result = await runtime.Scan.RunAsync(false, CancellationToken.None);
        Assert.Equal(0, result["queued"].ToInt32());
        Assert.Equal(0, await runtime.Store.Collection("storage_transfer")
            .CountDocumentsAsync(new BsonDocument("s", sourceId), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ScanGlobalBudgetAllowsOversizedFirstThenStops()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        await runtime.InsertProjectAsync(editTime: DateTime.UtcNow.AddDays(-100), fs: 1024);
        await runtime.InsertProjectAsync(editTime: DateTime.UtcNow.AddDays(-99), fs: 1024);
        await runtime.Settings.UpdateAsync(Json(
                """{"scan_enabled":true,"run_enabled":false,"run_max_bytes":1048576,"run_max_projects":20}"""),
            ObjectId.GenerateNewId(), CancellationToken.None);
        await runtime.Scan.RunAsync(false, CancellationToken.None);
        Assert.Equal(1, await runtime.Store.Collection("storage_transfer")
            .CountDocumentsAsync(new BsonDocument("s", sourceId), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ScanSkipsProjectWithFreshPresence()
    {
        using var runtime = Runtime();
        await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(editTime: DateTime.UtcNow.AddDays(-100));
        await runtime.Store.Collection("project_presence").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["last_heartbeat"] = DateTime.UtcNow,
            ["expire_at"] = DateTime.UtcNow.AddMinutes(1),
        }, cancellationToken: CancellationToken.None);
        await runtime.Settings.UpdateAsync(Json("""{"scan_enabled":true,"run_enabled":false}"""),
            ObjectId.GenerateNewId(), CancellationToken.None);
        await runtime.Scan.RunAsync(false, CancellationToken.None);
        Assert.Equal(0, await runtime.Store.Collection("storage_transfer")
            .CountDocumentsAsync(new BsonDocument("p", projectId), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ScanRunDisabledLeavesPreparedCheckpoint()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(editTime: DateTime.UtcNow.AddDays(-100));
        await runtime.Settings.UpdateAsync(Json("""{"scan_enabled":true,"run_enabled":false}"""),
            ObjectId.GenerateNewId(), CancellationToken.None);
        await runtime.Scan.RunAsync(false, CancellationToken.None);
        var transfer = await runtime.Store.Collection("storage_transfer")
            .Find(new BsonDocument { ["p"] = projectId, ["s"] = sourceId })
            .FirstOrDefaultAsync(CancellationToken.None);
        Assert.NotNull(transfer);
        Assert.Equal(0, transfer!["st"].ToInt32());
    }

    [Fact]
    public async Task MoveValidationRejectsDefaultToDefaultAndSameTarget()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var defaultProject = await runtime.InsertProjectAsync();
        var same = await runtime.InsertProjectAsync(csid: sourceId);
        var defaultError = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Projects.MoveAsync(defaultProject.ToString(),
                Json("""{"target_source_id":"default"}"""), CancellationToken.None));
        Assert.Equal(5, defaultError.Code);
        var sameError = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Projects.MoveAsync(same.ToString(),
                Json($$"""{"target_source_id":"{{sourceId}}"}"""), CancellationToken.None));
        Assert.Equal(5, sameError.Code);
    }

    [Fact]
    public async Task SettingsPartialUpdateWritesAuditAndKeepsDefaults()
    {
        using var runtime = Runtime();
        var actor = ObjectId.GenerateNewId();
        var view = await runtime.Settings.UpdateAsync(
            Json("""{"scan_enabled":true,"run_max_objects":1234}"""), actor, CancellationToken.None);
        Assert.True(view["scan_enabled"].AsBoolean);
        Assert.Equal(1234, view["run_max_objects"].ToInt32());
        Assert.Equal(20L * 1024 * 1024 * 1024, view["run_max_bytes"].ToInt64());
        Assert.Equal(1, await runtime.Store.Collection("storage_pool_audit")
            .CountDocumentsAsync(new BsonDocument("a", actor), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task StatsAndUsageExposeAggregatesWithoutObjectKeys()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(csid: sourceId, fs: 2);
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), "usage.png");
        var stats = await runtime.Projects.StatsAsync(CancellationToken.None);
        var usage = await runtime.Projects.UsageAsync(CancellationToken.None);
        Assert.True(stats["sources"].ToInt64() >= 1);
        Assert.True(stats["migrated_projects"].ToInt64() >= 1);
        Assert.True(usage["total"]["bytes"].ToInt64() >= 2048);
        Assert.True(usage["total"]["projects"].ToInt64() >= 1);
        Assert.True(usage["migrated_projects"].ToInt64() >= 1);
        Assert.DoesNotContain("secret_access_key", usage.ToJson());
    }

    [Fact]
    public async Task DifferentDirectionOrTargetIsRejectedWhileProjectTransferActive()
    {
        using var runtime = Runtime();
        var sourceA = await runtime.InsertSourceAsync("a/");
        var sourceB = await runtime.InsertSourceAsync("b/");
        var projectId = await runtime.InsertProjectAsync(fs: 1);
        var first = await runtime.Engine.PrepareAsync(projectId, sourceA, StoragePoolConstants.DirectionIn,
            null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Engine.PrepareAsync(projectId, sourceB, StoragePoolConstants.DirectionIn,
                null, CancellationToken.None));

        Assert.Equal(7, error.Code);
        var active = await runtime.Store.Collection("storage_transfer").Find(new BsonDocument
        {
            ["p"] = projectId,
            ["st"] = new BsonDocument { ["$gte"] = 0, ["$lt"] = 4 },
        }).ToListAsync(CancellationToken.None);
        Assert.Single(active);
        Assert.Equal(first["_id"], active[0]["_id"]);
    }

    [Fact]
    public async Task ShortWriteNeverSwitchesPointerOrDeletesSource()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync("src/");
        var targetId = await runtime.InsertSourceAsync("dst/");
        var projectId = await runtime.InsertProjectAsync(csid: sourceId, fs: 1);
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), "short.png");
        var sourceBackend = new MemoryBackend();
        sourceBackend.Seed("files/short.png", new byte[1000]);
        var targetBackend = new MemoryBackend { ShortWrite = true };
        var engine = FaultEngine(runtime, sourceBackend, targetBackend);
        var transfer = await engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionMove,
            targetId, CancellationToken.None);

        await engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);

        var stored = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(StoragePoolConstants.Failed, stored["st"].ToInt32());
        Assert.Equal(sourceId, project["csid"].AsObjectId);
        Assert.Equal(0, sourceBackend.DeleteCalls);
    }

    [Fact]
    public async Task EqualLengthCorruptionIsNotDetectedBecauseVerificationIsSizeOnly()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync("src/");
        var targetId = await runtime.InsertSourceAsync("dst/");
        var projectId = await runtime.InsertProjectAsync(csid: sourceId, fs: 1);
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), "equal.png");
        var sourceBackend = new MemoryBackend();
        sourceBackend.Seed("files/equal.png", [9, 9, 9, 9]);
        var originalBytes = sourceBackend.Bytes("files/equal.png");
        var targetBackend = new MemoryBackend();
        var engine = FaultEngine(runtime, sourceBackend, targetBackend);
        var transfer = await engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionMove,
            targetId, CancellationToken.None);
        var transferId = transfer["_id"].AsObjectId;
        // Put the transfer in the verify phase with a same-length but corrupted
        // destination object. The engine's contract is size-only, so this must
        // be reported as verified and the pointer switched.
        targetBackend.Seed("files/equal.png", [1, 2, 3, 4]);
        await runtime.Store.Collection("storage_transfer_object").UpdateManyAsync(
            new BsonDocument { ["t"] = transferId, ["kd"] = StoragePoolConstants.KindOriginal },
            new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.ObjectCopied,
                ["sz"] = 4L,
            }), cancellationToken: CancellationToken.None);
        await runtime.Store.Collection("storage_transfer_object").UpdateManyAsync(
            new BsonDocument { ["t"] = transferId, ["kd"] = StoragePoolConstants.KindResample },
            new BsonDocument("$set", new BsonDocument("st", StoragePoolConstants.ObjectSkipped)),
            cancellationToken: CancellationToken.None);
        await runtime.Store.Collection("storage_transfer").UpdateOneAsync(
            new BsonDocument("_id", transferId),
            new BsonDocument("$set", new BsonDocument("st", StoragePoolConstants.Verified)),
            cancellationToken: CancellationToken.None);

        await engine.RunAsync(transferId, CancellationToken.None);

        Assert.Equal(StoragePoolConstants.Done,
            (await runtime.GetTransferAsync(transferId))["st"].ToInt32());
        Assert.Equal(targetId, (await runtime.GetProjectAsync(projectId))["csid"].AsObjectId);
        Assert.Equal(4, targetBackend.Bytes("files/equal.png").Length);
        Assert.False(originalBytes.SequenceEqual(targetBackend.Bytes("files/equal.png")));
    }

    [Fact]
    public async Task AlreadySwitchedPointerResumesWithoutReverseCopy()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync("src/");
        var targetId = await runtime.InsertSourceAsync("dst/");
        var projectId = await runtime.InsertProjectAsync(csid: sourceId, fs: 1);
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), "recover.png");
        var sourceBackend = new MemoryBackend();
        sourceBackend.Seed("files/recover.png", new byte[1000]);
        var targetBackend = new MemoryBackend();
        targetBackend.Seed("files/recover.png", new byte[1000]);
        var engine = FaultEngine(runtime, sourceBackend, targetBackend);
        var transfer = await engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionMove,
            targetId, CancellationToken.None);
        var transferId = transfer["_id"].AsObjectId;
        await runtime.Store.Collection("storage_transfer_object").UpdateManyAsync(
            new BsonDocument { ["t"] = transferId, ["kd"] = StoragePoolConstants.KindOriginal },
            new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.ObjectVerified,
                ["sz"] = 1000L,
            }));
        await runtime.Store.Collection("storage_transfer_object").UpdateManyAsync(
            new BsonDocument { ["t"] = transferId, ["kd"] = StoragePoolConstants.KindResample },
            new BsonDocument("$set", new BsonDocument("st", StoragePoolConstants.ObjectSkipped)));
        await runtime.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument
            {
                ["csid"] = targetId,
                ["csw"] = DateTime.UtcNow,
            }));
        await runtime.Store.Collection("storage_transfer").UpdateOneAsync(new BsonDocument("_id", transferId),
            new BsonDocument("$set", new BsonDocument { ["st"] = StoragePoolConstants.Verified }));

        await engine.RunAsync(transferId, CancellationToken.None);

        var stored = await runtime.GetTransferAsync(transferId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(StoragePoolConstants.Done, stored["st"].ToInt32());
        Assert.Equal(targetId, project["csid"].AsObjectId);
        Assert.False(sourceBackend.Contains("files/recover.png"));
        Assert.True(targetBackend.Contains("files/recover.png"));
    }

    [Fact]
    public async Task ZeroByteObjectIsCopiedVerifiedAndSwitched()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync("src/");
        var targetId = await runtime.InsertSourceAsync("dst/");
        var projectId = await runtime.InsertProjectAsync(csid: sourceId, fs: 0);
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), "empty.png");
        var sourceBackend = new MemoryBackend();
        sourceBackend.Seed("files/empty.png", []);
        var targetBackend = new MemoryBackend();
        var engine = FaultEngine(runtime, sourceBackend, targetBackend);
        var transfer = await engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionMove,
            targetId, CancellationToken.None);

        await engine.RunAsync(transfer["_id"].AsObjectId, CancellationToken.None);

        var stored = await runtime.GetTransferAsync(transfer["_id"].AsObjectId);
        var project = await runtime.GetProjectAsync(projectId);
        Assert.Equal(StoragePoolConstants.Done, stored["st"].ToInt32());
        Assert.Equal(targetId, project["csid"].AsObjectId);
        Assert.True(targetBackend.Contains("files/empty.png"));
        Assert.Empty(targetBackend.Bytes("files/empty.png"));
    }

    [Fact]
    public async Task CleanupOfPooledProjectDeletesOnlyResolvedSourceObjects()
    {
        using var runtime = Runtime();
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync(csid: sourceId);
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        var outputId = ObjectId.GenerateNewId();
        await runtime.Store.Collection("output").InsertOneAsync(new BsonDocument
        {
            ["_id"] = outputId, ["p"] = projectId, ["file_name"] = "out.png", ["sb"] = 4L,
        }, cancellationToken: CancellationToken.None);
        await runtime.PutSourceAsync(sourceId, "files/" + saveName, [1, 2, 3]);
        await runtime.PutSourceAsync(sourceId, "outputs/" + outputId + "/out.png", [4, 5, 6]);
        await runtime.Default.PutAsync("files/" + saveName, new MemoryStream([9, 9, 9]),
            "image/png", CancellationToken.None);
        await runtime.Default.PutAsync("outputs/" + outputId + "/out.png",
            new MemoryStream([8, 8, 8]), "image/png", CancellationToken.None);
        var project = await runtime.GetProjectAsync(projectId);

        runtime.Registry.Register(new StoragePoolSourceResolver(runtime.Store, runtime.Factory));
        await ProjectContentCleanup.ClearProjectContentsAsync(
            runtime.Store, runtime.Router, runtime.Default, services.Configuration, project,
            CancellationToken.None);

        await using (var lease = await runtime.Router.ForSourceAsync(sourceId, CancellationToken.None))
        {
            Assert.Null(await lease.Backend.HeadAsync("files/" + saveName, CancellationToken.None));
            Assert.Null(await lease.Backend.HeadAsync(
                "outputs/" + outputId + "/out.png", CancellationToken.None));
        }
        Assert.NotNull(await runtime.Default.HeadAsync("files/" + saveName, CancellationToken.None));
        Assert.NotNull(await runtime.Default.HeadAsync(
            "outputs/" + outputId + "/out.png", CancellationToken.None));
    }

    private StoragePoolTransferEngine FaultEngine(StoragePoolRuntime runtime, MemoryBackend source,
        MemoryBackend target)
    {
        var labels = new ProjectLabelLock(runtime.Store, TimeProvider.System);
        return new StoragePoolTransferEngine(runtime.Store, runtime.Factory, runtime.Default, labels,
            runtime.Locks, services.Configuration, TimeProvider.System, descriptor =>
                new StorageBackendLease(
                    descriptor.Prefix.StartsWith("src", StringComparison.Ordinal) ? source : target, false));
    }
}

internal sealed class StoragePoolRuntime : IDisposable
{
    public MongoStore Store { get; }
    public ObjectStorage Default { get; }
    public StorageBackendFactory Factory { get; } = new();
    public ProjectStorageResolverRegistry Registry { get; } = new();
    public StoragePoolSourceService Sources { get; }
    public StoragePoolSettingsService Settings { get; }
    public StoragePoolLockService Locks { get; }
    public StoragePoolTransferEngine Engine { get; }
    public StoragePoolTransferService Transfers { get; }
    public StoragePoolProjectService Projects { get; }
    public StoragePoolScanService Scan { get; }
    public ProjectStorageRouter Router { get; }

    public StoragePoolRuntime(LocalServices services)
    {
        var baseUri = services.Configuration["Mongo:Uri"]
            ?? throw new InvalidOperationException("Mongo:Uri is required for storage-pool tests");
        var isolatedUri = new MongoUrlBuilder(baseUri)
        {
            DatabaseName = "shoreline_storage_pool_test_" + Guid.NewGuid().ToString("N"),
        }.ToMongoUrl().ToString();
        var isolatedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = isolatedUri })
            .Build();
        Store = new MongoStore(isolatedConfiguration);
        Store.Collection("storage_transfer").Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("p"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "storage_transfer_one_active_project",
                        Unique = true,
                        PartialFilterExpression =
                            Builders<BsonDocument>.Filter.Gte("st", 0) &
                            Builders<BsonDocument>.Filter.Lt("st", 4),
                    }))
            .GetAwaiter().GetResult();
        Default = new ObjectStorage(services.Configuration);
        var clock = TimeProvider.System;
        Sources = new StoragePoolSourceService(Store, Factory, clock);
        Settings = new StoragePoolSettingsService(Store, clock);
        Locks = new StoragePoolLockService(Store, clock);
        var labels = new ProjectLabelLock(Store, clock);
        Engine = new StoragePoolTransferEngine(Store, Factory, Default, labels, Locks,
            services.Configuration, clock);
        Transfers = new StoragePoolTransferService(Store, Engine);
        Projects = new StoragePoolProjectService(Store, Sources, Engine, Transfers);
        Scan = new StoragePoolScanService(Store, Settings, Engine, Transfers, clock);
        Router = new ProjectStorageRouter(Default, Factory, Registry);
    }

    public async Task<ObjectId> InsertSourceAsync(string? prefix = null,
        string? endpoint = null)
    {
        endpoint ??= "http://127.0.0.1:" + TestEnvironment.S3Port;
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await Store.Collection("storage_pool_source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "source-" + id,
            ["e"] = true,
            ["ep"] = endpoint,
            ["b"] = "shoreline-test",
            ["sp"] = prefix ?? "pool/" + Guid.NewGuid().ToString("N") + "/",
            ["r"] = "us-east-1",
            ["as"] = "path",
            ["vs"] = true,
            ["ak"] = "local-test-only",
            ["sk"] = "local-disposable-test-secret",
            ["pd"] = "",
            ["rm"] = "",
            ["iad"] = 90,
            ["mo"] = 2000,
            ["hd"] = false,
            ["ct"] = now,
            ["ut"] = now,
        }, cancellationToken: CancellationToken.None);
        return id;
    }

    public async Task<BsonDocument> GetSourceAsync(ObjectId id) =>
        await Store.Collection("storage_pool_source").Find(new BsonDocument("_id", id))
            .FirstAsync(CancellationToken.None);

    public async Task<ObjectId> InsertProjectAsync(ObjectId? csid = null, long fs = 1,
        DateTime? editTime = null)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "project-" + id,
            ["st"] = 0,
            ["cip"] = false,
            ["m_e"] = editTime ?? now.AddDays(-2),
            ["label_revision"] = 0,
            ["splk"] = false,
            ["fs"] = fs,
            ["cover_bytes"] = 0L,
            ["resample_bytes"] = 0L,
            ["ob"] = 0L,
            ["csid"] = csid is null ? BsonNull.Value : csid.Value,
            ["csw"] = BsonNull.Value,
            ["ct"] = now,
        }, cancellationToken: CancellationToken.None);
        return id;
    }

    public async Task<BsonDocument> GetProjectAsync(ObjectId id) =>
        await Store.Collection("project").Find(new BsonDocument("_id", id)).FirstAsync(CancellationToken.None);

    public async Task InsertImageFileAsync(ObjectId projectId, ObjectId fileId, string saveName)
    {
        await Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["t"] = 2,
            ["sa"] = saveName,
            ["fs"] = 1L,
            ["cover_bytes"] = 0L,
            ["resample_bytes"] = 0L,
            ["ac"] = true,
        }, cancellationToken: CancellationToken.None);
    }

    public async Task PutSourceAsync(ObjectId sourceId, string key, byte[] bytes)
    {
        var source = await GetSourceAsync(sourceId);
        await using var lease = Factory.CreateLease(StoragePoolSourceService.Descriptor(source));
        await lease.Backend.PutAsync(key, new MemoryStream(bytes), "application/octet-stream",
            CancellationToken.None);
    }

    public async Task<BsonDocument> GetTransferAsync(ObjectId id) =>
        await Store.Collection("storage_transfer").Find(new BsonDocument("_id", id))
            .FirstAsync(CancellationToken.None);

    public void Dispose() => Default.Dispose();
}

internal sealed class MemoryBackend : IStorageBackend
{
    private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

    public bool ShortWrite { get; init; }
    public int DeleteCalls { get; private set; }

    public void Seed(string key, byte[] bytes) => objects[key] = bytes;
    public bool Contains(string key) => objects.ContainsKey(key);
    public byte[] Bytes(string key) => objects[key];

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var wanted = ShortWrite ? 500 : long.MaxValue;
        var chunk = new byte[8192];
        long total = 0;
        while (total < wanted)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, wanted - total)), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            total += read;
        }
        objects[key] = buffer.ToArray();
    }

    public Task<ObjectDownload> GetAsync(string key, CancellationToken ct)
    {
        if (!objects.TryGetValue(key, out var bytes)) throw new FileNotFoundException(key);
        var response = new GetObjectResponse
        {
            ResponseStream = new MemoryStream(bytes, writable: false),
            ContentLength = bytes.Length,
        };
        return Task.FromResult(new ObjectDownload(response));
    }

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) =>
        Task.FromResult(objects.TryGetValue(key, out var bytes)
            ? new ObjectInfo(key, bytes.Length, "\"etag\"", "application/octet-stream", DateTime.UtcNow)
            : null);

    public async Task<long?> SizeAsync(string key, CancellationToken ct) =>
        (await HeadAsync(key, ct))?.Size;

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        DeleteCalls++;
        objects.Remove(key);
        return Task.CompletedTask;
    }

    public Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        if (objects.TryGetValue(source, out var bytes)) objects[destination] = bytes.ToArray();
        return Task.CompletedTask;
    }

    public Task<string> SignGetAsync(string key, TimeSpan validity) => Task.FromResult("");

    public Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition) => Task.FromResult("");

    public Task<ObjectPage> ListAsync(string prefix, string? startAfter, int maxKeys, CancellationToken ct)
    {
        var items = objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(key => startAfter is null || string.CompareOrdinal(key, startAfter) > 0)
            .OrderBy(key => key, StringComparer.Ordinal).Take(maxKeys)
            .Select(key => new ObjectInfo(key, objects[key].Length)).ToList();
        return Task.FromResult(new ObjectPage(items, null));
    }

    public void Dispose()
    {
    }
}
