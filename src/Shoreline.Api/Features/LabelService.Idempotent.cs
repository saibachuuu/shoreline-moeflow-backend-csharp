using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// The source row either did not exist before this call (Created) or was
/// already present with the same stable id and intent (AlreadyApplied).
/// </summary>
public enum SourceWriteOutcome
{
    Created,
    AlreadyApplied
}

public sealed record SourceWriteResult(
    SourceWriteOutcome Outcome,
    ObjectId SourceId,
    long Revision,
    bool CountersApplied);

/// <summary>
/// Translation import outcome. Empty content is intentionally a no-op:
/// Empty is returned without inserting a row or touching counters.
/// </summary>
public enum TranslationWriteOutcome
{
    Created,
    AlreadyApplied,
    Empty
}

public sealed record TranslationWriteResult(
    TranslationWriteOutcome Outcome,
    ObjectId TranslationId,
    long Revision,
    bool CountersApplied);

/// <summary>
/// Optional deterministic fault-injection seam for import recovery tests.
/// Stage names are stable and documented in docs/label-idempotent-writes.md.
/// Production code leaves this null.
/// </summary>
public sealed class LabelIdempotentHooks
{
    public Func<string, CancellationToken, Task>? AfterStageAsync { get; init; }
}

public sealed partial class LabelService
{
    private const string PendingField = "idempotent_pending";
    private const string PendingOwnerField = "idempotent_owner";
    private const string PendingRevisionField = "idempotent_revision";

    /// <summary>
    /// Test-only seam. It is intentionally a settable property so the existing
    /// seven production files do not need to change.
    /// </summary>
    public LabelIdempotentHooks? IdempotentHooks { get; set; }

