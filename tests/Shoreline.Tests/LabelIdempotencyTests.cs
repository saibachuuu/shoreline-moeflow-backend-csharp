using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class LabelIdempotencyTests(LocalServices services)
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Source_retry_after_insert_then_exception_converges()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("source_inserted");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "alpha", 0.1, 0.2, 1, 0, world.Revision, CancellationToken.None));

        var inserted = await LoadAsync(world, "source", sourceId);
        Assert.True(inserted.Flag("idempotent_pending"));

        world.Labels.IdempotentHooks = null;
        var retried = await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId,
            sourceId, "alpha", 0.1, 0.2, 1, 0, world.Revision, CancellationToken.None);

        Assert.Equal(SourceWriteOutcome.AlreadyApplied, retried.Outcome);
        Assert.True(retried.CountersApplied);
        Assert.Equal(1, await CountAsync(world, "source", world.FileId));
        Assert.Equal(1, (await LoadAsync(world, "file", world.FileId)).Integer("sc"));
        Assert.Equal(1, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
        Assert.False((await LoadAsync(world, "source", sourceId)).Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Source_failure_after_file_counts_converges_without_double_count()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("reconcile_files");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "bravo", 0, 0, 1, 0, world.Revision, CancellationToken.None));

        world.Labels.IdempotentHooks = null;
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "bravo", 0, 0, 1, 0, world.Revision, CancellationToken.None);
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "bravo", 0, 0, 1, 0, world.Revision, CancellationToken.None);

        Assert.Equal(1, await CountAsync(world, "source", world.FileId));
        Assert.Equal(1, (await LoadAsync(world, "file", world.FileId)).Integer("sc"));
        Assert.Equal(1, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
    }

    [Fact]
    public async Task Source_failure_after_project_counts_converges()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("reconcile_project");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "charlie", 0.5, 0.5, 2, 0, world.Revision, CancellationToken.None));

        world.Labels.IdempotentHooks = null;
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "charlie", 0.5, 0.5, 2, 0, world.Revision, CancellationToken.None);
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "charlie", 0.5, 0.5, 2, 0, world.Revision, CancellationToken.None);

        Assert.Equal(1, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
        Assert.Equal(1, (await LoadAsync(world, "file", world.FileId)).Integer("sc"));
    }

    [Fact]
    public async Task Source_failure_after_cache_counts_converges()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("reconcile_cache");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "delta", 0, 1, 1, 0, world.Revision, CancellationToken.None));

        world.Labels.IdempotentHooks = null;
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "delta", 0, 1, 1, 0, world.Revision, CancellationToken.None);
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "delta", 0, 1, 1, 0, world.Revision, CancellationToken.None);

        var cache = await LoadAsync(world, "file_target_cache", world.CacheId);
        Assert.Equal(0, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
    }

    [Fact]
    public async Task Source_failure_after_target_counts_converges()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("reconcile_targets");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "echo", 0.2, 0.3, 1, 0, world.Revision, CancellationToken.None));

        world.Labels.IdempotentHooks = null;
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "echo", 0.2, 0.3, 1, 0, world.Revision, CancellationToken.None);
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
            "echo", 0.2, 0.3, 1, 0, world.Revision, CancellationToken.None);

        Assert.Equal(1, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
    }

    [Fact]
    public async Task Translation_retry_after_insert_then_exception_converges()
    {
        var world = await WorldAsync();
        var source = await CreateSourceAsync(world, "source");
        var translationId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("translation_inserted");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source, world.TargetId,
                translationId, "译文", world.Revision, CancellationToken.None));

        Assert.Equal(1, await CountAsync(world, "translation", source));
        var inserted = await LoadAsync(world, "translation", translationId);
        Assert.True(inserted.Flag("idempotent_pending"));

        world.Labels.IdempotentHooks = null;
        var retried = await world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source,
            world.TargetId, translationId, "译文", world.Revision, CancellationToken.None);

        Assert.Equal(TranslationWriteOutcome.AlreadyApplied, retried.Outcome);
        Assert.True(retried.CountersApplied);
        Assert.Equal(1, await CountAsync(world, "translation", source));
        var cache = await LoadAsync(world, "file_target_cache", world.CacheId);
        Assert.Equal(1, cache.Integer("ts"));
        Assert.Equal(1, (await LoadAsync(world, "target", world.TargetId)).Integer("tsc"));
        Assert.False((await LoadAsync(world, "translation", translationId)).Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Translation_failure_after_reconcile_stage_converges()
    {
        var world = await WorldAsync();
        var source = await CreateSourceAsync(world, "source");
        var translationId = ObjectId.GenerateNewId();
        world.Labels.IdempotentHooks = Hooks("reconcile_cache");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source, world.TargetId,
                translationId, "译文", world.Revision, CancellationToken.None));

        Assert.True((await LoadAsync(world, "translation", translationId)).Flag("idempotent_pending"));

        world.Labels.IdempotentHooks = null;
        await world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source, world.TargetId,
            translationId, "译文", world.Revision, CancellationToken.None);
        await world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source, world.TargetId,
            translationId, "译文", world.Revision, CancellationToken.None);

        Assert.Equal(1, await CountAsync(world, "translation", source));
        Assert.Equal(1, (await LoadAsync(world, "file_target_cache", world.CacheId)).Integer("ts"));
        Assert.False((await LoadAsync(world, "translation", translationId)).Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Translation_same_id_on_another_source_is_rejected_without_changes()
    {
        var world = await WorldAsync();
        var firstSource = await CreateSourceAsync(world, "first");
        var secondSource = await CreateSourceAsync(world, "second");
        var translationId = ObjectId.GenerateNewId();
        await InsertTranslationAsync(world, firstSource, world.TargetId, selected: false,
            content: "translation", userId: world.ActorId, translationId: translationId);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, secondSource,
                world.TargetId, translationId, "translation", world.Revision, CancellationToken.None));

        Assert.Equal(2, error.Code);
        var row = await LoadAsync(world, "translation", translationId);
        Assert.Equal(firstSource, row["o"].AsObjectId);
        Assert.False(row.Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Translation_empty_content_is_a_noop()
    {
        var world = await WorldAsync();
        var source = await CreateSourceAsync(world, "source");
        var translationId = ObjectId.GenerateNewId();

        var result = await world.Labels.CreateTranslationOnceAsync(Context(), world.Actor, source,
            world.TargetId, translationId, "", world.Revision, CancellationToken.None);

        Assert.Equal(TranslationWriteOutcome.Empty, result.Outcome);
        Assert.False(result.CountersApplied);
        Assert.Equal(0, await CountAsync(world, "translation", source));
        Assert.Equal(0, (await LoadAsync(world, "file_target_cache", world.CacheId)).Integer("ts"));
    }

    [Fact]
    public async Task Source_same_id_on_another_file_is_rejected_without_changes()
    {
        var world = await WorldAsync();
        var otherFile = await AddFileAsync(world);
        var sourceId = ObjectId.GenerateNewId();
        await InsertSourceAsync(world, otherFile, sourceId, "other", 0, 0, 1, 0);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "other", 0, 0, 1, 0, world.Revision, CancellationToken.None));

        Assert.Equal(2, error.Code);
        var row = await LoadAsync(world, "source", sourceId);
        Assert.Equal(otherFile, row["f"].AsObjectId);
        Assert.False(row.Flag("idempotent_pending"));
        Assert.Equal(0, (await LoadAsync(world, "file", world.FileId)).Integer("sc"));
    }

    [Fact]
    public async Task Source_same_id_with_different_intent_is_rejected_without_changes()
    {
        var world = await WorldAsync();
        var sourceId = ObjectId.GenerateNewId();
        await InsertSourceAsync(world, world.FileId, sourceId, "original", 0.1, 0.2, 1, 0);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "changed", 0.1, 0.2, 1, 0, world.Revision, CancellationToken.None));

        Assert.Equal(2, error.Code);
        var row = await LoadAsync(world, "source", sourceId);
        Assert.Equal("original", row.Text("c"));
        Assert.False(row.Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Source_same_id_with_stale_epoch_is_409_without_changes()
    {
        var world = await WorldAsync(revision: 1);
        var sourceId = ObjectId.GenerateNewId();
        await InsertSourceAsync(world, world.FileId, sourceId, "old", 0.1, 0.2, 1, 0, epoch: 0);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "old", 0.1, 0.2, 1, 0, world.Revision, CancellationToken.None));

        Assert.Equal(1, error.Code);
        Assert.Equal(409, error.Status);
        var row = await LoadAsync(world, "source", sourceId);
        Assert.Equal(0L, row.Integer("label_epoch"));
        Assert.False(row.Flag("idempotent_pending"));
    }

    [Fact]
    public async Task Source_stale_expected_revision_is_409_and_writes_nothing()
    {
        var world = await WorldAsync(revision: 2);
        var sourceId = ObjectId.GenerateNewId();

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, sourceId,
                "stale", 0, 0, 1, 0, 1, CancellationToken.None));

        Assert.Equal(1, error.Code);
        Assert.Equal(409, error.Status);
        Assert.Null(await FindAsync(world, "source", sourceId));
        Assert.Equal(0, (await LoadAsync(world, "file", world.FileId)).Integer("sc"));
        Assert.Equal(0, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
    }

    [Fact]
    public async Task Source_rank_collision_does_not_claim_another_source()
    {
        var world = await WorldAsync();
        await InsertSourceAsync(world, world.FileId, ObjectId.GenerateNewId(), "existing", 0, 0, 1, 7);
        var sourceId = ObjectId.GenerateNewId();

        var first = await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId,
            sourceId, "new", 0.1, 0.1, 1, 7, world.Revision, CancellationToken.None);
        var second = await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId,
            sourceId, "new", 0.1, 0.1, 1, 7, world.Revision, CancellationToken.None);

        Assert.Equal(SourceWriteOutcome.Created, first.Outcome);
        Assert.Equal(SourceWriteOutcome.AlreadyApplied, second.Outcome);
        Assert.Equal(2, await CountAsync(world, "source", world.FileId));
        Assert.Equal(1, await CountAsync(world, "source", world.FileId, "new"));
    }

    [Fact]
    public async Task Folder_rolls_up_only_active_nonblank_sources_and_translations()
    {
        var world = await WorldAsync(withFolder: true);
        var source = await CreateSourceAsync(world, "folder source");
        await InsertTranslationAsync(world, source, world.TargetId, selected: true, content: "tra");

        var reconciler = new LabelCounterReconciler(world.Store);
        await reconciler.ReconcileAsync(world.ProjectId, world.Revision, Now, CancellationToken.None);

        var child = await LoadAsync(world, "file", world.FileId);
        var folder = await LoadAsync(world, "file", world.FolderId);
        Assert.Equal(1, child.Integer("sc"));
        Assert.Equal(1, folder.Integer("sc"));
        Assert.Equal(1, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));

        await world.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("ac", false)));
        await reconciler.ReconcileAsync(world.ProjectId, world.Revision, Now, CancellationToken.None);

        child = await LoadAsync(world, "file", world.FileId);
        folder = await LoadAsync(world, "file", world.FolderId);
        Assert.Equal(1, child.Integer("sc"));
        Assert.Equal(0, folder.Integer("sc"));
        Assert.Equal(0, (await LoadAsync(world, "project", world.ProjectId)).Integer("sc"));
    }

    [Fact]
    public async Task Blank_sources_are_excluded_but_selected_distinct_sources_are_counted()
    {
        var world = await WorldAsync();
        var target2 = await AddTargetAsync(world);
        var blankSource = ObjectId.GenerateNewId();
        var realSource = ObjectId.GenerateNewId();
        await InsertSourceAsync(world, world.FileId, blankSource, "  ", 0, 0, 1, 0, blank: true);
        await InsertSourceAsync(world, world.FileId, realSource, "real", 0, 0, 1, 1);
        await InsertTranslationAsync(world, blankSource, world.TargetId, selected: true, content: "blank tra");
        await InsertTranslationAsync(world, realSource, target2, selected: true, content: "real tra");
        await InsertTranslationAsync(world, realSource, target2, selected: false, content: "other tra",
            userId: ObjectId.GenerateNewId());

        var reconciler = new LabelCounterReconciler(world.Store);
        await reconciler.ReconcileAsync(world.ProjectId, world.Revision, Now, CancellationToken.None);

        var file = await LoadAsync(world, "file", world.FileId);
        Assert.Equal(1, file.Integer("sc"));
        Assert.Equal(1, file.Integer("tsc"));
        Assert.Equal(1, file.Integer("csc"));
        var target1 = await LoadAsync(world, "target", world.TargetId);
        Assert.Equal(0, target1.Integer("tsc"));
        Assert.Equal(0, target1.Integer("csc"));
        var target2Row = await LoadAsync(world, "target", target2);
        Assert.Equal(1, target2Row.Integer("tsc"));
        Assert.Equal(1, target2Row.Integer("csc"));
    }

    [Fact]
    public async Task Http_context_borrows_the_already_held_lease_without_deadlock()
    {
        var world = await WorldAsync();
        var context = new LabelRequestContext();
        await using (await world.Lock.EnterAsync(context, world.ProjectId, false, world.Revision,
                         CancellationToken.None))
        {
            var labels = new LabelService(world.Store, TimeProvider.System,
                new IdentityPermissions(world.Store, TimeProvider.System),
                new UserView(services.Configuration, services.App.Services),
                services.App.Services, world.Lock, context);
            var sourceId = ObjectId.GenerateNewId();
            var result = await labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId,
                sourceId, "borrowed", 0, 0, 1, 0, world.Revision, CancellationToken.None);
            Assert.Equal(SourceWriteOutcome.Created, result.Outcome);
        }

        Assert.Equal(0, await world.Store.Collection("project_label_lock")
            .CountDocumentsAsync(new BsonDocument("_id", world.ProjectId)));
    }

    private static LabelIdempotentHooks Hooks(string failingStage) => new()
    {
        AfterStageAsync = (stage, _) => stage == failingStage
            ? throw new InvalidOperationException("injected " + stage)
            : Task.CompletedTask
    };

    private async Task<ObjectId> CreateSourceAsync(World world, string content)
    {
        var id = ObjectId.GenerateNewId();
        await world.Labels.CreateSourceOnceAsync(Context(), world.Actor, world.FileId, id, content,
            0, 0, 1, 0, world.Revision, CancellationToken.None);
        return id;
    }

    private async Task<ObjectId> AddTargetAsync(World world)
    {
        var id = ObjectId.GenerateNewId();
        await world.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["t"] = world.ProjectId,
            ["l"] = world.LanguageId,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ct"] = Now,
            ["et"] = Now,
            ["i"] = "",
            ["label_epoch"] = world.Revision
        });
        return id;
    }

    private async Task<ObjectId> AddFileAsync(World world, bool active = true)
    {
        var id = ObjectId.GenerateNewId();
        await world.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "file-" + id,
            ["t"] = 2,
            ["p"] = world.ProjectId,
            ["a"] = new BsonArray(),
            ["ac"] = active,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = Now,
            ["label_epoch"] = world.Revision
        });
        return id;
    }

    private async Task InsertSourceAsync(World world, ObjectId fileId, ObjectId sourceId,
        string content, double x, double y, int position, int rank, bool blank = false,
        long? epoch = null)
    {
        await world.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = sourceId,
            ["f"] = fileId,
            ["r"] = rank,
            ["c"] = content,
            ["x"] = x,
            ["y"] = y,
            ["v"] = new BsonArray(),
            ["p"] = position,
            ["lf"] = true,
            ["b"] = blank,
            ["cm"] = false,
            ["pt"] = new BsonArray(),
            ["ct"] = Now,
            ["e"] = Now,
            ["label_epoch"] = epoch ?? world.Revision
        });
    }

    private async Task InsertTranslationAsync(World world, ObjectId sourceId, ObjectId targetId,
        bool selected, string content, ObjectId? userId = null, ObjectId? translationId = null)
    {
        await world.Store.Collection("translation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = translationId ?? ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = targetId,
            ["c"] = content,
            ["u"] = userId ?? world.ActorId,
            ["p"] = "",
            ["s"] = selected,
            ["mt"] = false,
            ["ct"] = Now,
            ["e"] = Now,
            ["label_epoch"] = world.Revision
        });
    }

    private static HttpContext Context() => new DefaultHttpContext();

    private static async Task<BsonDocument> LoadAsync(World world, string collection, ObjectId id) =>
        await world.Store.Collection(collection)
            .Find(new BsonDocument("_id", id)).FirstOrDefaultAsync()
        ?? throw new InvalidOperationException($"missing {collection}/{id}");

    private static async Task<BsonDocument?> FindAsync(World world, string collection, ObjectId id) =>
        await world.Store.Collection(collection)
            .Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();

    private static async Task<long> CountAsync(World world, string collection, ObjectId key,
        string? content = null)
    {
        var filter = new BsonDocument
        {
            [collection == "translation" ? "o" : "f"] = key
        };
        if (content is not null) filter["c"] = content;
        return await world.Store.Collection(collection).CountDocumentsAsync(filter);
    }

    private async Task<World> WorldAsync(bool withFolder = false, bool active = true, long revision = 0)
    {
        var store = services.Store;
        var clock = TimeProvider.System;
        var actorId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var folderId = ObjectId.GenerateNewId();
        var fileId = ObjectId.GenerateNewId();
        var targetId = ObjectId.GenerateNewId();
        var cacheId = ObjectId.GenerateNewId();
        var languageId = ObjectId.GenerateNewId();
        var teamId = ObjectId.GenerateNewId();
        var suffix = Guid.NewGuid().ToString("N");

        var actor = new BsonDocument
        {
            ["_id"] = actorId,
            ["n"] = "actor-" + suffix,
            ["e"] = "actor-" + suffix + "@example.test",
            ["as"] = new BsonArray(),
            ["l"] = "auto",
            ["admin"] = false,
            ["b"] = false
        };
        await store.Collection("user").InsertOneAsync(actor);
        await store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "project-" + suffix,
            ["st"] = new BsonInt32(0),
            ["t"] = teamId,
            ["ou"] = actorId,
            ["m_e"] = Now,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["label_revision"] = revision
        });
        await store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["user"] = actorId,
            ["status"] = "active",
            ["tags"] = new BsonArray { "creator" }
        });
        await store.Collection("language").InsertOneAsync(new BsonDocument
        {
            ["_id"] = languageId,
            ["c"] = "中文",
            ["e"] = "Chinese",
            ["co"] = "zh-" + suffix,
            ["gt"] = "zh-" + suffix,
            ["go"] = "zh-" + suffix,
            ["n"] = false,
            ["s"] = 1
        });

        if (withFolder)
        {
            await store.Collection("file").InsertOneAsync(new BsonDocument
            {
                ["_id"] = folderId,
                ["n"] = "folder",
                ["t"] = 1,
                ["p"] = projectId,
                ["a"] = new BsonArray(),
                ["ac"] = true,
                ["sc"] = 0,
                ["tsc"] = 0,
                ["csc"] = 0,
                ["et"] = Now,
                ["label_epoch"] = revision
            });
        }

        var file = new BsonDocument
        {
            ["_id"] = fileId,
            ["n"] = "file",
            ["t"] = 2,
            ["p"] = projectId,
            ["a"] = withFolder
                ? new BsonArray { folderId }
                : new BsonArray(),
            ["ac"] = active,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = Now,
            ["label_epoch"] = revision
        };
        await store.Collection("file").InsertOneAsync(file);
        await store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = targetId,
            ["t"] = projectId,
            ["l"] = languageId,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ct"] = Now,
            ["et"] = Now,
            ["i"] = "",
            ["label_epoch"] = revision
        });
        await store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
        {
            ["_id"] = cacheId,
            ["f"] = fileId,
            ["t"] = targetId,
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = Now,
            ["label_epoch"] = revision
        });

        var lockObject = new ProjectLabelLock(store, clock);
        var labels = new LabelService(store, clock,
            new IdentityPermissions(store, clock),
            new UserView(services.Configuration, services.App.Services),
            services.App.Services, lockObject, new LabelRequestContext());
        await labels.InitializeAsync(CancellationToken.None);

        return new World
        {
            Store = store,
            Actor = actor,
            ActorId = actorId,
            ProjectId = projectId,
            FileId = fileId,
            FolderId = folderId,
            TargetId = targetId,
            CacheId = cacheId,
            LanguageId = languageId,
            TeamId = teamId,
            Revision = revision,
            Lock = lockObject,
            Labels = labels
        };
    }

    private sealed class World
    {
        public required MongoStore Store { get; init; }
        public required BsonDocument Actor { get; init; }
        public required ObjectId ActorId { get; init; }
        public required ObjectId ProjectId { get; init; }
        public required ObjectId FileId { get; init; }
        public required ObjectId FolderId { get; init; }
        public required ObjectId TargetId { get; init; }
        public required ObjectId CacheId { get; init; }
        public required ObjectId LanguageId { get; init; }
        public required ObjectId TeamId { get; init; }
        public required long Revision { get; init; }
        public required ProjectLabelLock Lock { get; init; }
        public required LabelService Labels { get; init; }
    }
}
