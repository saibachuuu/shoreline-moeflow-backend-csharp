using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.StoragePool;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

/// <summary>
/// Rebuilds the storage-pool worker and its scoped services over the same
/// Mongo database, so these tests exercise durable state rather than the
/// lifetime of a single service instance.
/// </summary>
[Collection("local-services")]
public sealed class StoragePoolRecoveryTests(LocalServices services)
{
    private static readonly TimeZoneInfo PlusEight = TimeZoneInfo.CreateCustomTimeZone(
        "storage-pool-test+08", TimeSpan.FromHours(8),
        "storage-pool-test+08", "storage-pool-test+08");

    [Fact]
    public async Task WorkerResumesPreparedTransferAfterHostReconstruction()
    {
        var clock = new StoragePoolMutableClock(
            new DateTimeOffset(2026, 11, 11, 3, 5, 0, TimeSpan.Zero), PlusEight);
        using var runtime = new StoragePoolRuntime(services, clock);
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        var saveName = Guid.NewGuid().ToString("N") + ".png";
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(), saveName);
        await runtime.Default.PutAsync("files/" + saveName,
            new MemoryStream(Encoding.UTF8.GetBytes("recovery-transfer")), "image/png",
            CancellationToken.None);
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId,
            StoragePoolConstants.DirectionIn, null, CancellationToken.None);
        var transferId = transfer["_id"].AsObjectId;
        Assert.Equal(StoragePoolConstants.Pending, transfer["st"].ToInt32());

        using (var worker = new StoragePoolWorkerHarness(services, runtime, clock))
            await worker.TickAsync(CancellationToken.None);

        var done = await runtime.GetTransferAsync(transferId);
        Assert.Equal(StoragePoolConstants.Done, done["st"].ToInt32());
        Assert.Equal(sourceId, (await runtime.GetProjectAsync(projectId))["csid"].AsObjectId);
        Assert.Null(await runtime.Default.HeadAsync("files/" + saveName, CancellationToken.None));
    }

    [Fact]
    public async Task ProjectLockLeaseExpiresAcrossServiceReconstruction()
    {
        var clock = new StoragePoolMutableClock(
            new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new StoragePoolRuntime(services, clock);
        var projectId = ObjectId.GenerateNewId();
        var firstTransfer = ObjectId.GenerateNewId();
        var secondTransfer = ObjectId.GenerateNewId();

        Assert.True(await runtime.Locks.AcquireAsync(projectId, firstTransfer, "owner-a", 1800,
            CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(1799));
        var beforeExpiry = new StoragePoolLockService(runtime.Store, clock);
        Assert.False(await beforeExpiry.AcquireAsync(projectId, secondTransfer, "owner-b", 1800,
            CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = new StoragePoolLockService(runtime.Store, clock);
        Assert.True(await afterExpiry.AcquireAsync(projectId, secondTransfer, "owner-b", 1800,
            CancellationToken.None));

        await afterExpiry.ReleaseAsync(projectId, firstTransfer, "owner-a", CancellationToken.None);
        var row = await runtime.Store.Collection(StoragePoolConstants.LockCollection)
            .Find(new BsonDocument("_id", projectId)).FirstAsync(CancellationToken.None);
        Assert.Equal(secondTransfer, row["t"].AsObjectId);
        Assert.Equal("owner-b", row["o"].AsString);

        await afterExpiry.ReleaseAsync(projectId, secondTransfer, "owner-b", CancellationToken.None);
        Assert.Null(await runtime.Store.Collection(StoragePoolConstants.LockCollection)
            .Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StaleRunLockWithoutTtlBlocksWorkerRecoveryLikeLegacy()
    {
        var clock = new StoragePoolMutableClock(
            new DateTimeOffset(2026, 11, 11, 3, 5, 0, TimeSpan.Zero));
        using var runtime = new StoragePoolRuntime(services, clock);
        var sourceId = await runtime.InsertSourceAsync();
        var projectId = await runtime.InsertProjectAsync();
        await runtime.InsertImageFileAsync(projectId, ObjectId.GenerateNewId(),
            Guid.NewGuid().ToString("N") + ".png");
        var transfer = await runtime.Engine.PrepareAsync(projectId, sourceId,
            StoragePoolConstants.DirectionIn, null, CancellationToken.None);
        var transferId = transfer["_id"].AsObjectId;
        await runtime.Store.Collection(StoragePoolConstants.RunLockCollection).InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = transferId,
                ["o"] = "crashed-worker",
                ["ct"] = clock.GetUtcNow().UtcDateTime,
            }, cancellationToken: CancellationToken.None);

        using (var worker = new StoragePoolWorkerHarness(services, runtime, clock))
            await worker.TickAsync(CancellationToken.None);

        Assert.Equal(StoragePoolConstants.Pending, (await runtime.GetTransferAsync(transferId))["st"].ToInt32());
        Assert.True(await runtime.Store.Collection(StoragePoolConstants.RunLockCollection)
            .Find(new BsonDocument("_id", transferId)).AnyAsync(CancellationToken.None));
    }
}

internal sealed class StoragePoolWorkerHarness : IDisposable
{
    private readonly ServiceProvider provider;

    public StoragePoolWorker Worker { get; }

    public StoragePoolWorkerHarness(LocalServices services, StoragePoolRuntime runtime,
        TimeProvider clock, bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(services.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = runtime.MongoUri,
                ["StoragePool:WorkerEnabled"] = enabled ? "true" : "false",
            })
            .Build();
        var store = new MongoStore(configuration);
        var factory = new StorageBackendFactory();
        var storage = new ObjectStorage(configuration);
        var labels = new ProjectLabelLock(store, clock);
        var builder = new ServiceCollection();
        builder.AddSingleton(store);
        builder.AddSingleton(factory);
        builder.AddSingleton(storage);
        builder.AddSingleton<IConfiguration>(configuration);
        builder.AddSingleton(clock);
        builder.AddSingleton(labels);
        builder.AddScoped(_ => new StoragePoolLockService(store, clock));
        builder.AddScoped(_ => new StoragePoolSettingsService(store, clock));
        builder.AddScoped(sp => new StoragePoolTransferEngine(store, factory, storage,
            labels, sp.GetRequiredService<StoragePoolLockService>(), configuration, clock));
        builder.AddScoped(sp => new StoragePoolTransferService(store,
            sp.GetRequiredService<StoragePoolTransferEngine>()));
        builder.AddScoped(sp => new StoragePoolScanService(store,
            sp.GetRequiredService<StoragePoolSettingsService>(),
            sp.GetRequiredService<StoragePoolTransferEngine>(),
            sp.GetRequiredService<StoragePoolTransferService>(), clock));
        provider = builder.BuildServiceProvider();
        Worker = new StoragePoolWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            configuration, NullLogger<StoragePoolWorker>.Instance);
    }

    public Task TickAsync(CancellationToken ct = default) => Worker.TickAsync(ct);

    public void Dispose() => provider.Dispose();
}

internal sealed class StoragePoolMutableClock : TimeProvider
{
    private DateTimeOffset utcNow;

    public StoragePoolMutableClock(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null)
    {
        this.utcNow = utcNow.ToUniversalTime();
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone { get; }

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan delta) => utcNow += delta;

    public void SetUtcNow(DateTimeOffset value) => utcNow = value.ToUniversalTime();
}