    /// <summary>
    /// Create one image source with a caller-supplied stable id. The whole
    /// operation runs under the shared project label lease. Counters are
    /// rebuilt from primary rows, never incremented, so an unknown insert
    /// outcome or a partial counter write converges on retry.
    /// <paramref name="context"/> is nullable because the import job calls this
    /// outside an HTTP request; the helper borrows the scoped
    /// <see cref="LabelRequestContext"/> instead of reading the HttpContext.
    /// Python has no per-request object on the import task path.
    /// </summary>
    public async Task<SourceWriteResult> CreateSourceOnceAsync(
        HttpContext? context,
        BsonDocument actor,
        ObjectId fileId,
        ObjectId sourceId,
        string content,
        double x,
        double y,
        int positionType,
        int sourceRank,
        long expectedRevision,
        CancellationToken ct)
    {
        _ = context;
        if (sourceId == ObjectId.Empty)
            throw ApiError.Invalid("缺少稳定的原文 id");

        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await EnterAsync(projectId, ct);
        if (session.Revision != expectedRevision)
            throw LabelErrors.Busy("后台任务的标签版本已过期");

        var file = await RequireFileAsync(fileId, ct);
        RequireImage(file, "只有图片文件能添加原文");
        await permissions.RequireProjectAsync(actor, session.Project, "project:ADD_LABEL", ct);
        ValidatePositionType(positionType);
        ValidateRange(x, "x");
        ValidateRange(y, "y");
        RequireActivated(file);
        session.AssertEpoch(file);

        var owner = session.Lease.Owner;
        var now = Now();
        var existing = await Sources.Find(new BsonDocument("_id", sourceId))
            .FirstOrDefaultAsync(ct);
        var created = false;

        if (existing is null)
        {
            var source = new BsonDocument
            {
                ["_id"] = sourceId,
                ["f"] = fileId,
                ["r"] = sourceRank,
                ["c"] = content,
                ["x"] = x,
                ["y"] = y,
                ["v"] = new BsonArray(),
                ["p"] = positionType,
                ["lf"] = true,
                ["b"] = false,
                ["cm"] = false,
                ["pt"] = new BsonArray(),
                ["ct"] = now,
                ["e"] = now,
                ["label_epoch"] = session.Revision,
                [PendingField] = true,
                [PendingOwnerField] = owner,
                [PendingRevisionField] = session.Revision
            };
            try
            {
                await Sources.InsertOneAsync(source, cancellationToken: ct);
                created = true;
                await StageAsync("source_inserted", ct);
            }
            catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
            {
                existing = await Sources.Find(new BsonDocument("_id", sourceId))
                    .FirstOrDefaultAsync(ct);
                if (existing is null) throw;
            }
        }

        if (!created)
        {
            existing ??= await Sources.Find(new BsonDocument("_id", sourceId))
                .FirstOrDefaultAsync(ct);
            if (existing is null)
                throw SourceNotExist();

            ValidateSourceIntent(existing, fileId, content, x, y, positionType, session);
            await MarkSourcePendingAsync(sourceId, owner, session.Revision, ct);
        }

        await ReconcileAsync(projectId, session.Revision, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        await StageAsync("idempotent_touched", ct);
        await ClearSourcePendingAsync(sourceId, owner, session.Revision, ct);

        return new SourceWriteResult(
            created ? SourceWriteOutcome.Created : SourceWriteOutcome.AlreadyApplied,
            sourceId,
            session.Revision,
            true);
    }

    /// <summary>
    /// Create one translation with a stable id. Pass ObjectId.Empty to fall
    /// back to the legacy unique identity {o,t,u}. Empty content is a no-op
    /// (no row, no counter reconcile). <paramref name="context"/> is nullable
    /// for the same non-HTTP import-job reason as
    /// <see cref="CreateSourceOnceAsync"/>.
    /// </summary>
    public async Task<TranslationWriteResult> CreateTranslationOnceAsync(
        HttpContext? context,
        BsonDocument actor,
        ObjectId sourceId,
        ObjectId targetId,
        ObjectId translationId,
        string content,
        long expectedRevision,
        CancellationToken ct)
    {
        _ = context;
        var source = await RequireSourceAsync(sourceId, ct);
        var fileId = SourceFileId(source);
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await EnterAsync(projectId, ct);
        if (session.Revision != expectedRevision)
            throw LabelErrors.Busy("后台任务的标签版本已过期");

        var file = await RequireFileAsync(fileId, ct);
        await permissions.RequireProjectAsync(actor, session.Project, "project:ADD_TRA", ct);
        var target = await RequireTargetAsync(projectId, targetId, ct);
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);
        session.AssertReferenceEpoch(target);

        var actorId = IdValue(actor);
        var existing = translationId == ObjectId.Empty
            ? await Translations.Find(new BsonDocument
            {
                ["o"] = sourceId,
                ["t"] = targetId,
                ["u"] = actorId
            }).FirstOrDefaultAsync(ct)
            : await Translations.Find(new BsonDocument("_id", translationId))
                .FirstOrDefaultAsync(ct);

        if (content == "")
        {
            if (existing is not null)
            {
                session.AssertEpoch(existing);
                ValidateTranslationIdentity(existing, sourceId, targetId, actorId, null);
                return new TranslationWriteResult(
                    TranslationWriteOutcome.AlreadyApplied,
                    IdValue(existing),
                    session.Revision,
                    false);
            }

            return new TranslationWriteResult(
                TranslationWriteOutcome.Empty,
                translationId,
                session.Revision,
                false);
        }

        var owner = session.Lease.Owner;
        var now = Now();
        var created = false;

        if (existing is null)
        {
            var id = translationId == ObjectId.Empty
                ? ObjectId.GenerateNewId()
                : translationId;
            var editTime = await NextEditTimeAsync(sourceId, targetId, null, ct);
            var translation = new BsonDocument
            {
                ["_id"] = id,
                ["o"] = sourceId,
                ["t"] = targetId,
                ["c"] = content,
                ["u"] = actorId,
                ["p"] = "",
                ["s"] = false,
                ["mt"] = false,
                ["ct"] = now,
                ["e"] = editTime,
                ["label_epoch"] = session.Revision,
                [PendingField] = true,
                [PendingOwnerField] = owner,
                [PendingRevisionField] = session.Revision
            };
            try
            {
                await Translations.InsertOneAsync(translation, cancellationToken: ct);
                existing = translation;
                created = true;
                await StageAsync("translation_inserted", ct);
            }
            catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
            {
                existing = translationId == ObjectId.Empty
                    ? await Translations.Find(new BsonDocument
                    {
                        ["o"] = sourceId,
                        ["t"] = targetId,
                        ["u"] = actorId
                    }).FirstOrDefaultAsync(ct)
                    : await Translations.Find(new BsonDocument("_id", translationId))
                        .FirstOrDefaultAsync(ct);
                if (existing is null)
                    throw new ApiError("TranslationNotUniqueError", 8007, "翻译不唯一");
            }
        }

        if (!created)
        {
            existing ??= translationId == ObjectId.Empty
                ? await Translations.Find(new BsonDocument
                {
                    ["o"] = sourceId,
                    ["t"] = targetId,
                    ["u"] = actorId
                }).FirstOrDefaultAsync(ct)
                : await Translations.Find(new BsonDocument("_id", translationId))
                    .FirstOrDefaultAsync(ct);
            if (existing is null)
                throw TranslationNotExist();

            ValidateTranslationIdentity(existing, sourceId, targetId, actorId, content, session);
            await MarkTranslationPendingAsync(IdValue(existing), owner, session.Revision, ct);
        }

        var resultId = IdValue(existing!);
        await ReconcileAsync(projectId, session.Revision, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        await StageAsync("idempotent_touched", ct);
        await ClearTranslationPendingAsync(resultId, owner, session.Revision, ct);

        return new TranslationWriteResult(
            created ? TranslationWriteOutcome.Created : TranslationWriteOutcome.AlreadyApplied,
            resultId,
            session.Revision,
            true);
    }

    private async Task ReconcileAsync(
        ObjectId projectId,
        long revision,
        DateTime now,
        CancellationToken ct)
    {
        var reconciler = new LabelCounterReconciler(store, IdempotentHooks);
        await reconciler.ReconcileAsync(projectId, revision, now, ct);
    }

    private async Task StageAsync(string stage, CancellationToken ct)
    {
        if (IdempotentHooks?.AfterStageAsync is { } hook)
            await hook(stage, ct);
    }

    private static void ValidateSourceIntent(
        BsonDocument source,
        ObjectId fileId,
        string content,
        double x,
        double y,
        int positionType,
        LabelSession session)
    {
        session.AssertEpoch(source);
        var storedFile = ObjectIdOf(source.GetValue("f", BsonNull.Value));
        if (storedFile != fileId)
            throw SourceIntentConflict();

        if (!string.Equals(source.Text("c"), content, StringComparison.Ordinal) ||
            !SameDouble(source.GetValue("x", 0), x) ||
            !SameDouble(source.GetValue("y", 0), y) ||
            IntOf(source.GetValue("p", PositionIn)) != positionType)
            throw SourceIntentConflict();
    }

    private static void ValidateTranslationIdentity(
        BsonDocument translation,
        ObjectId sourceId,
        ObjectId targetId,
        ObjectId actorId,
        string? content,
        LabelSession? session = null)
    {
        session?.AssertEpoch(translation);
        if (ObjectIdOf(translation.GetValue("o", BsonNull.Value)) != sourceId ||
            ObjectIdOf(translation.GetValue("t", BsonNull.Value)) != targetId ||
            ObjectIdOf(translation.GetValue("u", BsonNull.Value)) != actorId)
            throw TranslationIntentConflict();

        if (translation.Flag("mt"))
            throw new ApiError("TranslationNotUniqueError", 8007, "翻译不唯一");

        if (content is not null &&
            !string.Equals(translation.Text("c"), content, StringComparison.Ordinal))
            throw TranslationIntentConflict();
    }

    private async Task MarkSourcePendingAsync(
        ObjectId sourceId,
        string owner,
        long revision,
        CancellationToken ct)
    {
        var result = await Sources.UpdateOneAsync(new BsonDocument("_id", sourceId),
            new BsonDocument("$set", new BsonDocument
            {
                [PendingField] = true,
                [PendingOwnerField] = owner,
                [PendingRevisionField] = revision
            }), cancellationToken: ct);
        if (result.MatchedCount == 0) throw SourceNotExist();
    }

    private async Task ClearSourcePendingAsync(
        ObjectId sourceId,
        string owner,
        long revision,
        CancellationToken ct)
    {
        await Sources.UpdateOneAsync(new BsonDocument
        {
            ["_id"] = sourceId,
            [PendingField] = true,
            [PendingOwnerField] = owner,
            [PendingRevisionField] = revision
        }, new BsonDocument
        {
            ["$unset"] = new BsonDocument
            {
                [PendingField] = "",
                [PendingOwnerField] = "",
                [PendingRevisionField] = ""
            },
            ["$set"] = new BsonDocument("label_epoch", revision)
        }, cancellationToken: ct);
    }

    private async Task MarkTranslationPendingAsync(
        ObjectId translationId,
        string owner,
        long revision,
        CancellationToken ct)
    {
        var result = await Translations.UpdateOneAsync(new BsonDocument("_id", translationId),
            new BsonDocument("$set", new BsonDocument
            {
                [PendingField] = true,
                [PendingOwnerField] = owner,
                [PendingRevisionField] = revision
            }), cancellationToken: ct);
        if (result.MatchedCount == 0) throw TranslationNotExist();
    }

    private async Task ClearTranslationPendingAsync(
        ObjectId translationId,
        string owner,
        long revision,
        CancellationToken ct)
    {
        await Translations.UpdateOneAsync(new BsonDocument
        {
            ["_id"] = translationId,
            [PendingField] = true,
            [PendingOwnerField] = owner,
            [PendingRevisionField] = revision
        }, new BsonDocument
        {
            ["$unset"] = new BsonDocument
            {
                [PendingField] = "",
                [PendingOwnerField] = "",
                [PendingRevisionField] = ""
            },
            ["$set"] = new BsonDocument("label_epoch", revision)
        }, cancellationToken: ct);
    }

    private static bool SameDouble(BsonValue value, double expected) =>
        Math.Abs(DoubleOf(value) - expected) < 1e-9;

    private static ApiError SourceIntentConflict() =>
        ApiError.Invalid("原文写入标识与已有原文不一致");

    private static ApiError TranslationIntentConflict() =>
        ApiError.Invalid("译文写入标识与已有译文不一致");
}

/// <summary>
/// Deterministic label-counter rebuild from primary source/translation rows.
/// It owns only label counters (sc/tsc/csc and canonical file_target_cache
/// ts/cs). It never touches file storage, file counts, derivatives or
/// accounting fields. The public surface is intentionally reusable by future
/// import code, but E1 never references E2's statistics implementation.
/// </summary>
public sealed class LabelCounterReconciler(
    MongoStore store,
    LabelIdempotentHooks? hooks = null)
{
    private const int BatchSize = 200;
    private const int WriteBatchSize = 500;
    private const int FolderType = 1;

    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> FileTargetCaches => store.Collection("file_target_cache");

    public async Task ReconcileAsync(
        ObjectId projectId,
        long revision,
        DateTime now,
        CancellationToken ct)
    {
        var files = await Files.Find(new BsonDocument("p", projectId)).ToListAsync(ct);
        var targets = await Targets.Find(new BsonDocument("t", projectId)).ToListAsync(ct);
        var targetIds = targets.Select(IdValue).ToList();
        var states = files.Select(file => new FileState(file)).ToList();
        var byId = states.ToDictionary(state => state.Id);

        await CountSourcesAsync(states, ct);
        await CountTranslationsAsync(states, targetIds, projectId, ct);

        long projectSc = 0;
        long projectTsc = 0;
        long projectCsc = 0;
        var targetTsc = targetIds.ToDictionary(id => id, _ => 0L);
        var targetCsc = targetIds.ToDictionary(id => id, _ => 0L);

        foreach (var state in states)
        {
            if (!state.IsFolder)
            {
                state.TranslatedCount = state.CacheTs.Values.Sum();
                state.CheckedCount = state.CacheCs.Values.Sum();
            }
        }

        // Active non-folder files contribute to project totals and roll up to
        // every ancestor. Inactive files keep their own counts but do not roll
        // up and do not contribute to project/target totals.
        foreach (var state in states)
        {
            if (state.IsFolder || !state.Active) continue;

            projectSc += state.SourceCount;
            projectTsc += state.TranslatedCount;
            projectCsc += state.CheckedCount;

            foreach (var targetId in targetIds)
            {
                if (!state.CacheTs.TryGetValue(targetId, out var ts)) continue;
                targetTsc[targetId] += ts;
                targetCsc[targetId] += state.CacheCs.GetValueOrDefault(targetId);
            }

            foreach (var ancestorId in state.Ancestors)
            {
                if (!byId.TryGetValue(ancestorId, out var ancestor))
                    throw ApiError.Invalid("项目文件祖先不存在");
                ancestor.SourceCount += state.SourceCount;
                ancestor.TranslatedCount += state.TranslatedCount;
                ancestor.CheckedCount += state.CheckedCount;
                foreach (var targetId in targetIds)
                {
                    ancestor.CacheTs[targetId] =
                        ancestor.CacheTs.GetValueOrDefault(targetId) +
                        state.CacheTs.GetValueOrDefault(targetId);
                    ancestor.CacheCs[targetId] =
                        ancestor.CacheCs.GetValueOrDefault(targetId) +
                        state.CacheCs.GetValueOrDefault(targetId);
                }
            }
        }

        await WriteFileCountsAsync(states, revision, ct);
        await StageAsync("reconcile_files", ct);

        await WriteCachesAsync(states, targetIds, revision, now, ct);
        await StageAsync("reconcile_cache", ct);

        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument
            {
                ["sc"] = projectSc,
                ["tsc"] = projectTsc,
                ["csc"] = projectCsc
            }), cancellationToken: ct);
        await StageAsync("reconcile_project", ct);

