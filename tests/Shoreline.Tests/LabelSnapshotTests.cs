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
/// Direct-service compatibility tests for the E2 snapshot/restore/clear/merge
/// chain. Program.cs intentionally does not map these endpoints yet, so the
/// tests exercise the service and the already-existing label guard directly.
/// </summary>
[Collection("local-services")]
public sealed class LabelSnapshotTests(LocalServices services)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Create_restore_roundtrip_restores_labels_epoch_and_counts()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, 0, content: "原文");
        var translation = await TranslationAsync(world, source, world.Actor, "译文");
        var tip = await TipAsync(world, source, world.Actor, "批注");

        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId, "版本一");

        await Store("source").UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument("c", "live")));
        await Store("translation").DeleteManyAsync(new BsonDocument("o", source["_id"]));
        await Store("tip").DeleteManyAsync(new BsonDocument("o", source["_id"]));
        var extra = await SourceAsync(world, 1, content: "extra");

        var protection = await world.Service.RestoreSnapshotAsync(
            world.Actor, world.ProjectId, snapshotId.ToString());

        Assert.NotEqual(snapshotId, protection);
        var restored = await LoadAsync("source", source["_id"].AsObjectId);
        Assert.Equal("原文", restored.Text("c"));
        Assert.Equal(1L, restored["label_epoch"].ToInt64());
        Assert.Equal(1, await CountAsync("translation", new BsonDocument("o", source["_id"])));
        Assert.Equal(1, await CountAsync("tip", new BsonDocument("o", source["_id"])));
        Assert.Equal("译文", (await LoadAsync("translation", translation["_id"].AsObjectId)).Text("c"));
        Assert.Equal("批注", (await LoadAsync("tip", tip["_id"].AsObjectId)).Text("c"));
        Assert.Equal(0, await CountAsync("source", new BsonDocument("_id", extra["_id"])));

        var project = await LoadAsync("project", world.ProjectId);
        Assert.Equal(1L, project["label_revision"].ToInt64());
        Assert.False(project.Contains("label_recovery"));
        Assert.Equal(2, await CountAsync("project_label_snapshot",
            new BsonDocument { ["project"] = world.ProjectId, ["ready"] = true }));
    }

    [Fact]
    public async Task Canonical_file_target_cache_is_reused_and_stamped_without_shadow_collection()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        await TranslationAsync(world, source, world.Actor, "译文");
        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId);

        await world.Service.RestoreSnapshotAsync(world.Actor, world.ProjectId, snapshotId.ToString());
        var restoredCache = await LoadAsync("file_target_cache", world.Cache["_id"].AsObjectId);
        Assert.Equal(world.Cache["_id"], restoredCache["_id"]);
        Assert.Equal(world.Cache["f"], restoredCache["f"]);
        Assert.Equal(1L, restoredCache["label_epoch"].ToInt64());
        Assert.Equal(1, await CountAsync("file_target_cache",
            new BsonDocument("f", world.FileId)));

        await world.Service.ClearLabelsAsync(world.Actor, world.ProjectId, "source");
        var clearedCache = await LoadAsync("file_target_cache", world.Cache["_id"].AsObjectId);
        Assert.Equal(world.Cache["_id"], clearedCache["_id"]);
        Assert.Equal(2L, clearedCache["label_epoch"].ToInt64());
        Assert.Equal(1, await CountAsync("file_target_cache",
            new BsonDocument("f", world.FileId)));
        Assert.Equal(0, await CountAsync("filetargetcache", new BsonDocument()));
    }

    [Fact]
    public async Task Advance_writes_project_m_e_with_fixed_clock_and_no_raw_edit_time()
    {
        var world = await WorldAsync();
        var fixedNow = new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var fixedService = BuildService(timeProvider: new FixedClock(fixedNow));
        await SourceAsync(world);

        await fixedService.ClearLabelsAsync(world.Actor, world.ProjectId, "source");

        var project = await LoadAsync("project", world.ProjectId);
        Assert.Equal(fixedNow, project["m_e"].ToUniversalTime());
        Assert.False(project.Contains("edit_time"));
        Assert.Equal(1L, project["label_revision"].ToInt64());

        var older = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "older",
            ["st"] = 0,
            ["ou"] = world.Actor["_id"],
            ["t"] = ObjectId.GenerateNewId(),
            ["label_revision"] = 0L,
            ["m_e"] = fixedNow.AddDays(-1)
        };
        await Store("project").InsertOneAsync(older);
        var newest = await Store("project").Find(new BsonDocument("_id",
                new BsonDocument("$in", new BsonArray(new[] { world.ProjectId, older["_id"].AsObjectId }))))
            .Sort(new BsonDocument("m_e", -1)).FirstAsync();
        Assert.Equal(world.ProjectId, newest["_id"].AsObjectId);
    }

    [Fact]
    public async Task Batched_in_queries_keep_all_1001_interleaved_sources_translations_and_stamps()
    {
        var world = await WorldAsync();
        var secondFile = await FileAsync(world.ProjectId, 2);
        var sourceIds = Enumerable.Range(0, 1001)
            .Select(_ => ObjectId.GenerateNewId()).ToList();
        var now = DateTime.UtcNow;
        var sources = new List<BsonDocument>();
        for (var index = 0; index < sourceIds.Count; index++)
        {
            sources.Add(new BsonDocument
            {
                ["_id"] = sourceIds[index],
                ["f"] = index % 2 == 0 ? world.FileId : secondFile["_id"].AsObjectId,
                ["r"] = index,
                ["c"] = "source-" + index,
                ["x"] = 0.0,
                ["y"] = 0.0,
                ["v"] = new BsonArray(),
                ["p"] = index,
                ["lf"] = false,
                ["b"] = false,
                ["cm"] = false,
                ["pt"] = new BsonArray(),
                ["ct"] = now,
                ["e"] = now,
                ["label_epoch"] = 0L
            });
        }
        await Store("source").InsertManyAsync(sources);
        await Store("translation").InsertManyAsync(sources.Select((source, index) =>
            new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = source["_id"],
                ["t"] = world.TargetId,
                ["c"] = "译文-" + index,
                ["u"] = world.Actor["_id"],
                ["p"] = "校对",
                ["pr"] = world.Actor["_id"],
                ["s"] = false,
                ["mt"] = false,
                ["ct"] = now,
                ["e"] = now,
                ["label_epoch"] = 0L
            }));

        var loaded = await world.Service.LoadLabelDataAsync(world.ProjectId);
        Assert.Equal(1001, loaded["sources"].Count);
        Assert.Equal(1001, loaded["translations"].Count);
        var loadedSourceIds = loaded["sources"].Select(row => row["_id"].AsObjectId).ToList();
        Assert.Equal(loadedSourceIds.OrderBy(id => id).ToList(), loadedSourceIds);
        var digest = LabelSnapshotService.DataDigest(loaded);

        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId);
        await Store("source").DeleteOneAsync(new BsonDocument("_id", sourceIds[^1]));
        await Store("translation").DeleteManyAsync(new BsonDocument("o", sourceIds[^1]));
        await world.Service.RestoreSnapshotAsync(world.Actor, world.ProjectId, snapshotId.ToString());

        var restored = await world.Service.LoadLabelDataAsync(world.ProjectId);
        Assert.Equal(1001, restored["sources"].Count);
        Assert.Equal(1001, restored["translations"].Count);
        Assert.Equal(digest, LabelSnapshotService.DataDigest(restored));
        var fileFilter = new BsonDocument("$in",
            new BsonArray(new[] { world.FileId, secondFile["_id"].AsObjectId }));
        Assert.Equal(1001, await CountAsync("source", new BsonDocument
        {
            ["f"] = fileFilter,
            ["label_epoch"] = 1L
        }));
        Assert.Equal(1001, await CountAsync("translation", new BsonDocument
        {
            ["t"] = world.TargetId,
            ["label_epoch"] = 1L
        }));

        await world.Service.ClearLabelsAsync(world.Actor, world.ProjectId, "proofread");
        Assert.Equal(1001, await CountAsync("translation", new BsonDocument
        {
            ["t"] = world.TargetId,
            ["p"] = ""
        }));
        Assert.Equal(1001, await CountAsync("translation", new BsonDocument
        {
            ["t"] = world.TargetId,
            ["label_epoch"] = 2L
        }));
        Assert.Equal(0, await CountAsync("filetargetcache", new BsonDocument()));
    }

    [Fact]
    public async Task Corrupt_chunk_is_rejected_before_protection_snapshot_and_live_data_is_untouched()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId);

        var chunk = await Store("project_label_snapshot_chunk")
            .Find(new BsonDocument { ["snapshot"] = snapshotId, ["kind"] = "sources" })
            .FirstAsync();
        var bytes = chunk["data"].AsBsonBinaryData.Bytes;
        bytes[^1] ^= 0x40;
        await Store("project_label_snapshot_chunk").UpdateOneAsync(
            new BsonDocument("_id", chunk["_id"]),
            new BsonDocument("$set", new BsonDocument("data", new BsonBinaryData(bytes))));

        await Store("source").UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument("c", "live")));
        var error = await CaptureError(() => world.Service.RestoreSnapshotAsync(
            world.Actor, world.ProjectId, snapshotId.ToString()));

        Assert.Equal("快照完整性校验失败", error.Message);
        Assert.Equal("live", (await LoadAsync("source", source["_id"].AsObjectId)).Text("c"));
        Assert.Equal(0L, (await LoadAsync("project", world.ProjectId))["label_revision"].ToInt64());
        Assert.Equal(1, await CountAsync("project_label_snapshot", new BsonDocument("project", world.ProjectId)));
    }

    [Fact]
    public async Task Foreign_version_and_wrong_owner_snapshots_are_rejected()
    {
        var mine = await WorldAsync();
        var other = await WorldAsync();
        var row = await SourceAsync(mine);
        var snapshotId = await mine.Service.CreateSnapshotAsync(mine.Actor, mine.ProjectId);

        Assert.Equal("快照不存在或版本不兼容", (await CaptureError(() =>
            other.Service.LoadSnapshotAsync(other.ProjectId, snapshotId.ToString()))).Message);

        await Store("project_label_snapshot").UpdateOneAsync(
            new BsonDocument("_id", snapshotId),
            new BsonDocument("$set", new BsonDocument("version", 2)));
        Assert.Equal("快照不存在或版本不兼容", (await CaptureError(() =>
            mine.Service.LoadSnapshotAsync(mine.ProjectId, snapshotId.ToString()))).Message);
        Assert.Equal("source", (await LoadAsync("source", row["_id"].AsObjectId)).Text("c"));
    }

    [Fact]
    public async Task Fingerprint_change_rejects_but_extra_files_and_fields_are_allowed()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, content: "original");
        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId);

        await Store("file").UpdateOneAsync(new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("md", "changed")));
        Assert.Equal("快照关联的文件已删除或被替换，不能完整恢复", (await CaptureError(() =>
            world.Service.RestoreSnapshotAsync(world.Actor, world.ProjectId, snapshotId.ToString()))).Message);

        await Store("file").UpdateOneAsync(new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("md", "md5")));
        await Store("file").UpdateOneAsync(new BsonDocument("_id", world.FileId),
            new BsonDocument("$set", new BsonDocument("unimportant", "changed")));

        var extraFile = await FileAsync(world.ProjectId, 2);
        var extraSource = await SourceAsync(world, 1, content: "extra", fileId: extraFile["_id"].AsObjectId);
        await world.Service.RestoreSnapshotAsync(world.Actor, world.ProjectId, snapshotId.ToString());

        Assert.Equal(0, await CountAsync("source", new BsonDocument("_id", extraSource["_id"])));
        Assert.Equal(1, await CountAsync("file", new BsonDocument("_id", extraFile["_id"])));
        Assert.Equal("original", (await LoadAsync("source", source["_id"].AsObjectId)).Text("c"));
    }

    [Fact]
    public async Task Replaced_target_is_rejected_before_any_live_mutation()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var snapshotId = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId);

        await Store("target").UpdateOneAsync(new BsonDocument("_id", world.TargetId),
            new BsonDocument("$set", new BsonDocument("l", ObjectId.GenerateNewId())));
        Assert.Equal("快照关联的目标语言已删除或改变", (await CaptureError(() =>
            world.Service.RestoreSnapshotAsync(world.Actor, world.ProjectId, snapshotId.ToString()))).Message);
        Assert.Equal("source", (await LoadAsync("source", source["_id"].AsObjectId)).Text("c"));
    }

    [Fact]
    public async Task Clear_proofread_keeps_selected_state_and_optional_tips()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var proofreader = await UserAsync("proofreader");
        var translation = await TranslationAsync(world, source, world.Actor, "译文",
            proofread: "修正", proofreader: proofreader, selected: true);
        var tip = await TipAsync(world, source, world.Actor, "批注");

        var snapshotId = await world.Service.ClearLabelsAsync(
            world.Actor, world.ProjectId, "proofread", clearTips: false);

        var row = await LoadAsync("translation", translation["_id"].AsObjectId);
        Assert.Equal("", row.Text("p"));
        Assert.False(row.Contains("pr"));
        Assert.True(row.Flag("s"));
        Assert.Equal(1, await CountAsync("tip", new BsonDocument("_id", tip["_id"])));
        Assert.Equal("clear", (await LoadAsync("project_label_snapshot", snapshotId)).Text("reason"));
        Assert.Equal(1L, (await LoadAsync("project", world.ProjectId))["label_revision"].ToInt64());
    }

    [Fact]
    public async Task Clear_translation_deletes_translations_but_preserves_tips_and_sources()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var translation = await TranslationAsync(world, source, world.Actor, "译文");
        var tip = await TipAsync(world, source, world.Actor, "批注");

        await world.Service.ClearLabelsAsync(world.Actor, world.ProjectId, "translation");

        Assert.Equal(0, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));
        Assert.Equal(1, await CountAsync("tip", new BsonDocument("_id", tip["_id"])));
        Assert.Equal(1, await CountAsync("source", new BsonDocument("_id", source["_id"])));
    }

    [Fact]
    public async Task Clear_source_blanks_text_sources_but_leaves_image_content()
    {
        var text = await WorldAsync(fileType: 3);
        var textSource = await SourceAsync(text, content: "文字原稿");
        await TranslationAsync(text, textSource, text.Actor, "text translation");
        await text.Service.ClearLabelsAsync(text.Actor, text.ProjectId, "source");
        var blanked = await LoadAsync("source", textSource["_id"].AsObjectId);
        Assert.Equal("", blanked.Text("c"));
        Assert.True(blanked.Flag("b"));
        Assert.Empty(blanked["pt"].AsBsonArray);
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("o", textSource["_id"])));

        var image = await WorldAsync(fileType: 2);
        var imageSource = await SourceAsync(image, content: "图片文字");
        await TranslationAsync(image, imageSource, image.Actor, "image translation");
        await image.Service.ClearLabelsAsync(image.Actor, image.ProjectId, "source");
        var kept = await LoadAsync("source", imageSource["_id"].AsObjectId);
        Assert.Equal("图片文字", kept.Text("c"));
        Assert.False(kept.Flag("b"));
        Assert.Equal(0, await CountAsync("translation", new BsonDocument("o", imageSource["_id"])));
    }

    [Fact]
    public async Task Delete_translation_labels_requires_empty_source_and_creates_no_snapshot_on_refusal()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, content: "not empty");

        var error = await CaptureError(() => world.Service.ClearLabelsAsync(
            world.Actor, world.ProjectId, "translation", deleteLabels: true));
        Assert.Equal("原文字段有内容，不能完全删除标签", error.Message);
        Assert.Equal(0, await CountAsync("project_label_snapshot", new BsonDocument("project", world.ProjectId)));

        await Store("source").UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument { ["c"] = "", ["b"] = true }));
        await world.Service.ClearLabelsAsync(world.Actor, world.ProjectId, "translation", deleteLabels: true);
        Assert.Equal(0, await CountAsync("source", new BsonDocument("_id", source["_id"])));
        Assert.Equal(1, await CountAsync("project_label_snapshot", new BsonDocument("project", world.ProjectId)));
    }

    [Fact]
    public async Task Delete_proofread_labels_is_rejected()
    {
        var world = await WorldAsync();
        var error = await CaptureError(() => world.Service.ClearLabelsAsync(
            world.Actor, world.ProjectId, "proofread", deleteLabels: true));
        Assert.Equal("清空校对不能删除标签", error.Message);
        Assert.Equal(0, await CountAsync("project_label_snapshot", new BsonDocument("project", world.ProjectId)));
    }

    [Fact]
    public async Task Statistics_failure_rolls_back_labels_and_advances_revision()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var translation = await TranslationAsync(world, source, world.Actor, "译文");
        var fired = false;
        var service = BuildService((phase, _) =>
        {
            if (phase == "before_rebuild" && !fired)
            {
                fired = true;
                throw new InvalidOperationException("before rebuild");
            }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearLabelsAsync(
            world.Actor, world.ProjectId, "translation"));

        Assert.Equal(1, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));
        Assert.Equal("译文", (await LoadAsync("translation", translation["_id"].AsObjectId)).Text("c"));
        var project = await LoadAsync("project", world.ProjectId);
        Assert.Equal(1L, project["label_revision"].ToInt64());
        Assert.False(project.Contains("label_recovery"));
        Assert.Equal("source", (await LoadAsync("source", source["_id"].AsObjectId)).Text("c"));
    }

    [Fact]
    public async Task Double_failure_keeps_journal_then_recover_restores_and_clears_it()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var translation = await TranslationAsync(world, source, world.Actor, "译文");
        var service = BuildService((phase, _) => phase is "before_rebuild" or "rollback_before_rebuild"
            ? throw new InvalidOperationException(phase)
            : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearLabelsAsync(
            world.Actor, world.ProjectId, "translation"));

        var project = await LoadAsync("project", world.ProjectId);
        Assert.True(project.Contains("label_recovery"));
        Assert.Equal(0L, project["label_revision"].ToInt64());
        Assert.Equal(1, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));

        await world.Service.RecoverAsync(world.Actor, world.ProjectId);

        project = await LoadAsync("project", world.ProjectId);
        Assert.False(project.Contains("label_recovery"));
        Assert.Equal(1L, project["label_revision"].ToInt64());
        Assert.Equal(1, await CountAsync("translation", new BsonDocument("_id", translation["_id"])));
        Assert.Equal(1L, (await LoadAsync("translation", translation["_id"].AsObjectId))
            ["label_epoch"].ToInt64());
    }

    [Fact]
    public async Task Recover_without_pending_journal_is_rejected()
    {
        var world = await WorldAsync();
        var error = await CaptureError(() => world.Service.RecoverAsync(world.Actor, world.ProjectId));
        Assert.Equal("项目没有待恢复操作", error.Message);
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task Merge_unique_winner_upserts_translation_and_clears_proofread_fields()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, content: "原文");
        var proofreader = await UserAsync("proofreader");
        var translation = await TranslationAsync(world, source, world.Actor, "原始译文",
            proofread: "校对译文", proofreader: proofreader);
        var tip = await TipAsync(world, source, world.Actor, "批注");

        var preview = await world.Service.PreviewMergeAsync(world.Actor, world.ProjectId, world.TargetId);
        Assert.Single(preview.Candidates);
        Assert.Empty(preview.SelectionConflicts);
        Assert.Equal(1, preview.ProofreadCount);
        Assert.True(preview.HasSourceContent);
        Assert.Single(preview.Candidates[0].Versions);

        await world.Service.MergeLabelsAsync(world.Actor, world.ProjectId, world.TargetId,
            preview.Token, null, null);

        var merged = await Store("translation").Find(new BsonDocument
        {
            ["o"] = source["_id"],
            ["u"] = proofreader["_id"]
        }).ToListAsync();
        var row = Assert.Single(merged);
        Assert.Equal("校对译文", row.Text("c"));
        Assert.Equal("", row.Text("p"));
        Assert.True(row.Flag("s"));
        Assert.Equal(world.Actor["_id"].AsObjectId, row["sr"].AsObjectId);
        Assert.False(row.Flag("mt"));
        Assert.Equal("原始译文", (await LoadAsync("translation", translation["_id"].AsObjectId)).Text("c"));
        Assert.Equal(1, await CountAsync("tip", new BsonDocument("_id", tip["_id"])));
        Assert.Equal(1L, (await LoadAsync("project", world.ProjectId))["label_revision"].ToInt64());
    }

    [Fact]
    public async Task Merge_conflicting_versions_requires_an_explicit_version()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var writer = await UserAsync("writer");
        var proofreader = await UserAsync("proofreader");
        await TranslationAsync(world, source, writer, "base",
            proofread: "version 1", proofreader: proofreader);
        await TranslationAsync(world, source, world.Actor, "base",
            proofread: "version 2", proofreader: proofreader);

        var preview = await world.Service.PreviewMergeAsync(world.Actor, world.ProjectId, world.TargetId);
        var candidate = Assert.Single(preview.Candidates);
        Assert.Equal(2, candidate.Versions.Count);
        Assert.Empty(preview.SelectionConflicts);

        var error = await CaptureError(() => world.Service.MergeLabelsAsync(
            world.Actor, world.ProjectId, world.TargetId, preview.Token, null, null));
        Assert.Equal("请解决所有校对内容冲突", error.Message);

        await world.Service.MergeLabelsAsync(world.Actor, world.ProjectId, world.TargetId,
            preview.Token, new Dictionary<string, string?> { [candidate.Key] = candidate.Versions[1].Id },
            null);
        var rows = await Store("translation").Find(new BsonDocument("o", source["_id"])).ToListAsync();
        Assert.Contains(rows, row => row.Text("c") == "version 2" && row.Flag("s"));
        Assert.All(rows, row => Assert.Equal("", row.Text("p")));
    }

    [Fact]
    public async Task Merge_multiple_proofreaders_requires_a_selection_for_each_source()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world);
        var writer = await UserAsync("writer");
        var first = await UserAsync("first");
        var second = await UserAsync("second");
        var firstRow = await TranslationAsync(world, source, writer, "base",
            proofread: "first proof", proofreader: first);
        var secondRow = await TranslationAsync(world, source, world.Actor, "base",
            proofread: "second proof", proofreader: second);

        var preview = await world.Service.PreviewMergeAsync(world.Actor, world.ProjectId, world.TargetId);
        Assert.Equal(2, preview.Candidates.Count);
        var conflict = Assert.Single(preview.SelectionConflicts);
        Assert.Equal(2, conflict.Users.Count);

        var error = await CaptureError(() => world.Service.MergeLabelsAsync(
            world.Actor, world.ProjectId, world.TargetId, preview.Token, null, null));
        Assert.Equal("请选择每个标签最终选定的翻译", error.Message);

        await world.Service.MergeLabelsAsync(world.Actor, world.ProjectId, world.TargetId,
            preview.Token, null,
            new Dictionary<string, string?> { [source["_id"].AsObjectId.ToString()] = first["_id"].AsObjectId.ToString() });

        var firstMerged = Assert.Single(await Store("translation").Find(new BsonDocument
        {
            ["o"] = source["_id"],
            ["u"] = first["_id"]
        }).ToListAsync());
        var secondMerged = Assert.Single(await Store("translation").Find(new BsonDocument
        {
            ["o"] = source["_id"],
            ["u"] = second["_id"]
        }).ToListAsync());
        Assert.True(firstMerged.Flag("s"));
        Assert.False(secondMerged.Flag("s"));
        Assert.False((await LoadAsync("translation", firstRow["_id"].AsObjectId)).Flag("s"));
        Assert.False((await LoadAsync("translation", secondRow["_id"].AsObjectId)).Flag("s"));
        Assert.All(await Store("translation").Find(new BsonDocument("o", source["_id"])).ToListAsync(),
            row => Assert.Equal("", row.Text("p")));
    }

    [Fact]
    public async Task Merge_stale_token_is_rejected_and_creates_no_snapshot()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, content: "before");
        var proofreader = await UserAsync("proofreader");
        await TranslationAsync(world, source, world.Actor, "base",
            proofread: "proof", proofreader: proofreader);
        var preview = await world.Service.PreviewMergeAsync(world.Actor, world.ProjectId, world.TargetId);

        await Store("source").UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument("c", "after")));
        var error = await CaptureError(() => world.Service.MergeLabelsAsync(
            world.Actor, world.ProjectId, world.TargetId, preview.Token, null, null));
        Assert.Equal("预览后数据已改变，请重新确认冲突", error.Message);
        Assert.Equal(0, await CountAsync("project_label_snapshot", new BsonDocument("project", world.ProjectId)));
    }

    [Fact]
    public async Task Stale_revision_rejects_snapshot_creation_without_writing()
    {
        var world = await WorldAsync();
        var error = await CaptureError(() => world.Service.CreateSnapshotAsync(
            world.Actor, world.ProjectId, "stale", "manual", expectedRevision: 1));
        Assert.Equal("LabelBusyError", error.Error);
        Assert.Equal(1, error.Code);
        Assert.Equal(409, error.Status);
        Assert.Equal(0, await CountAsync("project_label_snapshot",
            new BsonDocument("project", world.ProjectId)));
    }

    [Fact]
    public async Task List_state_and_pagination_use_ready_snapshots_and_canonical_counts()
    {
        var world = await WorldAsync();
        var source = await SourceAsync(world, content: "content");
        var first = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId, "first");
        var second = await world.Service.CreateSnapshotAsync(world.Actor, world.ProjectId, "second");
        await Store("project_label_snapshot").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = world.ProjectId,
            ["name"] = "not ready",
            ["created"] = DateTime.UtcNow,
            ["ready"] = false,
            ["counts"] = new BsonDocument()
        });

        var page = await world.Service.ListSnapshotsAsync(world.Actor, world.ProjectId, 1, 1);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal("second", page.Items[0].Name);
        Assert.Equal("manual", page.Items[0].Reason);
        Assert.Equal(1, page.Items[0].Counts["sources"]);
        Assert.Equal(5, page.Items[0].Counts.Count);

        var state = await world.Service.GetLabelStateAsync(world.Actor, world.ProjectId);
        Assert.True(state.HasSourceContent);
        Assert.False(state.RecoveryRequired);
        Assert.Equal(1, state.Counts["sources"]);
        Assert.True(state.Capabilities["create"]);
        Assert.True(state.Capabilities["restore"]);
        Assert.True(state.Capabilities["clear_proofread"]);
        Assert.True(state.Capabilities["merge"]);
        Assert.Equal(1, await CountAsync("project_label_snapshot",
            new BsonDocument("_id", first)));
        Assert.Equal(1, await CountAsync("project_label_snapshot",
            new BsonDocument("_id", second)));
    }

    [Fact]
    public async Task Permissions_match_python_manager_proofreader_and_site_admin_rules()
    {
        var world = await WorldAsync(tags: ["proofreader"]);
        var proofreader = world.Actor;
        var capabilities = await world.Service.CapabilitiesForProjectAsync(proofreader, world.ProjectId);
        Assert.True(capabilities["create"]);
        Assert.True(capabilities["clear_proofread"]);
        Assert.False(capabilities["restore"]);
        Assert.True(capabilities["merge"]);

        var outsider = await UserAsync("outsider");
        var outsiderCapabilities = await world.Service.CapabilitiesForProjectAsync(outsider, world.ProjectId);
        Assert.False(outsiderCapabilities["create"]);
        Assert.False(outsiderCapabilities["clear_proofread"]);

        var admin = await UserAsync("site-admin", admin: true);
        var adminCapabilities = await world.Service.CapabilitiesForProjectAsync(admin, world.ProjectId);
        Assert.False(adminCapabilities["create"]);
        Assert.False(adminCapabilities["restore"]);

        await Assert.ThrowsAsync<ApiError>(() => world.Service.RequireForProjectAsync(
            outsider, world.ProjectId, "clear_proofread"));
        var error = await CaptureError(() => world.Service.RequireForProjectAsync(
            admin, world.ProjectId, "restore"));
        Assert.Equal(103, error.Code);
    }

    [Fact]
    public async Task Request_guard_and_scoped_service_share_one_mongo_lock_and_emit_headers()
    {
        var world = await WorldAsync();
        await SourceAsync(world);
        var clock = TimeProvider.System;
        var labels = new ProjectLabelLock(services.Store, clock);
        var collection = new ServiceCollection();
        collection.AddSingleton(services.Store);
        collection.AddSingleton(clock);
        collection.AddSingleton(labels);
        collection.AddSingleton(new IdentityPermissions(services.Store, clock));
        collection.AddScoped<LabelRequestContext>();
        collection.AddScoped(provider => new LabelSnapshotService(
            provider.GetRequiredService<MongoStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IdentityPermissions>(),
            provider.GetRequiredService<ProjectLabelLock>(),
            provider.GetRequiredService<LabelRequestContext>()));
        await using var provider = collection.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        LabelSnapshotService.LabelState? state = null;
        var guard = new LabelRequestGuard(async http =>
        {
            var service = http.RequestServices.GetRequiredService<LabelSnapshotService>();
            state = await service.GetLabelStateAsync(world.Actor, world.ProjectId);
        }, services.Store, labels);

        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/projects/" + world.ProjectId + "/label-state";
        context.Request.RouteValues["projectId"] = world.ProjectId.ToString();

        await guard.InvokeAsync(context);

        Assert.NotNull(state);
        Assert.True(state.HasSourceContent);
        Assert.Equal(world.ProjectId.ToString(), context.Response.Headers["X-Label-Project"].ToString());
        Assert.Equal("0", context.Response.Headers["X-Label-Revision"].ToString());
        Assert.Equal(0, await CountAsync("project_label_lock",
            new BsonDocument("project", world.ProjectId)));
    }

    private sealed class World
    {
        public required BsonDocument Actor { get; init; }
        public required BsonDocument Project { get; init; }
        public required BsonDocument File { get; init; }
        public required BsonDocument Target { get; init; }
        public required BsonDocument Cache { get; init; }
        public required LabelSnapshotService Service { get; init; }
        public ObjectId ProjectId => Project["_id"].AsObjectId;
        public ObjectId FileId => File["_id"].AsObjectId;
        public ObjectId TargetId => Target["_id"].AsObjectId;
    }

    private async Task<World> WorldAsync(int fileType = 2, string[]? tags = null)
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
            ["label_revision"] = 0L,
            ["et"] = now
        };
        await Store("project").InsertOneAsync(project);
        await AddMemberAsync(projectId, actor, tags ?? new[] { "creator" });

        var languageId = ObjectId.GenerateNewId();
        await Store("language").InsertOneAsync(new BsonDocument
        {
            ["_id"] = languageId,
            ["co"] = "zh-" + languageId.ToString(),
            ["c"] = "中文",
            ["e"] = "Chinese"
        });

        var file = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "file",
            ["t"] = fileType,
            ["p"] = projectId,
            ["a"] = new BsonArray(),
            ["ac"] = true,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now,
            ["sa"] = true,
            ["fs"] = 10,
            ["md"] = "md5",
            ["v"] = 1,
            ["label_epoch"] = 0L
        };
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
            ["label_epoch"] = 0L
        };
        await Store("target").InsertOneAsync(target);
        var cache = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = file["_id"],
            ["t"] = target["_id"],
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = now,
            ["label_epoch"] = 0L
        };
        await Store("file_target_cache").InsertOneAsync(cache);

        var service = BuildService();
        await service.InitializeAsync(Ct);
        return new World
        {
            Actor = actor,
            Project = project,
            File = file,
            Target = target,
            Cache = cache,
            Service = service
        };
    }

    private LabelSnapshotService BuildService(
        Func<string, CancellationToken, Task>? fault = null,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        return new LabelSnapshotService(
            services.Store,
            clock,
            new IdentityPermissions(services.Store, clock),
            new ProjectLabelLock(services.Store, clock),
            new LabelRequestContext(),
            fault);
    }

    private async Task<BsonDocument> UserAsync(string prefix, bool admin = false)
    {
        var user = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = prefix + "-" + ObjectId.GenerateNewId().ToString(),
            ["e"] = prefix + "-" + ObjectId.GenerateNewId().ToString() + "@example.test",
            ["as"] = new BsonArray(),
            ["l"] = "auto",
            ["admin"] = admin,
            ["b"] = false
        };
        await Store("user").InsertOneAsync(user);
        return user;
    }

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
        int rank = 0,
        string content = "source",
        ObjectId? fileId = null)
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
            ["p"] = 0,
            ["lf"] = false,
            ["b"] = false,
            ["cm"] = false,
            ["pt"] = new BsonArray(),
            ["ct"] = now,
            ["e"] = now,
            ["label_epoch"] = 0L
        };
        await Store("source").InsertOneAsync(source);
        return source;
    }

    private async Task<BsonDocument> TranslationAsync(
        World world,
        BsonDocument source,
        BsonDocument user,
        string content,
        string proofread = "",
        BsonDocument? proofreader = null,
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
            ["p"] = proofread,
            ["s"] = selected,
            ["mt"] = mt,
            ["ct"] = now,
            ["e"] = now,
            ["label_epoch"] = 0L
        };
        if (proofreader is not null) translation["pr"] = proofreader["_id"];
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
            ["e"] = now,
            ["label_epoch"] = 0L
        };
        await Store("tip").InsertOneAsync(tip);
        return tip;
    }

    private async Task<BsonDocument> FileAsync(ObjectId projectId, int fileType)
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
            ["sa"] = true,
            ["md"] = "extra",
            ["v"] = 1,
            ["label_epoch"] = 0L
        };
        await Store("file").InsertOneAsync(file);
        return file;
    }

    private Task<BsonDocument> LoadAsync(string collection, ObjectId id) =>
        Store(collection).Find(new BsonDocument("_id", id)).FirstAsync();

    private Task<long> CountAsync(string collection, BsonDocument filter) =>
        Store(collection).CountDocumentsAsync(filter);

    private IMongoCollection<BsonDocument> Store(string collection) =>
        services.Store.Collection(collection);

    private static async Task<ApiError> CaptureError(Func<Task> action) =>
        await Assert.ThrowsAsync<ApiError>(action);

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
