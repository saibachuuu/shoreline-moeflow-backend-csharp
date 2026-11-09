using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Direct-service tests for the E1 label chain. They deliberately do not go
/// through HTTP: Program.cs has not been wired to the label registration yet.
/// </summary>
[Collection("local-services")]
public sealed class LabelTests(LocalServices services)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task List_paginates_before_blank_filter_and_keeps_raw_total()
    {
        var world = await WorldAsync();
        await SourceAsync(world, 0, content: "zero");
        await SourceAsync(world, 1, blank: true, content: "blank");
        await SourceAsync(world, 2, content: "two");

        var first = await world.Labels.ListSourcesAsync(
            Context("?page=1&limit=1"), world.Actor, world.FileId, world.TargetId, paging: true, Ct);
        Assert.True(first.Paged);
        Assert.Equal(3, first.Total);
        Assert.Single(first.Items);
        Assert.Equal("zero", first.Items[0]["content"]);

        var all = await world.Labels.ListSourcesAsync(
            Context(), world.Actor, world.FileId, world.TargetId, paging: false, Ct);
        Assert.False(all.Paged);
        Assert.Equal(3, all.Total);
        Assert.Equal(2, all.Items.Count);
        Assert.DoesNotContain(all.Items, item => Equals(item["content"], "blank"));
    }

    [Fact]
    public async Task List_separates_own_others_tips_and_other_language_flag()
    {
        var world = await WorldAsync();
        var first = await SourceAsync(world, 0, content: "one");
        var second = await SourceAsync(world, 1, content: "two");
        var other = await UserAsync("other");
        await AddMemberAsync(world, other, "creator");

        await TranslationAsync(world, first, world.Actor, "mine", targetId: world.TargetId);
        await TranslationAsync(world, first, other, "theirs", targetId: world.TargetId);
        await TranslationAsync(world, second, other, "elsewhere", targetId: ObjectId.GenerateNewId());
        await TipAsync(world, first, world.Actor, "note", targetId: world.TargetId);

        var result = await world.Labels.ListSourcesAsync(
            Context(), world.Actor, world.FileId, world.TargetId, paging: false, Ct);
        var one = result.Items.Single(item => Equals(item["content"], "one"));
        var two = result.Items.Single(item => Equals(item["content"], "two"));

        Assert.True(one.ContainsKey("my_translation"));
        Assert.Equal("mine", ((Dictionary<string, object?>)one["my_translation"]!)["content"]);
        var translations = (List<Dictionary<string, object?>>)one["translations"]!;
        Assert.Single(translations);
        Assert.Equal("theirs", translations[0]["content"]);
        Assert.Single((List<Dictionary<string, object?>>)one["tips"]!);
        Assert.True((bool)two["has_other_language_translation"]!);
        Assert.False((bool)one["has_other_language_translation"]!);
    }

    [Fact]
    public async Task Create_source_appends_rank_and_updates_file_and_project_counts()
    {
        var world = await WorldAsync();
        var first = await world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "first", 0.1, 0.2, LabelService.PositionIn, Ct);
        var second = await world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "second", 0.3, 0.4, LabelService.PositionOut, Ct);

        Assert.Equal(LabelService.PositionIn, Convert.ToInt32(first["position_type"]));
        Assert.Equal(LabelService.PositionOut, Convert.ToInt32(second["position_type"]));
        var sources = await world.Store.Collection("source")
            .Find(new BsonDocument("f", world.FileId)).Sort(new BsonDocument("r", 1)).ToListAsync();
        Assert.Equal(new long[] { 0, 1 }, sources.Select(row => row.Integer("r")).ToArray());

        var file = await LoadAsync("file", world.FileId);
        Assert.Equal(2, file.Integer("sc"));
        var project = await LoadAsync("project", world.ProjectId);
        Assert.Equal(2, project.Integer("sc"));
    }

    [Fact]
    public async Task Create_source_requires_image_and_valid_vocabulary()
    {
        var text = await WorldAsync(fileType: 3);
        Assert.Equal(108, (await CaptureError(() => text.Labels.CreateSourceAsync(
            Context(), text.Actor, text.FileId, "x", 0, 0, LabelService.PositionIn, Ct))).Code);

        var world = await WorldAsync();
        Assert.Equal(2, (await CaptureError(() => world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "x", 0, 0, 3, Ct))).Code);
        Assert.Equal(2, (await CaptureError(() => world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "x", 1.1, 0, LabelService.PositionIn, Ct))).Code);
    }

    [Fact]
    public async Task Edit_source_updates_fields_and_rejects_empty_or_non_image()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0, content: "old");
        var updated = await world.Labels.EditSourceAsync(
            Context(), world.Actor, source["_id"].AsObjectId,
            new SourceEditRequest("new", 0.2, 0.3, [0.1, 0.2], LabelService.PositionOut), Ct);

        Assert.Equal("new", updated["content"]);
        Assert.Equal(2, Convert.ToInt32(updated["position_type"]));
        var stored = await LoadAsync("source", source["_id"].AsObjectId);
        Assert.Equal(2, stored["v"].AsBsonArray.Count);

        Assert.Equal(2, (await CaptureError(() => world.Labels.EditSourceAsync(
            Context(), world.Actor, source["_id"].AsObjectId, new SourceEditRequest(), Ct))).Code);

        var text = await WorldAsync(fileType: 3);
        var textSource = await SourceAsync(text, 0);
        Assert.Equal(108, (await CaptureError(() => text.Labels.EditSourceAsync(
            Context(), text.Actor, textSource["_id"].AsObjectId,
            new SourceEditRequest("x"), Ct))).Code);
    }

    [Fact]
    public async Task Move_source_to_end_and_before_target_reorders_without_gaps()
    {
        var world = await WorldAsync();
        var first = await SourceAsync(world, 0, content: "a");
        var second = await SourceAsync(world, 1, content: "b");
        await SourceAsync(world, 2, content: "c");

        await world.Labels.MoveSourceRankAsync(
            Context(), world.Actor, first["_id"].AsObjectId, "end", Ct);
        Assert.Equal(2, (await LoadAsync("source", first["_id"].AsObjectId)).Integer("r"));

        await world.Labels.MoveSourceRankAsync(
            Context(), world.Actor, first["_id"].AsObjectId, second["_id"].AsObjectId.ToString(), Ct);
        var rows = await world.Store.Collection("source")
            .Find(new BsonDocument("f", world.FileId)).Sort(new BsonDocument("r", 1)).ToListAsync();
        Assert.Equal(new[] { "a", "b", "c" }, rows.Select(row => row.Text("c")).ToArray());
        Assert.Equal(new long[] { 0, 1, 2 }, rows.Select(row => row.Integer("r")).ToArray());

        var otherFile = await FileAsync(world, world.ProjectId, fileType: 2);
        var foreign = await SourceAsync(world, 0, fileId: otherFile["_id"].AsObjectId);
        Assert.Equal(8005, (await CaptureError(() => world.Labels.MoveSourceRankAsync(
            Context(), world.Actor, first["_id"].AsObjectId, foreign["_id"].AsObjectId.ToString(), Ct))).Code);
    }

    [Fact]
    public async Task Move_source_rejects_while_file_is_marked_source_moving()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);
        await SourceAsync(world, 1);
        await world.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("sm", true)));

        var error = await CaptureError(() => world.Labels.MoveSourceRankAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "end", Ct));
        Assert.Equal(8006, error.Code);
        Assert.Equal(0, (await LoadAsync("source", source["_id"].AsObjectId)).Integer("r"));
    }

    [Fact]
    public async Task Delete_source_cascades_translations_tips_and_all_counts()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);
        var selected = await TranslationAsync(world, source, world.Actor, "a", selected: true);
        await TranslationAsync(world, source, await UserAsync("other"), "b");
        await TipAsync(world, source, world.Actor, "note");

        await SetCounterAsync(world.FileId, "sc", 1);
        await SetCounterAsync(world.FileId, "tsc", 1);
        await SetCounterAsync(world.FileId, "csc", 1);
        await SetProjectCounterAsync(world.ProjectId, "sc", 1);
        await SetProjectCounterAsync(world.ProjectId, "tsc", 1);
        await SetProjectCounterAsync(world.ProjectId, "csc", 1);
        await SetTargetCounterAsync(world.TargetId, "tsc", 1);
        await SetTargetCounterAsync(world.TargetId, "csc", 1);
        await world.Store.Collection("file_target_cache").UpdateOneAsync(
            new BsonDocument("_id", world.CacheId),
            new BsonDocument("$set", new BsonDocument { ["ts"] = 1, ["cs"] = 1 }));

        await world.Labels.DeleteSourceAsync(Context(), world.Actor, source["_id"].AsObjectId, Ct);

        Assert.Equal(0, await CountAsync("source", new BsonDocument("_id", source["_id"])));
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("o", source["_id"])));
        Assert.Equal(0, await CountAsync("tip", new BsonDocument("o", source["_id"])));
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("_id", selected["_id"])));

        var file = await LoadAsync("file", world.FileId);
        Assert.Equal(0, file.Integer("sc"));
        Assert.Equal(0, file.Integer("tsc"));
        Assert.Equal(0, file.Integer("csc"));
        var target = await LoadAsync("target", world.TargetId);
        Assert.Equal(0, target.Integer("tsc"));
        Assert.Equal(0, target.Integer("csc"));
        var project = await LoadAsync("project", world.ProjectId);
        Assert.Equal(0, project.Integer("sc"));
        Assert.Equal(0, project.Integer("tsc"));
        Assert.Equal(0, project.Integer("csc"));
        var cache = await world.Store.Collection("file_target_cache")
            .Find(new BsonDocument("_id", world.CacheId)).FirstAsync();
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(0, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
    }

    [Fact]
    public async Task Create_translation_empty_is_204_and_new_translation_increments_once()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);

        Assert.Null(await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "", world.TargetId, Ct));
        Assert.Equal(0, (await LoadAsync("target", world.TargetId)).Integer("tsc"));

        var first = await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "hello", world.TargetId, Ct);
        Assert.NotNull(first);
        var second = await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "hello again", world.TargetId, Ct);
        Assert.NotNull(second);
        Assert.Equal(first!["id"], second!["id"]);
        Assert.True(DateTime.Parse(second["edit_time"]!.ToString()!) >
                    DateTime.Parse(first["edit_time"]!.ToString()!));
        Assert.Equal(1, (await LoadAsync("target", world.TargetId)).Integer("tsc"));
        Assert.Equal(1, (await LoadAsync("file", world.FileId)).Integer("tsc"));
        Assert.Equal(1, (await LoadAsync("project", world.ProjectId)).Integer("tsc"));

        var translationId = ObjectId.Parse(first["id"]!.ToString()!);
        await world.Store.Collection("translation").UpdateOneAsync(
            new BsonDocument("_id", translationId),
            new BsonDocument("$set", new BsonDocument { ["p"] = "note", ["c"] = "x" }));
        var kept = await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "", world.TargetId, Ct);
        Assert.NotNull(kept);
        Assert.Equal("", kept!["content"]);

        await world.Store.Collection("translation").UpdateOneAsync(
            new BsonDocument("_id", translationId),
            new BsonDocument("$set", new BsonDocument { ["p"] = "", ["c"] = "" }));
        Assert.Null(await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "", world.TargetId, Ct));
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("_id", translationId)));
        Assert.Equal(0, (await LoadAsync("target", world.TargetId)).Integer("tsc"));
    }

    [Fact]
    public async Task Create_translation_machine_row_conflicts_with_legacy_unique_index()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);
        await TranslationAsync(world, source, world.Actor, "machine", mt: true);

        var error = await CaptureError(() => world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "manual", world.TargetId, Ct));
        Assert.Equal(8007, error.Code);
        Assert.Equal("TranslationNotUniqueError", error.Error);
    }

    [Fact]
    public async Task Translation_selection_has_one_winner_and_checked_count_tracks_source()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);
        var other = await UserAsync("other");
        await AddMemberAsync(world, other, "creator");
        var mine = await TranslationAsync(world, source, world.Actor, "mine");
        var theirs = await TranslationAsync(world, source, other, "theirs");

        await world.Labels.EditTranslationAsync(Context(), world.Actor, mine["_id"].AsObjectId,
            new TranslationEditRequest(Selected: true), Ct);
        Assert.Equal(1, (await LoadAsync("target", world.TargetId)).Integer("csc"));

        await world.Labels.EditTranslationAsync(Context(), world.Actor, theirs["_id"].AsObjectId,
            new TranslationEditRequest(Selected: true), Ct);
        Assert.False((await LoadAsync("translation", mine["_id"].AsObjectId)).Flag("s"));
        Assert.True((await LoadAsync("translation", theirs["_id"].AsObjectId)).Flag("s"));
        Assert.Equal(1, (await LoadAsync("target", world.TargetId)).Integer("csc"));
        Assert.Equal(1, (await LoadAsync("file", world.FileId)).Integer("csc"));

        await world.Labels.EditTranslationAsync(Context(), world.Actor, theirs["_id"].AsObjectId,
            new TranslationEditRequest(Selected: false), Ct);
        Assert.Equal(0, (await LoadAsync("target", world.TargetId)).Integer("csc"));
    }

    [Fact]
    public async Task Batch_selection_selects_only_matching_source_and_translation()
    {
        var world = await WorldAsync();
        var first = await SourceAsync(world, 0);
        var second = await SourceAsync(world, 1);
        var one = await TranslationAsync(world, first, world.Actor, "one");
        var two = await TranslationAsync(world, second, world.Actor, "two");

        await world.Labels.BatchSelectAsync(Context(), world.Actor, world.FileId,
            [new BatchSelection(first["_id"].AsObjectId, one["_id"].AsObjectId),
             new BatchSelection(second["_id"].AsObjectId, two["_id"].AsObjectId)], Ct);

        Assert.True((await LoadAsync("translation", one["_id"].AsObjectId)).Flag("s"));
        Assert.True((await LoadAsync("translation", two["_id"].AsObjectId)).Flag("s"));
        Assert.Equal(2, (await LoadAsync("target", world.TargetId)).Integer("csc"));
    }

    [Fact]
    public async Task Translation_edit_prevalidates_proofread_before_selecting()
    {
        var world = await WorldAsync(tags: ["checkonly"], checkPolicy: true);
        var source = await SourceAsync(world, 0);
        var translation = await TranslationAsync(world, source, world.Actor, "mine");

        var error = await CaptureError(() => world.Labels.EditTranslationAsync(
            Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(Selected: true, ProofreadContent: "proof"), Ct));
        Assert.Equal(103, error.Code);
        Assert.False((await LoadAsync("translation", translation["_id"].AsObjectId)).Flag("s"));
    }

    [Fact]
    public async Task Translation_edit_own_content_proofread_and_clear_semantics()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);
        var translation = await TranslationAsync(world, source, world.Actor, "old");
        var other = await UserAsync("other");
        await AddMemberAsync(world, other, "creator");

        await world.Labels.EditTranslationAsync(Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(Content: "new"), Ct);
        Assert.Equal(103, (await CaptureError(() => world.Labels.EditTranslationAsync(
            Context(), other, translation["_id"].AsObjectId,
            new TranslationEditRequest(Content: "stolen"), Ct))).Code);

        await world.Labels.EditTranslationAsync(Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(ProofreadContent: "checked"), Ct);
        var proofread = await LoadAsync("translation", translation["_id"].AsObjectId);
        Assert.Equal("checked", proofread.Text("p"));
        Assert.Equal(world.Actor["_id"].AsObjectId, proofread["pr"].AsObjectId);

        await world.Labels.EditTranslationAsync(Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(ProofreadContent: ""), Ct);
        var cleared = await LoadAsync("translation", translation["_id"].AsObjectId);
        Assert.Equal("new", cleared.Text("c"));
        Assert.Equal("", cleared.Text("p"));
        Assert.False(cleared.Contains("pr"));

        Assert.Null(await world.Labels.EditTranslationAsync(
            Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(Content: "", ProofreadContent: ""), Ct));
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));
    }

    [Fact]
    public async Task Translation_delete_requires_ownership_or_delete_permission()
    {
        var world = await WorldAsync(tags: ["checkonly"], checkPolicy: true);
        var source = await SourceAsync(world, 0);
        var owner = await UserAsync("owner");
        var translation = await TranslationAsync(world, source, owner, "mine");

        Assert.Equal(103, (await CaptureError(() => world.Labels.DeleteTranslationAsync(
            Context(), world.Actor, translation["_id"].AsObjectId, Ct))).Code);

        var creator = await UserAsync("creator");
        await AddMemberAsync(world, creator, "creator");
        await world.Labels.DeleteTranslationAsync(
            Context(), creator, translation["_id"].AsObjectId, Ct);
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));
    }

    [Fact]
    public async Task Tip_upsert_uses_strict_time_and_owner_checks()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0);

        Assert.Equal(4021, (await CaptureError(() => world.Labels.CreateTipAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "", world.TargetId, Ct))).Code);

        var first = await world.Labels.CreateTipAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "note", world.TargetId, Ct);
        var second = await world.Labels.CreateTipAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "note 2", world.TargetId, Ct);
        Assert.Equal(first["id"], second["id"]);
        Assert.True(DateTime.Parse(second["edit_time"]!.ToString()!) >
                    DateTime.Parse(first["edit_time"]!.ToString()!));
        Assert.Equal(first["create_time"], second["create_time"]);

        Assert.Equal(4021, (await CaptureError(() => world.Labels.EditTipAsync(
            Context(), world.Actor, ObjectId.Parse(first["id"]!.ToString()!), "   ", Ct))).Code);

        var other = await UserAsync("other");
        await AddMemberAsync(world, other, "creator");
        Assert.Equal(103, (await CaptureError(() => world.Labels.EditTipAsync(
            Context(), other, ObjectId.Parse(first["id"]!.ToString()!), "changed", Ct))).Code);
        Assert.Equal(103, (await CaptureError(() => world.Labels.DeleteTipAsync(
            Context(), other, ObjectId.Parse(first["id"]!.ToString()!), Ct))).Code);

        Assert.Equal(4024, (await CaptureError(() => world.Labels.CreateTipAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "x", ObjectId.GenerateNewId(), Ct))).Code);

        await world.Labels.DeleteTipAsync(
            Context(), world.Actor, ObjectId.Parse(first["id"]!.ToString()!), Ct);
        Assert.Equal(0, await CountAsync("tip", new BsonDocument("_id", ObjectId.Parse(first["id"]!.ToString()))));
    }

    [Fact]
    public async Task Legacy_label_lock_blocks_waiting_writer_and_owner_cas_releases()
    {
        var world = await WorldAsync();
        await world.Store.Collection("project_label_lock").InsertOneAsync(new BsonDocument
        {
            ["_id"] = world.ProjectId,
            ["owner"] = "legacy-owner",
            ["created"] = DateTime.UtcNow
        });

        var busy = await CaptureError(() => world.Labels.ListSourcesAsync(
            Context(), world.Actor, world.FileId, world.TargetId, paging: false, Ct));
        Assert.Equal(409, busy.Status);
        Assert.Equal(1, busy.Code);
        Assert.Equal("LabelBusyError", busy.Error);

        await world.Store.Collection("project_label_lock")
            .DeleteOneAsync(new BsonDocument("_id", world.ProjectId));
        await using var lease = await world.Lock.AcquireRecoveryAsync(world.ProjectId);
        Assert.Equal(1, await CountAsync("project_label_lock", new BsonDocument("_id", world.ProjectId)));
        await lease.DisposeAsync();
        Assert.Equal(0, await CountAsync("project_label_lock", new BsonDocument("_id", world.ProjectId)));
    }

    [Fact]
    public async Task Recovery_flag_blocks_normal_operations_but_allows_recovery_lease()
    {
        var world = await WorldAsync();
        await world.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", world.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_recovery", ObjectId.GenerateNewId())));

        var busy = await CaptureError(() => world.Labels.ListSourcesAsync(
            Context(), world.Actor, world.FileId, world.TargetId, paging: false, Ct));
        Assert.Equal(409, busy.Status);

        await using var lease = await world.Lock.AcquireRecoveryAsync(world.ProjectId);
        Assert.True(lease.Recovery);
    }

    [Fact]
    public async Task Expected_revision_mismatch_is_rejected_for_jobs()
    {
        var world = await WorldAsync(revision: 2);
        var error = await CaptureError(() => world.Lock.AcquireRecoveryAsync(
            world.ProjectId, expectedRevision: 1));
        Assert.Equal(409, error.Status);
        Assert.Contains("后台任务的标签版本已过期", error.Message);

        await using var lease = await world.Lock.AcquireRecoveryAsync(
            world.ProjectId, expectedRevision: 2);
        Assert.Equal(2, lease.Revision);
    }

    [Fact]
    public async Task Stale_row_epoch_is_rejected_against_current_project_revision()
    {
        var world = await WorldAsync(revision: 1, fileEpoch: 0);
        var error = await CaptureError(() => world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "x", 0, 0, LabelService.PositionIn, Ct));
        Assert.Equal(409, error.Status);
        Assert.Equal(0, await CountAsync("source", new BsonDocument("f", world.FileId)));
    }

    [Fact]
    public async Task Rows_without_epoch_are_valid_when_revision_is_zero()
    {
        var world = await WorldAsync(revision: 0, includeFileEpoch: false);
        var source = await SourceAsync(world, 0, epoch: null);
        Assert.NotNull(await world.Labels.CreateSourceAsync(
            Context(), world.Actor, world.FileId, "new", 0, 0, LabelService.PositionIn, Ct));
        await world.Labels.EditSourceAsync(Context(), world.Actor, source["_id"].AsObjectId,
            new SourceEditRequest("edited"), Ct);
        Assert.Equal("edited", (await LoadAsync("source", source["_id"].AsObjectId)).Text("c"));
    }

    [Fact]
    public async Task Same_request_scope_reuses_lease_and_borrowed_dispose_does_not_release()
    {
        var world = await WorldAsync();
        var context = new LabelRequestContext();
        var first = await world.Lock.EnterAsync(context, world.ProjectId);
        var second = await world.Lock.EnterAsync(context, world.ProjectId);
        Assert.Same(first.Project, second.Project);
        Assert.Equal(1, await CountAsync("project_label_lock", new BsonDocument("_id", world.ProjectId)));

        await second.DisposeAsync();
        Assert.Equal(1, await CountAsync("project_label_lock", new BsonDocument("_id", world.ProjectId)));
        await first.DisposeAsync();
        Assert.Equal(0, await CountAsync("project_label_lock", new BsonDocument("_id", world.ProjectId)));
    }

    [Fact]
    public async Task Mutations_update_the_existing_file_target_cache_document_only()
    {
        var world = await WorldAsync(revision: 1);
        var source = await SourceAsync(world, 0);

        var cache = await CacheAsync(world);
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(0, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
        Assert.Equal(1, cache.Integer("label_epoch"));

        await world.Labels.CreateTranslationAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "hello", world.TargetId, Ct);
        cache = await CacheAsync(world);
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(1, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
        Assert.Equal(1, cache.Integer("label_epoch"));

        var translation = await Store("translation")
            .Find(new BsonDocument("o", source["_id"])).FirstAsync();
        await world.Labels.EditTranslationAsync(
            Context(), world.Actor, translation["_id"].AsObjectId,
            new TranslationEditRequest(Selected: true), Ct);
        cache = await CacheAsync(world);
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(1, cache.Integer("ts"));
        Assert.Equal(1, cache.Integer("cs"));
        Assert.Equal(1, cache.Integer("label_epoch"));

        // Tips touch source/file edit times but must not create a second cache
        // document or move the counts to a differently named collection.
        await world.Labels.CreateTipAsync(
            Context(), world.Actor, source["_id"].AsObjectId, "note", world.TargetId, Ct);
        cache = await CacheAsync(world);
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(1, cache.Integer("ts"));
        Assert.Equal(1, cache.Integer("cs"));
        Assert.Equal(0, await CountAsync("filetargetcache", new BsonDocument()));

        await world.Labels.DeleteSourceAsync(
            Context(), world.Actor, source["_id"].AsObjectId, Ct);
        cache = await CacheAsync(world);
        Assert.Equal(world.CacheId, cache["_id"].AsObjectId);
        Assert.Equal(0, cache.Integer("ts"));
        Assert.Equal(0, cache.Integer("cs"));
        Assert.Equal(1, cache.Integer("label_epoch"));
        Assert.Equal(0, await CountAsync("filetargetcache", new BsonDocument()));
    }

    [Fact]
    public async Task Acquire_releases_lock_when_post_insert_project_read_is_cancelled()
    {
        var world = await WorldAsync();
        var lockWithFault = new ProjectLabelLock(
            world.Store,
            TimeProvider.System,
            hooks: new ProjectLabelLockHooks
            {
                BeforeProjectReadAsync = (_, ct) => throw new OperationCanceledException(ct)
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lockWithFault.AcquireRecoveryAsync(world.ProjectId));
        Assert.Equal(0, await CountAsync("project_label_lock",
            new BsonDocument("_id", world.ProjectId)));

        // The same project must be acquirable by the next normal caller.
        await using var lease = await world.Lock.AcquireRecoveryAsync(world.ProjectId);
        Assert.Equal(1, await CountAsync("project_label_lock",
            new BsonDocument("_id", world.ProjectId)));
        await lease.DisposeAsync();
        Assert.Equal(0, await CountAsync("project_label_lock",
            new BsonDocument("_id", world.ProjectId)));
    }

    [Fact]
    public async Task Acquire_releases_own_owner_when_insert_acknowledgement_is_lost()
    {
        var world = await WorldAsync();
        var lockWithFault = new ProjectLabelLock(
            world.Store,
            TimeProvider.System,
            hooks: new ProjectLabelLockHooks
            {
                // The insert has already reached the server; the client now
                // loses the acknowledgement and throws.
                AfterInsertAsync = (_, _, _) => throw new InvalidOperationException("lost acknowledgement")
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lockWithFault.AcquireRecoveryAsync(world.ProjectId));
        Assert.Equal(0, await CountAsync("project_label_lock",
            new BsonDocument("_id", world.ProjectId)));

        await using var lease = await world.Lock.AcquireRecoveryAsync(world.ProjectId);
        Assert.True(await CountAsync("project_label_lock",
            new BsonDocument("_id", world.ProjectId)) > 0);
    }

    [Fact]
    public async Task Duplicate_waiter_never_deletes_another_owners_lock()
    {
        var world = await WorldAsync();
        await world.Store.Collection("project_label_lock").InsertOneAsync(new BsonDocument
        {
            ["_id"] = world.ProjectId,
            ["owner"] = "other-owner",
            ["created"] = DateTime.UtcNow
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Lock.AcquireRecoveryAsync(world.ProjectId, ct: cts.Token));

        var row = await world.Store.Collection("project_label_lock")
            .Find(new BsonDocument("_id", world.ProjectId)).FirstAsync();
        Assert.Equal("other-owner", row.Text("owner"));
    }

    // ------------------------------------------------------------------
    // Test world and fixtures
    // ------------------------------------------------------------------

    private sealed class World
    {
        public required BsonDocument Actor { get; init; }
        public required BsonDocument Project { get; init; }
        public required BsonDocument File { get; init; }
        public required BsonDocument Target { get; init; }
        public required LabelService Labels { get; init; }
        public required ProjectLabelLock Lock { get; init; }
        public required MongoStore Store { get; init; }
        public required ObjectId TeamId { get; init; }
        public required ObjectId CacheId { get; init; }
        public required long Revision { get; init; }
        public ObjectId ProjectId => Project["_id"].AsObjectId;
        public ObjectId FileId => File["_id"].AsObjectId;
        public ObjectId TargetId => Target["_id"].AsObjectId;
    }

    private async Task<World> WorldAsync(
        int fileType = 2,
        bool activated = true,
        long revision = 0,
        long? fileEpoch = null,
        bool includeFileEpoch = true,
        string[]? tags = null,
        bool checkPolicy = false)
    {
        var clock = TimeProvider.System;
        var actor = await UserAsync("actor");
        var teamId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        var project = new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "project",
            ["st"] = 0,
            ["ou"] = actor["_id"],
            ["t"] = teamId,
            ["label_revision"] = revision,
            ["m_e"] = now
        };
        await Store("project").InsertOneAsync(project);

        if (checkPolicy)
        {
            await Store("identity_tag_policy").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["team"] = teamId,
                ["project_tags"] = new BsonDocument
                {
                    ["checkonly"] = new BsonDocument
                    {
                        ["permissions"] = new BsonArray
                        {
                            "project:ACCESS", "project:CHECK_TRA"
                        }
                    }
                }
            });
        }

        await AddMemberAsync(projectId, actor, tags ?? new[] { "creator" });

        var languageId = ObjectId.GenerateNewId();
        await Store("language").InsertOneAsync(new BsonDocument
        {
            ["_id"] = languageId,
            ["c"] = "中文",
            ["e"] = "Chinese",
            ["co"] = $"zh-{Guid.NewGuid():N}",
            ["gt"] = $"zh-{Guid.NewGuid():N}",
            ["go"] = $"zh-{Guid.NewGuid():N}",
            ["n"] = false,
            ["s"] = 1
        });

        var file = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "file",
            ["t"] = fileType,
            ["p"] = projectId,
            ["a"] = new BsonArray(),
            ["ac"] = activated,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now
        };
        if (includeFileEpoch) file["label_epoch"] = fileEpoch ?? revision;
        await Store("file").InsertOneAsync(file);

        var target = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = projectId,
            ["l"] = languageId,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ct"] = now,
            ["et"] = now,
            ["i"] = "",
            ["label_epoch"] = revision
        };
        await Store("target").InsertOneAsync(target);

        var cacheId = ObjectId.GenerateNewId();
        await Store("file_target_cache").InsertOneAsync(new BsonDocument
        {
            ["_id"] = cacheId,
            ["f"] = file["_id"],
            ["t"] = target["_id"],
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = now,
            ["label_epoch"] = revision
        });

        var labels = new LabelService(
            services.Store,
            clock,
            new IdentityPermissions(services.Store, clock),
            new UserView(services.Configuration, services.App.Services),
            services.App.Services,
            new ProjectLabelLock(services.Store, clock),
            new LabelRequestContext());
        await labels.InitializeAsync(Ct);

        return new World
        {
            Actor = actor,
            Project = project,
            File = file,
            Target = target,
            Labels = labels,
            Lock = new ProjectLabelLock(services.Store, clock),
            Store = services.Store,
            TeamId = teamId,
            CacheId = cacheId,
            Revision = revision
        };
    }

    private async Task<BsonDocument> UserAsync(string name)
    {
        var user = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = $"{name}-{Guid.NewGuid():N}",
            ["e"] = $"{name}-{Guid.NewGuid():N}@example.test",
            ["as"] = new BsonArray(),
            ["l"] = "auto",
            ["admin"] = false,
            ["b"] = false
        };
        await Store("user").InsertOneAsync(user);
        return user;
    }

    private async Task AddMemberAsync(World world, BsonDocument user, params string[] tags) =>
        await AddMemberAsync(world.ProjectId, user, tags);

    private async Task AddMemberAsync(ObjectId projectId, BsonDocument user, params string[] tags) =>
        await Store("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["user"] = user["_id"],
            ["status"] = "active",
            ["tags"] = new BsonArray(tags)
        });

    private async Task<BsonDocument> SourceAsync(
        World world,
        int rank,
        bool blank = false,
        string content = "source",
        ObjectId? fileId = null,
        long? epoch = 0L)
    {
        var now = DateTime.UtcNow;
        var source = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = fileId ?? world.FileId,
            ["r"] = rank,
            ["c"] = content,
            ["x"] = 0.0,
            ["y"] = 0.0,
            ["v"] = new BsonArray(),
            ["p"] = LabelService.PositionIn,
            ["lf"] = true,
            ["b"] = blank,
            ["cm"] = false,
            ["pt"] = new BsonArray(),
            ["ct"] = now,
            ["e"] = now
        };
        if (epoch is not null) source["label_epoch"] = epoch.Value == 0 ? world.Revision : epoch.Value;
        await Store("source").InsertOneAsync(source);
        return source;
    }

    private async Task<BsonDocument> TranslationAsync(
        World world,
        BsonDocument source,
        BsonDocument user,
        string content,
        bool selected = false,
        bool mt = false,
        ObjectId? targetId = null)
    {
        var now = DateTime.UtcNow;
        var translation = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = source["_id"],
            ["t"] = targetId ?? world.TargetId,
            ["c"] = content,
            ["u"] = user["_id"],
            ["p"] = "",
            ["s"] = selected,
            ["mt"] = mt,
            ["ct"] = now,
            ["e"] = now,
            ["label_epoch"] = world.Revision
        };
        if (selected) translation["sr"] = user["_id"];
        await Store("translation").InsertOneAsync(translation);
        return translation;
    }

    private async Task<BsonDocument> TipAsync(
        World world,
        BsonDocument source,
        BsonDocument user,
        string content,
        ObjectId? targetId = null)
    {
        var now = DateTime.UtcNow;
        var tip = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = source["_id"],
            ["t"] = targetId ?? world.TargetId,
            ["c"] = content,
            ["u"] = user["_id"],
            ["ct"] = now,
            ["et"] = now,
            ["label_epoch"] = world.Revision
        };
        await Store("tip").InsertOneAsync(tip);
        return tip;
    }

    private async Task<BsonDocument> FileAsync(World world, ObjectId projectId, int fileType)
    {
        var now = DateTime.UtcNow;
        var file = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "other",
            ["t"] = fileType,
            ["p"] = projectId,
            ["a"] = new BsonArray(),
            ["ac"] = true,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now,
            ["label_epoch"] = world.Revision
        };
        await Store("file").InsertOneAsync(file);
        return file;
    }

    private Task<BsonDocument> LoadAsync(string collection, ObjectId id) =>
        Store(collection).Find(new BsonDocument("_id", id)).FirstAsync();

    private Task<BsonDocument> CacheAsync(World world) =>
        Store("file_target_cache").Find(new BsonDocument("_id", world.CacheId)).FirstAsync();

    private Task<long> CountAsync(string collection, BsonDocument filter) =>
        Store(collection).CountDocumentsAsync(filter);

    private IMongoCollection<BsonDocument> Store(string collection) =>
        services.Store.Collection(collection);

    private static DefaultHttpContext Context(string? query = null)
    {
        var context = new DefaultHttpContext();
        if (query is not null) context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static async Task<ApiError> CaptureError(Func<Task> action) =>
        await Assert.ThrowsAsync<ApiError>(action);

    private async Task SetCounterAsync(ObjectId id, string field, int value) =>
        await Store("file").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument(field, value)));

    private async Task SetProjectCounterAsync(ObjectId id, string field, int value) =>
        await Store("project").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument(field, value)));

    private async Task SetTargetCounterAsync(ObjectId id, string field, int value) =>
        await Store("target").UpdateOneAsync(new BsonDocument("_id", id),
            new BsonDocument("$set", new BsonDocument(field, value)));
}