        await WriteTargetsAsync(targets, targetTsc, targetCsc, revision, ct);
        await StageAsync("reconcile_targets", ct);
    }

    private async Task CountSourcesAsync(
        IReadOnlyList<FileState> states,
        CancellationToken ct)
    {
        var ids = states.Where(state => !state.IsFolder).Select(state => state.Id).ToList();
        var byId = states.ToDictionary(state => state.Id);

        foreach (var chunk in Chunks(ids, BatchSize))
        {
            using var cursor = await Sources.Find(new BsonDocument
            {
                ["f"] = new BsonDocument("$in", new BsonArray(chunk)),
                ["b"] = new BsonDocument("$ne", true)
            }).Project(Builders<BsonDocument>.Projection.Include("f"))
                .ToCursorAsync(ct);

            while (await cursor.MoveNextAsync(ct))
            {
                foreach (var row in cursor.Current)
                {
                    if (ObjectIdOf(row.GetValue("f", BsonNull.Value)) is { } fileId &&
                        byId.TryGetValue(fileId, out var state))
                        state.SourceCount++;
                }
            }
        }
    }

    private async Task CountTranslationsAsync(
        IReadOnlyList<FileState> states,
        IReadOnlyList<ObjectId> targetIds,
        ObjectId projectId,
        CancellationToken ct)
    {
        if (targetIds.Count == 0) return;

        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["t"] = new BsonDocument("$in", new BsonArray(targetIds))
            }),
            new BsonDocument("$lookup", new BsonDocument
            {
                ["from"] = "source",
                ["localField"] = "o",
                ["foreignField"] = "_id",
                ["as"] = "src"
            }),
            new BsonDocument("$unwind", "$src"),
            new BsonDocument("$match", new BsonDocument
            {
                ["src.b"] = new BsonDocument("$ne", true)
            }),
            new BsonDocument("$lookup", new BsonDocument
            {
                ["from"] = "file",
                ["localField"] = "src.f",
                ["foreignField"] = "_id",
                ["as"] = "file"
            }),
            new BsonDocument("$unwind", "$file"),
            new BsonDocument("$match", new BsonDocument
            {
                ["file.p"] = projectId
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument
                {
                    ["f"] = "$file._id",
                    ["t"] = "$t",
                    ["o"] = "$o"
                },
                ["checked"] = new BsonDocument("$max", new BsonDocument("$cond",
                    new BsonArray { "$s", 1, 0 }))
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument
                {
                    ["f"] = "$_id.f",
                    ["t"] = "$_id.t"
                },
                ["ts"] = new BsonDocument("$sum", 1),
                ["cs"] = new BsonDocument("$sum", "$checked")
            })
        });

        var byId = states.ToDictionary(state => state.Id);
        using var cursor = await Translations.AggregateAsync(pipeline, cancellationToken: ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var row in cursor.Current)
            {
                if (row.GetValue("_id", BsonNull.Value) is not BsonDocument group) continue;
                if (ObjectIdOf(group.GetValue("f", BsonNull.Value)) is not { } fileId) continue;
                if (ObjectIdOf(group.GetValue("t", BsonNull.Value)) is not { } targetId) continue;
                if (!byId.TryGetValue(fileId, out var state)) continue;
                state.CacheTs[targetId] = IntOf(row.GetValue("ts", 0));
                state.CacheCs[targetId] = IntOf(row.GetValue("cs", 0));
            }
        }
    }

    private async Task WriteFileCountsAsync(
        IReadOnlyList<FileState> states,
        long revision,
        CancellationToken ct)
    {
        var models = states.Select(state => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
            new BsonDocument("_id", state.Id),
            new BsonDocument("$set", new BsonDocument
            {
                ["sc"] = state.SourceCount,
                ["tsc"] = state.TranslatedCount,
                ["csc"] = state.CheckedCount,
                ["label_epoch"] = revision
            }))).ToList();
        await BulkAsync(Files, models, ct);
    }

    private async Task WriteCachesAsync(
        IReadOnlyList<FileState> states,
        IReadOnlyList<ObjectId> targetIds,
        long revision,
        DateTime now,
        CancellationToken ct)
    {
        var models = new List<WriteModel<BsonDocument>>();
        foreach (var state in states)
        {
            foreach (var targetId in targetIds)
            {
                models.Add(new UpdateOneModel<BsonDocument>(
                    new BsonDocument
                    {
                        ["f"] = state.Id,
                        ["t"] = targetId
                    },
                    new BsonDocument
                    {
                        ["$set"] = new BsonDocument
                        {
                            ["ts"] = state.CacheTs.GetValueOrDefault(targetId),
                            ["cs"] = state.CacheCs.GetValueOrDefault(targetId),
                            ["label_epoch"] = revision
                        },
                        ["$setOnInsert"] = new BsonDocument
                        {
                            ["f"] = state.Id,
                            ["t"] = targetId,
                            ["e"] = now
                        }
                    })
                {
                    IsUpsert = true
                });
            }
        }
        await BulkAsync(FileTargetCaches, models, ct);
    }

    private async Task WriteTargetsAsync(
        IReadOnlyList<BsonDocument> targets,
        IReadOnlyDictionary<ObjectId, long> targetTsc,
        IReadOnlyDictionary<ObjectId, long> targetCsc,
        long revision,
        CancellationToken ct)
    {
        var models = targets.Select(target =>
        {
            var id = IdValue(target);
            return (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", id),
                new BsonDocument("$set", new BsonDocument
                {
                    ["tsc"] = targetTsc.GetValueOrDefault(id),
                    ["csc"] = targetCsc.GetValueOrDefault(id),
                    ["label_epoch"] = revision
                }));
        }).ToList();
        await BulkAsync(Targets, models, ct);
    }

    private async Task StageAsync(string stage, CancellationToken ct)
    {
        if (hooks?.AfterStageAsync is { } hook)
            await hook(stage, ct);
    }

    private static async Task BulkAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<WriteModel<BsonDocument>> models,
        CancellationToken ct)
    {
        for (var offset = 0; offset < models.Count; offset += WriteBatchSize)
        {
            var chunk = models.Skip(offset).Take(WriteBatchSize).ToList();
            if (chunk.Count > 0)
                await collection.BulkWriteAsync(chunk, cancellationToken: ct);
        }
    }

    private static IEnumerable<List<ObjectId>> Chunks(IReadOnlyList<ObjectId> ids, int size)
    {
        for (var offset = 0; offset < ids.Count; offset += size)
            yield return ids.Skip(offset).Take(size).ToList();
    }

    private static ObjectId IdValue(BsonDocument document) =>
        ObjectIdOf(document.GetValue("_id", BsonNull.Value)) ??
        throw ApiError.Invalid("Invalid object id");

    private static ObjectId? ObjectIdOf(BsonValue value)
    {
        if (value.IsObjectId) return value.AsObjectId;
        if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed)) return parsed;
        return null;
    }

    private static int IntOf(BsonValue value)
    {
        try { return value.IsNumeric ? value.ToInt32() : 0; }
        catch (Exception) { return 0; }
    }

    private sealed class FileState
    {
        public FileState(BsonDocument document)
        {
            Document = document;
            Id = IdValue(document);
            ProjectId = ObjectIdOf(document.GetValue("p", BsonNull.Value)) ?? ObjectId.Empty;
            IsFolder = IntOf(document.GetValue("t", 0)) == FolderType;
            Active = document.Flag("ac");
            if (document.TryGetValue("a", out var ancestors) && ancestors is BsonArray array)
            {
                foreach (var value in array)
                {
                    if (ObjectIdOf(value) is { } id) Ancestors.Add(id);
                }
            }
        }

        public BsonDocument Document { get; }
        public ObjectId Id { get; }
        public ObjectId ProjectId { get; }
        public bool IsFolder { get; }
        public bool Active { get; }
        public List<ObjectId> Ancestors { get; } = new();
        public int SourceCount { get; set; }
        public int TranslatedCount { get; set; }
        public int CheckedCount { get; set; }
        public Dictionary<ObjectId, int> CacheTs { get; } = new();
        public Dictionary<ObjectId, int> CacheCs { get; } = new();
    }
}
