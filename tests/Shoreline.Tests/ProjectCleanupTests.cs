using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

// Direct-service coverage for the cleanup compatibility layer. These facts use
// the real legacy BSON field names (p/t/o/f/sa/file_name/sb), never the old
// HTTP fixtures' synthetic project fields. Local object storage is a temporary
// directory owned by each fact and removed in Fixture.DisposeAsync.
[Collection("local-services")]
public sealed class ProjectCleanupTests(LocalServices services)
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private MongoStore Store => services.Store;

    // ------------------------------------------------------------------ facts

    [Fact]
    public async Task ClearProjectRemovesLegacyRowsAndObjectsButRetainsOtherProject()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 4, tc: 2);
        var actor = await fx.AddCreatorAsync(project);
        var target = await fx.AddTargetAsync(project);
        await fx.AddProjectMemberAsync(project);

        var file = await fx.AddFileAsync(project, "page.png");
        var folder = await fx.AddFileAsync(project, "folder", type: 1);
        var source = await fx.AddSourceAsync(file);
        await fx.AddTranslationAsync(source, target);
        await fx.AddTipAsync(source, target);
        await fx.AddCacheAsync(file, target);
        var output = await fx.AddOutputAsync(project, target, "result.pdf", 4096);

        var other = await fx.AddProjectAsync(stv: 7, tc: 3);
        var otherFile = await fx.AddFileAsync(other, "other.png");
        var otherTarget = await fx.AddTargetAsync(other);
        var otherSource = await fx.AddSourceAsync(otherFile);
        await fx.AddTranslationAsync(otherSource, otherTarget);
        await fx.AddTipAsync(otherSource, otherTarget);
        await fx.AddCacheAsync(otherFile, otherTarget);
        var otherOutput = await fx.AddOutputAsync(other, otherTarget, "other.pdf", 1024);

        var originalKey = "files/page.png";
        var coverKey = "files/cover-page.webp";
        var resampleKey = "files/resample-page.webp";
        var proofreadKey = "files/proofread-deadbeef-page.webp";
        var outputKey = OutputKey(output);
        var otherFileKey = "files/other.png";
        var otherOutputKey = OutputKey(otherOutput);
        foreach (var key in new[] { originalKey, coverKey, resampleKey, proofreadKey, outputKey, otherFileKey, otherOutputKey })
            await PutObjectAsync(storage, key);

        var lifecycle = NewLifecycle(storage, config);
        var (changed, projectAfter) = await RunClearAsync(lifecycle, actor, project, "NORMAL", 4);

        Assert.True(changed);
        Assert.Equal(1, (int)projectAfter.Integer("st"));
        Assert.Equal(5L, projectAfter.Integer("stv"));
        Assert.False(projectAfter.Flag("cip"));
        Assert.False(projectAfter.Contains("clear_owner"));

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0L, reloaded.Integer("fs"));
        Assert.Equal(0L, reloaded.Integer("cover_bytes"));
        Assert.Equal(0L, reloaded.Integer("resample_bytes"));
        Assert.Equal(0L, reloaded.Integer("ob"));
        Assert.Equal(0, (int)reloaded.Integer("fo"));
        Assert.Equal(0, (int)reloaded.Integer("fc"));
        Assert.Equal(0, (int)reloaded.Integer("sc"));
        Assert.Equal(0, (int)reloaded.Integer("tsc"));
        Assert.Equal(0, (int)reloaded.Integer("csc"));
        Assert.Equal(2L, reloaded.Integer("tc"));

        Assert.Equal(0L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.Equal(0L, await CountAsync("output", new BsonDocument("p", project["_id"])));
        Assert.Equal(0L, await CountAsync("source", new BsonDocument("f", file["_id"])));
        Assert.Equal(0L, await CountAsync("translation", new BsonDocument("o", source["_id"])));
        Assert.Equal(0L, await CountAsync("tip", new BsonDocument("o", source["_id"])));
        Assert.Equal(0L, await CountAsync("file_target_cache", new BsonDocument("f", file["_id"])));
        Assert.Equal(1L, await CountAsync("target", new BsonDocument("_id", target["_id"])));
        Assert.Equal(1L, await CountAsync("project_member", new BsonDocument("project", project["_id"])));
        Assert.Equal(0L, await CountAsync("file", new BsonDocument("_id", folder["_id"])));

        Assert.Equal(1L, await CountAsync("file", new BsonDocument("_id", otherFile["_id"])));
        Assert.Equal(1L, await CountAsync("source", new BsonDocument("_id", otherSource["_id"])));
        Assert.Equal(1L, await CountAsync("translation", new BsonDocument("o", otherSource["_id"])));
        Assert.Equal(1L, await CountAsync("tip", new BsonDocument("o", otherSource["_id"])));
        Assert.Equal(1L, await CountAsync("file_target_cache", new BsonDocument("f", otherFile["_id"])));
        Assert.Equal(1L, await CountAsync("target", new BsonDocument("_id", otherTarget["_id"])));
        Assert.Equal(1L, await CountAsync("output", new BsonDocument("_id", otherOutput["_id"])));

        Assert.False(await ExistsAsync(storage, originalKey));
        Assert.False(await ExistsAsync(storage, coverKey));
        Assert.False(await ExistsAsync(storage, resampleKey));
        Assert.False(await ExistsAsync(storage, outputKey));
        Assert.True(await ExistsAsync(storage, proofreadKey));
        Assert.True(await ExistsAsync(storage, otherFileKey));
        Assert.True(await ExistsAsync(storage, otherOutputKey));
    }

    [Fact]
    public async Task ClearFailsClosedWhenStorageSourceIdIsSet()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 1, csid: ObjectId.GenerateNewId());
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "page.png");

        await PutObjectAsync(storage, "files/page.png");

        var lifecycle = NewLifecycle(storage, config);
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
        {
            await lifecycle.ClearAsync(actor, project, Payload("NORMAL", 1), "req-csid", CancellationToken.None);
        });

        Assert.Equal(5113, error.Code);
        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(1L, reloaded.Integer("stv"));
        Assert.False(reloaded.Flag("cip"));
        Assert.Equal(1L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.True(await ExistsAsync(storage, "files/page.png"));
        Assert.Equal(1L, await CountAsync("identity_audit_event",
            new BsonDocument { ["action"] = "project_clear_retryable_failure", ["project_id"] = project["_id"].ToString() }));
    }

    [Fact]
    public async Task ClearWithStaleExpectedVersionDestroysNothing()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 3);
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "keep.png");
        await PutObjectAsync(storage, "files/keep.png");

        var lifecycle = NewLifecycle(storage, config);
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
        {
            await lifecycle.ClearAsync(actor, project, Payload("NORMAL", 2), "req-stale", CancellationToken.None);
        });

        Assert.Equal(5112, error.Code);
        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(3L, reloaded.Integer("stv"));
        Assert.False(reloaded.Flag("cip"));
        Assert.Equal(1L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.True(await ExistsAsync(storage, "files/keep.png"));
    }

    [Fact]
    public async Task ClearStorageFailureLeavesRowsAndCanBeRetried()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 4);
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "blocked");
        await fx.AddFileAsync(project, "after.png");
        await PutObjectAsync(storage, "files/after.png");

        // Local storage refuses to delete a directory key, which injects a real
        // mid-clear storage failure without a fake storage implementation.
        Directory.CreateDirectory(Path.Combine(fx.Root!, "files", "blocked"));

        var lifecycle = NewLifecycle(storage, config);
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
        {
            await lifecycle.ClearAsync(actor, project, Payload("NORMAL", 4), "req-fail", CancellationToken.None);
        });
        Assert.Equal(5113, error.Code);

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(4L, reloaded.Integer("stv"));
        Assert.False(reloaded.Flag("cip"));
        Assert.False(reloaded.Contains("clear_owner"));
        Assert.Equal(2L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.True(await ExistsAsync(storage, "files/after.png"));
        Assert.Equal(1L, await CountAsync("identity_audit_event",
            new BsonDocument { ["action"] = "project_clear_retryable_failure", ["project_id"] = project["_id"].ToString() }));

        // Remove the injected obstacle; missing objects are idempotent, so the
        // same expected_version can be retried cleanly.
        Directory.Delete(Path.Combine(fx.Root!, "files", "blocked"));
        var (changed, projectAfter) = await RunClearAsync(lifecycle, actor, project, "NORMAL", 4);
        Assert.True(changed);
        Assert.Equal(1, (int)projectAfter.Integer("st"));
        Assert.Equal(0L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.False(await ExistsAsync(storage, "files/after.png"));
    }

    [Fact]
    public async Task ConcurrentClearClaimsOnceAndAuditsOnce()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 0);
        var actor = await fx.AddCreatorAsync(project);
        for (var index = 0; index < 8; index++)
        {
            var name = $"p{index}.png";
            await fx.AddFileAsync(project, name);
            await PutObjectAsync(storage, "files/" + name);
        }

        var lifecycle = NewLifecycle(storage, config);
        async Task<bool> AttemptAsync()
        {
            try
            {
                var recent = await ReloadProjectAsync(project);
                var (_, changed) = await lifecycle.ClearAsync(actor, recent, Payload("NORMAL", 0), "req-concurrent",
                    CancellationToken.None);
                return changed;
            }
            catch (ApiError error) when (error.Code is 409 or 5112 or 5100)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());
        Assert.Equal(1, results.Count(changed => changed));

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(1, (int)reloaded.Integer("st"));
        Assert.Equal(1L, await CountAsync("identity_audit_event",
            new BsonDocument { ["action"] = "project_clear", ["project_id"] = project["_id"].ToString() }));
        Assert.Equal(0L, await CountAsync("file", new BsonDocument("p", project["_id"])));
    }

    [Fact]
    public async Task CompleteAndReopenUseCtmAndAllowMissingStatusVersion()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(st: 0, stv: 0, includeStatusVersion: false);
        var actor = await fx.AddCreatorAsync(project);

        var lifecycle = NewLifecycle(storage, config);
        var (completed, changed) = await lifecycle.CompleteAsync(actor, project, Payload("NORMAL", 0), "req-complete",
            CancellationToken.None);
        Assert.True(changed);
        Assert.Equal(5, (int)completed.Integer("st"));
        Assert.Equal(1L, completed.Integer("stv"));
        Assert.True(completed.Contains("ctm"));
        Assert.False(completed.Contains("completed_time"));

        var reopened = await lifecycle.ReopenAsync(actor, completed, Payload("COMPLETED", 1), "req-reopen",
            CancellationToken.None);
        Assert.True(reopened.Changed);
        Assert.Equal(0, (int)reopened.Project.Integer("st"));
        Assert.Equal(2L, reopened.Project.Integer("stv"));
        Assert.False(reopened.Project.Contains("ctm"));
    }

    [Fact]
    public async Task ExpectedStatusMustEqualCurrentStatus()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(st: 0, stv: 2);
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "keep.png");

        var lifecycle = NewLifecycle(storage, config);
        // COMPLETED is in clear's allowed set, but the current status is NORMAL;
        // only the "must equal current" check can reject it.
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
        {
            await lifecycle.ClearAsync(actor, project, Payload("COMPLETED", 2), "req-status", CancellationToken.None);
        });

        Assert.Equal(5112, error.Code);
        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(2L, reloaded.Integer("stv"));
        Assert.False(reloaded.Flag("cip"));
        Assert.Equal(1L, await CountAsync("file", new BsonDocument("p", project["_id"])));
    }

    [Fact]
    public async Task CreateTargetCachesEveryFileRevisionIncludingFolderAndInactive()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var sourceLanguage = await fx.AddLanguageAsync("src-" + Guid.NewGuid().ToString("N"));
        var project = await fx.AddProjectAsync(ol: sourceLanguage["_id"].AsObjectId);
        var actor = await fx.AddCreatorAsync(project);
        var language = await fx.AddLanguageAsync("tgt-" + Guid.NewGuid().ToString("N"));

        var folder = await fx.AddFileAsync(project, "folder", type: 1);
        var active = await fx.AddFileAsync(project, "active.png");
        var inactive = await fx.AddFileAsync(project, "old.png", active: false, revision: 2);

        var other = await fx.AddProjectAsync();
        var otherFile = await fx.AddFileAsync(other, "other.png");

        var service = NewProjectService(storage, config);
        var target = await service.CreateTargetAsync(actor, project, language.Text("co"), CancellationToken.None);

        Assert.Equal(project["_id"], target["t"]);
        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(1L, reloaded.Integer("tc"));

        var caches = await Store.Collection("file_target_cache")
            .Find(new BsonDocument("t", target["_id"])).ToListAsync();
        Assert.Equal(3, caches.Count);
        var cachedFiles = caches.Select(cache => cache["f"].AsObjectId).ToHashSet();
        Assert.Contains(folder["_id"].AsObjectId, cachedFiles);
        Assert.Contains(active["_id"].AsObjectId, cachedFiles);
        Assert.Contains(inactive["_id"].AsObjectId, cachedFiles);
        Assert.DoesNotContain(otherFile["_id"].AsObjectId, cachedFiles);
        Assert.All(caches, cache =>
        {
            Assert.Equal(0, (int)cache.Integer("ts"));
            Assert.Equal(0, (int)cache.Integer("cs"));
            Assert.True(cache.Contains("e"));
        });
    }

    [Fact]
    public async Task DeleteTargetCascadesRowsObjectsAndCounterWithoutOtherProjects()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var language = await fx.AddLanguageAsync("tgt-" + Guid.NewGuid().ToString("N"));
        var project = await fx.AddProjectAsync(tc: 2, fs: 100L, ob: 4096L);
        var actor = await fx.AddCreatorAsync(project);

        var target = await fx.AddTargetAsync(project, language);
        var file = await fx.AddFileAsync(project, "page.png");
        var source = await fx.AddSourceAsync(file);
        await fx.AddTranslationAsync(source, target);
        await fx.AddTipAsync(source, target);
        await fx.AddCacheAsync(file, target);
        var output = await fx.AddOutputAsync(project, target, "result.pdf", 4096);

        var otherTarget = await fx.AddTargetAsync(project, await fx.AddLanguageAsync("oth-" + Guid.NewGuid().ToString("N")));
        var otherFile = await fx.AddFileAsync(project, "other.png");
        var otherSource = await fx.AddSourceAsync(otherFile);
        await fx.AddTranslationAsync(otherSource, otherTarget);
        await fx.AddTipAsync(otherSource, otherTarget);
        await fx.AddCacheAsync(otherFile, otherTarget);
        var otherOutput = await fx.AddOutputAsync(project, otherTarget, "keep.pdf", 1024);

        var otherProject = await fx.AddProjectAsync(tc: 1);
        var otherProjectTarget = await fx.AddTargetAsync(otherProject);
        var foreignOutput = await fx.AddOutputAsync(otherProject, otherProjectTarget, "foreign.pdf", 512);

        await PutObjectAsync(storage, OutputKey(output));
        await PutObjectAsync(storage, OutputKey(otherOutput));
        await PutObjectAsync(storage, OutputKey(foreignOutput));

        var service = NewProjectService(storage, config);
        await service.DeleteTargetAsync(actor, target, CancellationToken.None);

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(1L, reloaded.Integer("tc"));
        Assert.Equal(0L, reloaded.Integer("ob"));
        Assert.Equal(96L, reloaded.Integer("fs"));
        Assert.Equal(0L, await CountAsync("target", new BsonDocument("_id", target["_id"])));
        Assert.Equal(0L, await CountAsync("output", new BsonDocument("t", target["_id"])));
        Assert.Equal(0L, await CountAsync("translation", new BsonDocument("t", target["_id"])));
        Assert.Equal(0L, await CountAsync("tip", new BsonDocument("t", target["_id"])));
        Assert.Equal(0L, await CountAsync("file_target_cache", new BsonDocument("t", target["_id"])));

        Assert.Equal(1L, await CountAsync("target", new BsonDocument("_id", otherTarget["_id"])));
        Assert.Equal(1L, await CountAsync("output", new BsonDocument("_id", otherOutput["_id"])));
        Assert.Equal(1L, await CountAsync("translation", new BsonDocument("t", otherTarget["_id"])));
        Assert.Equal(1L, await CountAsync("file_target_cache", new BsonDocument("t", otherTarget["_id"])));
        Assert.Equal(1L, await CountAsync("target", new BsonDocument("_id", otherProjectTarget["_id"])));
        Assert.Equal(1L, await CountAsync("output", new BsonDocument("_id", foreignOutput["_id"])));

        Assert.False(await ExistsAsync(storage, OutputKey(output)));
        Assert.True(await ExistsAsync(storage, OutputKey(otherOutput)));
        Assert.True(await ExistsAsync(storage, OutputKey(foreignOutput)));
    }

    [Fact]
    public async Task DeleteTargetIsIdempotentWhenOutputObjectIsMissing()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(tc: 1);
        var actor = await fx.AddCreatorAsync(project);
        var target = await fx.AddTargetAsync(project);
        var file = await fx.AddFileAsync(project, "page.png");
        var source = await fx.AddSourceAsync(file);
        var cache = await fx.AddCacheAsync(file, target);
        var output = await fx.AddOutputAsync(project, target, "missing.pdf", 0);

        var service = NewProjectService(storage, config);
        await service.DeleteTargetAsync(actor, target, CancellationToken.None);

        Assert.Equal(0L, (await ReloadProjectAsync(project)).Integer("tc"));
        Assert.Equal(0L, await CountAsync("target", new BsonDocument("_id", target["_id"])));
        Assert.Equal(0L, await CountAsync("output", new BsonDocument("_id", output["_id"])));
        Assert.Equal(1L, await CountAsync("source", new BsonDocument("_id", source["_id"])));
        Assert.Equal(0L, await CountAsync("file_target_cache", new BsonDocument("_id", cache["_id"])));
    }

    [Fact]
    public async Task DeleteTargetFailsClosedWhenStorageSourceIdIsSet()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(tc: 1, csid: ObjectId.GenerateNewId());
        var actor = await fx.AddCreatorAsync(project);
        var target = await fx.AddTargetAsync(project);
        await fx.AddCacheAsync(await fx.AddFileAsync(project, "page.png"), target);

        var service = NewProjectService(storage, config);
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
            await service.DeleteTargetAsync(actor, target, CancellationToken.None));

        Assert.Equal(5113, error.Code);
        Assert.Equal(1L, (await ReloadProjectAsync(project)).Integer("tc"));
        Assert.Equal(1L, await CountAsync("target", new BsonDocument("_id", target["_id"])));
        Assert.Equal(1L, await CountAsync("file_target_cache", new BsonDocument("t", target["_id"])));
    }

    [Fact]
    public async Task CancellationDuringObjectDeleteReleasesOwnedClaimAndCanRetry()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 4);
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "page.png");
        await PutObjectAsync(storage, "files/page.png");

        using var requestCts = new CancellationTokenSource();
        var lifecycle = NewLifecycle(storage, config, (_, ct) =>
        {
            requestCts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await lifecycle.ClearAsync(actor, project, Payload("NORMAL", 4), "req-cancel", requestCts.Token));

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(4L, reloaded.Integer("stv"));
        Assert.False(reloaded.Flag("cip"));
        Assert.False(reloaded.Contains("clear_owner"));
        Assert.Equal(1L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.True(await ExistsAsync(storage, "files/page.png"));
        Assert.Equal(0L, await CountAsync("identity_audit_event",
            new BsonDocument { ["action"] = "project_clear_recovery_required", ["project_id"] = project["_id"].ToString() }));

        // Cancellation is not a fake 503; the same expected version is still
        // valid and a normal retry completes the cleanup.
        var (changed, after) = await RunClearAsync(NewLifecycle(storage, config), actor, project, "NORMAL", 4);
        Assert.True(changed);
        Assert.Equal(1, (int)after.Integer("st"));
        Assert.Equal(0L, await CountAsync("file", new BsonDocument("p", project["_id"])));
        Assert.False(await ExistsAsync(storage, "files/page.png"));
    }

    [Fact]
    public async Task SupersededOwnerCannotReleaseOrCommitNewClaim()
    {
        await using var fx = new Fixture(Store);
        var (storage, config) = fx.UseLocalStorage();
        var project = await fx.AddProjectAsync(stv: 4);
        var actor = await fx.AddCreatorAsync(project);
        await fx.AddFileAsync(project, "page.png");
        await PutObjectAsync(storage, "files/page.png");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldOwnerLifecycle = NewLifecycle(storage, config, async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });

        var attempt = oldOwnerLifecycle.ClearAsync(actor, project, Payload("NORMAL", 4), "req-superseded",
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var newOwner = ObjectId.GenerateNewId();
        try
        {
            await Store.Collection("project").UpdateOneAsync(
                new BsonDocument("_id", project["_id"]),
                new BsonDocument("$set", new BsonDocument("clear_owner", newOwner)));
        }
        finally
        {
            release.TrySetResult();
        }

        // The old invocation may have cleared rows before its final CAS, but it
        // must neither mark the project CLEARED nor release/overwrite the newer
        // owner's claim.
        var error = await Assert.ThrowsAsync<ApiError>(async () => await attempt);
        Assert.Equal(5112, error.Code);

        var reloaded = await ReloadProjectAsync(project);
        Assert.Equal(0, (int)reloaded.Integer("st"));
        Assert.Equal(4L, reloaded.Integer("stv"));
        Assert.True(reloaded.Flag("cip"));
        Assert.Equal(newOwner, reloaded["clear_owner"].AsObjectId);
        Assert.Equal(1L, await CountAsync("identity_audit_event",
            new BsonDocument { ["action"] = "project_clear_recovery_required", ["project_id"] = project["_id"].ToString() }));
    }

    // ---------------------------------------------------------------- helpers

    private ProjectLifecycleService NewLifecycle(ObjectStorage storage, IConfiguration configuration,
        Func<string, CancellationToken, Task>? deleteObject = null) =>
        new(Store, Clock, new IdentityPermissions(Store, Clock), new IdentityAuditWriter(Store, Clock),
            null!, storage, configuration, null, deleteObject);

    private ProjectService NewProjectService(ObjectStorage storage, IConfiguration configuration) =>
        new(Store, Clock, null!, new IdentityPermissions(Store, Clock), null!, null!, storage, configuration);

    private static JsonElementLite? Payload(string? expectedStatus, int? expectedVersion)
    {
        var fields = new List<string>();
        if (expectedStatus is not null) fields.Add("\"expected_status\":\"" + expectedStatus + "\"");
        if (expectedVersion is not null) fields.Add("\"expected_version\":" + expectedVersion.Value);
        if (fields.Count == 0) return null;
        using var document = JsonDocument.Parse("{" + string.Join(",", fields) + "}");
        return new JsonElementLite(document.RootElement.Clone());
    }

    private async Task<(bool Changed, BsonDocument Project)> RunClearAsync(ProjectLifecycleService lifecycle,
        BsonDocument actor, BsonDocument project, string expectedStatus, int expectedVersion)
    {
        var (updated, changed) = await lifecycle.ClearAsync(actor, project, Payload(expectedStatus, expectedVersion),
            "req-clear", CancellationToken.None);
        return (changed, updated);
    }

    private async Task<BsonDocument> ReloadProjectAsync(BsonDocument project) =>
        await Store.Collection("project").Find(new BsonDocument("_id", project["_id"])).FirstAsync();

    private async Task<long> CountAsync(string collection, BsonDocument filter) =>
        await Store.Collection(collection).CountDocumentsAsync(filter);

    private static string OutputKey(BsonDocument output) =>
        "outputs/" + output["_id"].AsObjectId + "/" + output.Text("file_name");

    private static async Task PutObjectAsync(ObjectStorage storage, string key)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("cleanup-test"));
        await storage.PutAsync(key, stream, "application/octet-stream", CancellationToken.None);
    }

    private static async Task<bool> ExistsAsync(ObjectStorage storage, string key) =>
        await storage.HeadAsync(key, CancellationToken.None) is not null;

    // ---------------------------------------------------------------- fixture

    private sealed class Fixture(MongoStore store) : IAsyncDisposable
    {
        private readonly List<ObjectId> _projects = [];
        private readonly List<ObjectId> _targets = [];
        private readonly List<ObjectId> _files = [];
        private readonly List<ObjectId> _sources = [];
        private readonly List<ObjectId> _languages = [];
        private readonly List<ObjectId> _teams = [];

        public string? Root { get; private set; }
        private ObjectStorage? Storage { get; set; }

        public (ObjectStorage Storage, IConfiguration Configuration) UseLocalStorage()
        {
            Root = Path.Combine(Path.GetTempPath(), "shoreline-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = "Local",
                ["Storage:Root"] = Root
            }).Build();
            Storage = new ObjectStorage(configuration);
            return (Storage, configuration);
        }

        public async Task<BsonDocument> AddProjectAsync(int st = 0, long stv = 0, bool includeStatusVersion = true,
            long tc = 0, long fs = 100L, long ob = 0L, ObjectId? ol = null, ObjectId? csid = null)
        {
            var id = ObjectId.GenerateNewId();
            var document = new BsonDocument
            {
                ["_id"] = id,
                ["n"] = "cleanup-" + id,
                ["t"] = ObjectId.GenerateNewId(),
                ["ou"] = ObjectId.GenerateNewId(),
                ["st"] = st,
                ["stv"] = stv,
                ["cip"] = false,
                ["tc"] = tc,
                ["fo"] = 2,
                ["fc"] = 3,
                ["fs"] = fs,
                ["cover_bytes"] = 50L,
                ["resample_bytes"] = 25L,
                ["ob"] = ob,
                ["sc"] = 4,
                ["tsc"] = 5,
                ["csc"] = 6,
                ["ol"] = ol ?? ObjectId.GenerateNewId()
            };
            if (!includeStatusVersion) document.Remove("stv");
            if (csid is not null) document["csid"] = csid.Value;
            await store.Collection("project").InsertOneAsync(document);
            _projects.Add(id);
            _teams.Add(document["t"].AsObjectId);
            return document;
        }

        public async Task<BsonDocument> AddCreatorAsync(BsonDocument project)
        {
            var actor = new BsonDocument { ["_id"] = project["ou"], ["n"] = "actor" };
            await store.Collection("team_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["team"] = project["t"],
                ["user"] = actor["_id"],
                ["status"] = "active",
                ["base_tag"] = "creator",
                ["tags"] = new BsonArray()
            });
            return actor;
        }

        public Task AddProjectMemberAsync(BsonDocument project) =>
            store.Collection("project_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["project"] = project["_id"],
                ["user"] = project["ou"],
                ["status"] = "active",
                ["tags"] = new BsonArray()
            });

        public async Task<BsonDocument> AddLanguageAsync(string code)
        {
            var document = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["co"] = code, ["n"] = code };
            await store.Collection("language").InsertOneAsync(document);
            _languages.Add(document["_id"].AsObjectId);
            return document;
        }

        public async Task<BsonDocument> AddTargetAsync(BsonDocument project, BsonDocument? language = null)
        {
            language ??= await AddLanguageAsync("tgt-" + Guid.NewGuid().ToString("N"));
            var document = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["t"] = project["_id"],
                ["l"] = language["_id"],
                ["tsc"] = 0,
                ["csc"] = 0,
                ["ct"] = DateTime.UtcNow,
                ["et"] = DateTime.UtcNow,
                ["i"] = ""
            };
            await store.Collection("target").InsertOneAsync(document);
            _targets.Add(document["_id"].AsObjectId);
            return document;
        }

        public async Task<BsonDocument> AddFileAsync(BsonDocument project, string saveName, int type = 0,
            bool active = true, int revision = 1)
        {
            var document = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["p"] = project["_id"],
                ["t"] = type,
                ["sa"] = saveName,
                ["n"] = saveName,
                ["v"] = revision,
                ["ac"] = active
            };
            await store.Collection("file").InsertOneAsync(document);
            _files.Add(document["_id"].AsObjectId);
            return document;
        }

        public async Task<BsonDocument> AddSourceAsync(BsonDocument file)
        {
            var document = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = file["_id"],
                ["r"] = 1,
                ["c"] = "source"
            };
            await store.Collection("source").InsertOneAsync(document);
            _sources.Add(document["_id"].AsObjectId);
            return document;
        }

        public Task AddTranslationAsync(BsonDocument source, BsonDocument target) =>
            store.Collection("translation").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = source["_id"],
                ["t"] = target["_id"],
                ["c"] = "translation"
            });

        public Task AddTipAsync(BsonDocument source, BsonDocument target) =>
            store.Collection("tip").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = source["_id"],
                ["t"] = target["_id"],
                ["c"] = "tip"
            });

        public async Task<BsonDocument> AddCacheAsync(BsonDocument file, BsonDocument target)
        {
            var document = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = file["_id"],
                ["t"] = target["_id"],
                ["ts"] = 1,
                ["cs"] = 2,
                ["e"] = DateTime.UtcNow
            };
            await store.Collection("file_target_cache").InsertOneAsync(document);
            return document;
        }

        public async Task<BsonDocument> AddOutputAsync(BsonDocument project, BsonDocument target, string fileName,
            long storageBytes)
        {
            var document = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["p"] = project["_id"],
                ["t"] = target["_id"],
                ["file_name"] = fileName,
                ["sb"] = storageBytes,
                ["ct"] = DateTime.UtcNow
            };
            await store.Collection("output").InsertOneAsync(document);
            return document;
        }

        public async ValueTask DisposeAsync()
        {
            var files = new BsonArray(_files.Select(id => (BsonValue)id));
            var targets = new BsonArray(_targets.Select(id => (BsonValue)id));
            var sources = new BsonArray(_sources.Select(id => (BsonValue)id));
            var projects = new BsonArray(_projects.Select(id => (BsonValue)id));
            var languages = new BsonArray(_languages.Select(id => (BsonValue)id));
            var teams = new BsonArray(_teams.Select(id => (BsonValue)id));

            if (_files.Count > 0)
                await store.Collection("source").DeleteManyAsync(new BsonDocument("f", new BsonDocument("$in", files)));
            if (_sources.Count > 0)
            {
                var sourceIds = new BsonDocument("$in", sources);
                await store.Collection("translation").DeleteManyAsync(new BsonDocument("o", sourceIds));
                await store.Collection("tip").DeleteManyAsync(new BsonDocument("o", sourceIds));
                await store.Collection("source").DeleteManyAsync(new BsonDocument("_id", sourceIds));
            }
            if (_targets.Count > 0)
            {
                var targetIds = new BsonDocument("$in", targets);
                await store.Collection("translation").DeleteManyAsync(new BsonDocument("t", targetIds));
                await store.Collection("tip").DeleteManyAsync(new BsonDocument("t", targetIds));
                await store.Collection("file_target_cache").DeleteManyAsync(new BsonDocument("t", targetIds));
                await store.Collection("output").DeleteManyAsync(new BsonDocument("t", targetIds));
                await store.Collection("target").DeleteManyAsync(new BsonDocument("_id", targetIds));
            }
            if (_files.Count > 0)
            {
                var fileIds = new BsonDocument("$in", files);
                await store.Collection("file_target_cache").DeleteManyAsync(new BsonDocument("f", fileIds));
                await store.Collection("file").DeleteManyAsync(new BsonDocument("_id", fileIds));
            }
            if (_projects.Count > 0)
            {
                var projectIds = new BsonDocument("$in", projects);
                await store.Collection("output").DeleteManyAsync(new BsonDocument("p", projectIds));
                await store.Collection("file").DeleteManyAsync(new BsonDocument("p", projectIds));
                await store.Collection("target").DeleteManyAsync(new BsonDocument("t", projectIds));
                await store.Collection("project_member").DeleteManyAsync(new BsonDocument("project", projectIds));
                await store.Collection("identity_audit_event").DeleteManyAsync(new BsonDocument("project_id",
                    new BsonDocument("$in", new BsonArray(_projects.Select(id => (BsonValue)id.ToString())))));
                await store.Collection("project").DeleteManyAsync(new BsonDocument("_id", projectIds));
            }
            if (_teams.Count > 0)
                await store.Collection("team_member").DeleteManyAsync(
                    new BsonDocument("team", new BsonDocument("$in", teams)));
            if (_languages.Count > 0)
                await store.Collection("language").DeleteManyAsync(
                    new BsonDocument("_id", new BsonDocument("$in", languages)));

            Storage?.Dispose();
            if (Root is not null && Directory.Exists(Root))
            {
                try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
