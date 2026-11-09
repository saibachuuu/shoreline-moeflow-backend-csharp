using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Storage;

/// <summary>
/// Module-neutral resolver hook. Modules can resolve an arbitrary storage
/// source id without the core router knowing their namespaces or types.
/// </summary>
public interface IProjectStorageResolver
{
    Task<StorageBackendLease?> ResolveAsync(ObjectId sourceId, CancellationToken cancellationToken);
}

public sealed class ProjectStorageResolverRegistry
{
    private readonly List<IProjectStorageResolver> resolvers = new();

    public void Register(IProjectStorageResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (resolvers)
        {
            if (!resolvers.Contains(resolver)) resolvers.Add(resolver);
        }
    }

    internal IReadOnlyList<IProjectStorageResolver> Snapshot()
    {
        lock (resolvers) return resolvers.ToArray();
    }
}

/// <summary>
/// Resolves the backend for an operation. Missing/empty project csid uses the
/// borrowed default backend. A non-empty csid that cannot be resolved fails
/// closed: it never falls back to the default bucket.
/// </summary>
public sealed class ProjectStorageRouter
{
    private readonly StorageBackendFactory factory;
    private readonly ProjectStorageResolverRegistry registry;
    private readonly ObjectStorage defaultStorage;

    public ProjectStorageRouter(
        ObjectStorage defaultStorage,
        StorageBackendFactory factory,
        ProjectStorageResolverRegistry registry)
    {
        this.defaultStorage = defaultStorage;
        this.factory = factory;
        this.registry = registry;
    }

    public ValueTask<StorageBackendLease> ForDefaultAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(factory.BorrowDefault(defaultStorage));

    public ValueTask<StorageBackendLease> ForOriginalAsync(BsonDocument project,
        CancellationToken cancellationToken = default) => ForProjectAsync(project, cancellationToken);

    public ValueTask<StorageBackendLease> ForResampleAsync(BsonDocument project,
        CancellationToken cancellationToken = default) => ForProjectAsync(project, cancellationToken);

    public ValueTask<StorageBackendLease> ForOutputAsync(BsonDocument project,
        CancellationToken cancellationToken = default) => ForProjectAsync(project, cancellationToken);

    public async ValueTask<StorageBackendLease> ForProjectAsync(BsonDocument project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sourceId = ReadSourceId(project);
        if (sourceId is null) return factory.BorrowDefault(defaultStorage);
        return await ForSourceAsync(sourceId.Value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<StorageBackendLease> ForSourceAsync(ObjectId sourceId,
        CancellationToken cancellationToken = default)
    {
        foreach (var resolver in registry.Snapshot())
        {
            var lease = await resolver.ResolveAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (lease is not null) return lease;
        }
        throw SourceUnavailable();
    }

    public static ObjectId? ReadSourceId(BsonDocument project)
    {
        if (!project.TryGetValue("csid", out var value) || value.IsBsonNull) return null;
        if (value.IsObjectId) return value.AsObjectId;
        if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed)) return parsed;
        throw SourceUnavailable();
    }

    public static ApiError SourceUnavailable() =>
        new("StorageSourceUnavailable", 6, "Storage source is unavailable", 409);
}
