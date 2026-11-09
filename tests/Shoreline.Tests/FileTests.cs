using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services-file")]
public sealed class FileTests(LocalServices services)
{
    private static readonly ObjectId ProjectId = ObjectId.Parse("653333333333333333333333");
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyBsonFixture_ToApi_KeepsRawFieldsAndConvertsUnits()
    {
        var clock = new TestClock(BaseTime);
        var files = Service(clock);
        var project = await InsertProjectAsync();
        var file = await InsertFileAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse("654444444444444444444444"),
            ["n"] = "001.png",
            ["t"] = FileService.TypeImage,
            ["p"] = project["_id"].AsObjectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["mo"] = BsonNull.Value,
            ["sn"] = "000001.png",
            ["dn"] = "",
            ["sa"] = "654444444444444444444444.png",
            ["md"] = "d41d8cd98f00b204e9800998ecf8427e",
            ["fs"] = new BsonInt64(2),
            ["cover_bytes"] = new BsonInt64(2048),
            ["resample_bytes"] = new BsonInt64(1024),
            ["fn"] = 0,
            ["th"] = 0,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 5,
            ["tsc"] = 1,
            ["csc"] = 2,
            ["et"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["v"] = 1,
            ["ac"] = true,
            ["ss"] = 0,
            ["ps"] = 2,
            ["pe"] = 1,
            ["op"] = 55,
        });

        var api = await files.GetAsync(file, project, null, CancellationToken.None);

        Assert.Equal(file["_id"].AsObjectId.ToString(), api["id"]);
        Assert.Equal(2L, api["file_size"]);
        Assert.Equal(2048L, api["cover_bytes"]);
        Assert.Equal(1024L, api["resample_bytes"]);
        Assert.Equal("自动标记中", api["parse_status_detail_name"]);
        Assert.Equal("未知字符集", api["parse_error_type_detail_name"]);
        Assert.Equal("图片识别中", api["image_ocr_percent_detail_name"]);
        Assert.Equal("generating", api["cover_url"]);
        Assert.NotNull(api["url"]);
        Assert.True(file.Contains("cover_bytes"));
        Assert.False(file.Contains("cb"));
    }

    [Fact]
    public void Filename_PythonValidationAndSortName()
    {
        var parsed = FileService.ParseFilename("作品 0012.PNG", folder: false);
        Assert.Equal(FileService.TypeImage, parsed.Type);
        Assert.Equal("png", parsed.Suffix.ToLowerInvariant());
        Assert.Equal("作品 000012", parsed.SortName);

        Assert.Equal(FileService.TypeImage, FileService.ParseFilename("A.JPEG", false).Type);
        Assert.Equal(FileService.TypeUnknown, FileService.ParseFilename("A.zip", false).Type);

        Assert.Equal("文件名错误: 文件名为空", Error(() => FileService.ParseFilename("   ", false)));
        Assert.Equal("文件名错误: 文件名过长", Error(() => FileService.ParseFilename(new string('中', 128), false)));
        Assert.Equal("文件名错误: 文件名不能是 . 或 ..", Error(() => FileService.ParseFilename(" . ", false)));
        Assert.Equal("文件名错误: 文件名不能以 . 结尾", Error(() => FileService.ParseFilename("a.", false)));
        Assert.Equal("文件名错误: 文件名前缀为空", Error(() => FileService.ParseFilename(".png", false)));
        Assert.Contains("\\ / : * ?", Error(() => FileService.ParseFilename("a/b.png", false)));
        Assert.Equal(107, ErrorCode(() => FileService.ParseFilename("a/b.png", false)));
    }

    [Fact]
    public void OrderSnapshot_PythonGoldenVector()
    {
        var files = new List<BsonDocument>
        {
            new()
            {
                ["_id"] = ObjectId.Parse("651111111111111111111111"),
                ["n"] = "001.png",
                ["mo"] = BsonNull.Value,
            },
            new()
            {
                ["_id"] = ObjectId.Parse("652222222222222222222222"),
                ["n"] = "作品 2.jpg",
                ["mo"] = 3,
            },
        };

        const string expected = "[[\"651111111111111111111111\", \"001.png\", null], [\"652222222222222222222222\", \"作品 2.jpg\", 3]]";
        Assert.Equal(expected, FileService.SnapshotJson(files));
        Assert.Equal(
            "141c271429d8e7a8aaef0ed105ff0e8cc0eeabad3b933520f9ab8ab50c554306",
            FileService.SnapshotVersion(files));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(),
            FileService.SnapshotVersion(files));
    }

    [Fact]
    public async Task List_RootParentIsolation_FiltersAndPaginationShape()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var folder = await InsertFileAsync(Folder(project, "001folder", root: true));
        var rootImage = await InsertFileAsync(Image(project, "002.png", root: true));
        var child = await InsertFileAsync(Image(project, "003.png", parent: folder));

        var rootPage = await files.ListAsync(
            project,
            new FileListQuery(null, false, false, [], null, null),
            new Pagination(1, 20),
            CancellationToken.None);
        Assert.Equal(2, rootPage.Count);
        Assert.DoesNotContain(rootPage.Items, item => (string?)item["id"] == child["_id"].AsObjectId.ToString());

        var inside = await files.ListAsync(
            project,
            new FileListQuery(folder["_id"].AsObjectId.ToString(), false, false, [], "3", null),
            new Pagination(1, 20),
            CancellationToken.None);
        Assert.Single(inside.Items);
        Assert.Equal((long)FileService.TypeImage, (long)inside.Items[0]["type"]!);

        var conflict = await Assert.ThrowsAsync<ApiError>(() => files.ListAsync(
            project,
            new FileListQuery(null, true, true, [], null, null),
            new Pagination(1, 20),
            CancellationToken.None));
        Assert.Equal(2, conflict.Code);
        Assert.Equal("can not use `only_file` and `only_folder` at one time", conflict.Message);

        var manual = await files.ListAsync(
            project,
            new FileListQuery(null, false, true, ["dir_sort_name", "type", "-sort_name"], null, null),
            new Pagination(1, 20),
            CancellationToken.None);
        Assert.Equal(rootImage["_id"].AsObjectId.ToString(), manual.Items[0]["id"]);
        Assert.DoesNotContain(manual.Items, item => (string?)item["id"] == folder["_id"].AsObjectId.ToString());

        var onlyFolders = await files.ListAsync(
            project,
            new FileListQuery(null, true, false, [], null, null),
            new Pagination(1, 20),
            CancellationToken.None);
        Assert.Single(onlyFolders.Items);
        Assert.Equal((long)FileService.TypeFolder, (long)onlyFolders.Items[0]["type"]!);
    }

    [Fact]
    public async Task List_TargetCacheFallbackAndForeignTargetRejected()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var image = await InsertFileAsync(Image(project, "001.png", root: true));
        var target = new BsonDocument
        {
            ["_id"] = ObjectId.Parse("655555555555555555555555"),
            ["t"] = project["_id"].AsObjectId,
            ["l"] = "zh",
        };
        await services.Store.Collection("target").InsertOneAsync(target);
        await services.Store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = image["_id"].AsObjectId,
            ["t"] = target["_id"].AsObjectId,
            ["ts"] = 4,
            ["cs"] = 2,
            ["e"] = new BsonDateTime(BaseTime.UtcDateTime),
        });

        var withTarget = await files.ListAsync(
            project,
            new FileListQuery(null, false, false, [], null, target["_id"].AsObjectId.ToString()),
            new Pagination(1, 20),
            CancellationToken.None);
        var cache = Assert.IsType<Dictionary<string, object?>>(withTarget.Items[0]["file_target_cache"]);
        Assert.Equal(4L, (long)cache["translated_source_count"]!);
        Assert.Equal(2L, (long)cache["checked_source_count"]!);

        var second = await InsertFileAsync(Image(project, "002.png", root: true));
        var missing = await files.GetAsync(second, project, target["_id"].AsObjectId.ToString(), CancellationToken.None);
        var empty = Assert.IsType<Dictionary<string, object?>>(missing["file_target_cache"]);
        Assert.Null(empty["id"]);
        Assert.Equal(0, empty["translated_source_count"]);

        var foreign = new BsonDocument
        {
            ["_id"] = ObjectId.Parse("656666666666666666666666"),
            ["t"] = ObjectId.Parse("657777777777777777777777"),
            ["l"] = "ja",
        };
        await services.Store.Collection("target").InsertOneAsync(foreign);
        var error = await Assert.ThrowsAsync<ApiError>(() => files.ListAsync(
            project,
            new FileListQuery(null, false, false, [], null, foreign["_id"].AsObjectId.ToString()),
            new Pagination(1, 20),
            CancellationToken.None));
        Assert.Equal(4024, error.Code);
    }

    [Fact]
    public async Task Upload_NewOverwriteSameId_NewKey_OldKeyCleanupAndKbCache()
    {
        var clock = new TestClock(BaseTime);
        var files = Service(clock);
        var project = await InsertProjectAsync();
        var storage = Storage();

        var firstBytes = new byte[1500];
        var first = await files.UploadAsync(
            project,
            "作品 001.PNG",
            Form("作品 001.PNG", firstBytes),
            null,
            CancellationToken.None);

        Assert.Equal((long)FileService.TypeImage, (long)first["type"]!);
        Assert.Equal(2L, first["file_size"]);
        Assert.False((bool)first["upload_overwrite"]!);
        var firstId = (string)first["id"]!;
        var firstSave = (string)first["save_name"]!;
        Assert.EndsWith(".PNG", firstSave);
        Assert.NotNull(await storage.HeadAsync(FileService.FilePrefix + firstSave));

        var stored = await services.Store.Collection("file")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(firstId)))
            .FirstAsync();
        Assert.Equal(0, stored.Integer("fn"));
        Assert.Equal(new BsonInt64(2), stored["fs"]);
        var projectAfterFirst = await LoadProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(2L, projectAfterFirst["fs"].ToInt64());

        var secondBytes = new byte[4096];
        var second = await files.UploadAsync(
            project,
            "作品 001.PNG",
            Form("作品 001.PNG", secondBytes),
            null,
            CancellationToken.None);

        Assert.Equal(firstId, second["id"]);
        Assert.True((bool)second["upload_overwrite"]!);
        var secondSave = (string)second["save_name"]!;
        Assert.NotEqual(firstSave, secondSave);
        Assert.Null(await storage.HeadAsync(FileService.FilePrefix + firstSave));
        Assert.NotNull(await storage.HeadAsync(FileService.FilePrefix + secondSave));
        Assert.Equal(4L, second["file_size"]);

        var projectAfterSecond = await LoadProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(4L, projectAfterSecond["fs"].ToInt64());
        Assert.Equal(1, projectAfterSecond.Integer("fc"));
        Assert.Equal(1L, await services.Store.Collection("file").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("p", project["_id"].AsObjectId)));

        var text = await Assert.ThrowsAsync<ApiError>(() => files.UploadAsync(
            project, "note.txt", Form("note.txt", [1, 2, 3]), null, CancellationToken.None));
        Assert.Equal(107, text.Code);
        Assert.Contains("暂不支持的文件格式", text.Message);

        var pooled = await InsertProjectAsync(storageSourceId: ObjectId.Parse("658888888888888888888888"));
        var unsupported = await Assert.ThrowsAsync<ApiError>(() => files.UploadAsync(
            pooled, "x.png", Form("x.png", [1]), null, CancellationToken.None));
        Assert.Equal("StoragePoolUnsupported", unsupported.Error);
        Assert.Equal(409, unsupported.Status);
    }

    [Fact]
    public async Task FolderRename_RewritesDescendantDirectoriesAndRejectsDuplicate()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var folder = await InsertFileAsync(Folder(project, "001old", root: true));
        var child = await InsertFileAsync(Image(project, "x.png", parent: folder));
        var grandchildFolder = await InsertFileAsync(Folder(project, "sub", parent: child));
        var grandchild = await InsertFileAsync(Image(project, "y.png", parent: grandchildFolder));
        var other = await InsertFileAsync(Folder(project, "Other", root: true));

        var duplicate = await Assert.ThrowsAsync<ApiError>(() =>
            files.RenameAsync(folder, project, "other", CancellationToken.None));
        Assert.Equal(4007, duplicate.Code);

        await files.RenameAsync(folder, project, "002new", CancellationToken.None);

        var renamed = await LoadFileAsync(folder["_id"].AsObjectId);
        Assert.Equal("002new", renamed.Text("n"));
        Assert.Equal("000002new", renamed.Text("sn"));
        Assert.Equal("000002new/", (await LoadFileAsync(child["_id"].AsObjectId)).Text("dn"));
        Assert.Equal(
            "000002new/x/",
            (await LoadFileAsync(grandchildFolder["_id"].AsObjectId)).Text("dn"));
        Assert.Equal(
            "000002new/x/sub/",
            (await LoadFileAsync(grandchild["_id"].AsObjectId)).Text("dn"));

        var suffix = await Assert.ThrowsAsync<ApiError>(() =>
            files.RenameAsync(child, project, "x.txt", CancellationToken.None));
        Assert.Equal(8003, suffix.Code);

        var deactivated = Image(project, "rev.png", root: true);
        deactivated["ac"] = false;
        await InsertFileAsync(deactivated);
        var notActivated = await Assert.ThrowsAsync<ApiError>(() =>
            files.RenameAsync(deactivated, project, "rev2.png", CancellationToken.None));
        Assert.Equal(4016, notActivated.Code);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Move_FolderDescendantsAncestorsCountsAndCycleGuards()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var target = await InsertFileAsync(Folder(project, "target", root: true));
        var moving = await InsertFileAsync(Folder(project, "moving", root: true));
        var inner = await InsertFileAsync(Image(project, "inner.png", parent: moving));
        var movingCounts = new BsonDocument
        {
            ["_id"] = moving["_id"].AsObjectId,
            ["p"] = project["_id"].AsObjectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["n"] = "moving",
            ["t"] = FileService.TypeFolder,
            ["sn"] = "moving",
            ["dn"] = "",
            ["fc"] = 1,
            ["fo"] = 0,
            ["sc"] = 0,
            ["fs"] = new BsonInt64(0),
            ["ac"] = true,
        };
        moving = await ReplaceFileAsync(moving["_id"].AsObjectId, movingCounts);

        await files.MoveAsync(moving, project, target["_id"].AsObjectId.ToString(), CancellationToken.None);

        var moved = await LoadFileAsync(moving["_id"].AsObjectId);
        Assert.Equal(target["_id"].AsObjectId, moved["f"].AsObjectId);
        Assert.Equal(target["_id"].AsObjectId, moved["a"][0].AsObjectId);
        Assert.Equal("target/", moved.Text("dn"));

        var movedInner = await LoadFileAsync(inner["_id"].AsObjectId);
        Assert.Equal(2, movedInner["a"].AsBsonArray.Count);
        Assert.Equal(target["_id"].AsObjectId, movedInner["a"][0].AsObjectId);
        Assert.Equal("target/moving/", movedInner.Text("dn"));

        var parentAfter = await LoadFileAsync(target["_id"].AsObjectId);
        Assert.Equal(1, parentAfter.Integer("fc"));
        Assert.Equal(1, parentAfter.Integer("fo"));

        var self = await Assert.ThrowsAsync<ApiError>(() => files.MoveAsync(
            moved, project, moving["_id"].AsObjectId.ToString(), CancellationToken.None));
        Assert.Equal(4012, self.Code);

        var same = await Assert.ThrowsAsync<ApiError>(() => files.MoveAsync(
            moved, project, target["_id"].AsObjectId.ToString(), CancellationToken.None));
        Assert.Equal(4013, same.Code);

        var cycle = await Assert.ThrowsAsync<ApiError>(() => files.MoveAsync(
            target, project, moving["_id"].AsObjectId.ToString(), CancellationToken.None));
        Assert.Equal(4014, cycle.Code);

        var folderNotFound = await Assert.ThrowsAsync<ApiError>(() => files.MoveAsync(
            moved, project, ObjectId.GenerateNewId().ToString(), CancellationToken.None));
        Assert.Equal(8002, folderNotFound.Code);

        var duplicate = await InsertFileAsync(Image(project, "inner.png", parent: target));
        Assert.NotNull(duplicate);
    }

    [Fact]
    public async Task Delete_RemovesOriginalAndDerivedKeepsProofreadAndRevisions()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var storage = Storage();

        var saveName = "659999999999999999999999.png";
        await PutAsync(storage, FileService.FilePrefix + saveName);
        await PutAsync(storage, FileService.FilePrefix + "cover-659999999999999999999999.webp");
        await PutAsync(storage, FileService.FilePrefix + "resample-659999999999999999999999.webp");
        await PutAsync(storage, "proofread/long-lived-proofread.webp");

        var file = await InsertFileAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse("659999999999999999999999"),
            ["n"] = "del.png",
            ["t"] = FileService.TypeImage,
            ["p"] = project["_id"].AsObjectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["mo"] = BsonNull.Value,
            ["sn"] = "000000del.png",
            ["dn"] = "",
            ["sa"] = saveName,
            ["md"] = "abc",
            ["fs"] = new BsonInt64(2),
            ["cover_bytes"] = new BsonInt64(1024),
            ["resample_bytes"] = new BsonInt64(1024),
            ["fn"] = 0,
            ["th"] = 3,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["v"] = 1,
            ["ac"] = true,
            ["ss"] = 0,
            ["ps"] = 0,
            ["op"] = 0,
        });
        project = await ReplaceProjectCountersAsync(project["_id"].AsObjectId, fs: 2, fc: 1);

        var revision = Image(project, "del.png", root: true);
        var revisionId = ObjectId.GenerateNewId();
        revision["_id"] = revisionId;
        revision["ac"] = false;
        revision["sa"] = revisionId.ToString() + ".png";
        revision["ov"] = file["_id"].AsObjectId;
        revision["v"] = 2;
        await PutAsync(storage, FileService.FilePrefix + revision.Text("sa"));
        await InsertFileAsync(revision);

        await files.DeleteAsync(file, project, CancellationToken.None);

        Assert.Null(await storage.HeadAsync(FileService.FilePrefix + saveName));
        Assert.Null(await storage.HeadAsync(FileService.FilePrefix + "cover-659999999999999999999999.webp"));
        Assert.Null(await storage.HeadAsync(FileService.FilePrefix + "resample-659999999999999999999999.webp"));
        Assert.NotNull(await storage.HeadAsync("proofread/long-lived-proofread.webp"));
        Assert.Equal(0, await services.Store.Collection("file").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("_id", file["_id"].AsObjectId)));
        Assert.Equal(0, await services.Store.Collection("file").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("_id", revision["_id"].AsObjectId)));

        var after = await LoadProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(0L, after["fs"].ToInt64());
    }

    [Fact]
    public async Task Delete_FolderCascadesTargetCachesSourcesTranslationsAndTips()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var folder = await InsertFileAsync(Folder(project, "001folder", root: true));
        var child = await InsertFileAsync(Image(project, "a.png", parent: folder));

        var sourceId = ObjectId.GenerateNewId();
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = sourceId,
            ["f"] = child["_id"].AsObjectId,
            ["r"] = 0,
            ["c"] = "hello",
        });
        await services.Store.Collection("translation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = ObjectId.GenerateNewId(),
            ["c"] = "你好",
        });
        await services.Store.Collection("tip").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = ObjectId.GenerateNewId(),
            ["c"] = "tip",
        });
        await services.Store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = child["_id"].AsObjectId,
            ["t"] = ObjectId.GenerateNewId(),
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = new BsonDateTime(BaseTime.UtcDateTime),
        });

        await files.DeleteAsync(folder, project, CancellationToken.None);

        Assert.Equal(0, await services.Store.Collection("source").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("_id", sourceId)));
        Assert.Equal(0, await services.Store.Collection("translation").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("o", sourceId)));
        Assert.Equal(0, await services.Store.Collection("tip").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("o", sourceId)));
        Assert.Equal(0, await services.Store.Collection("file_target_cache").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("f", child["_id"].AsObjectId)));
        Assert.Equal(0, await services.Store.Collection("file").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("p", project["_id"].AsObjectId)));
    }

    [Fact]
    public async Task Order_RequiresProvenanceAndSavesRejectsStaleDuplicateResetAndNoOp()
    {
        var files = Service(new TestClock(BaseTime));
        var actor = await InsertUserAsync();
        var project = await InsertProjectAsync();
        await InsertMemberAsync(project, actor, ["creator"]);

        var a = await InsertFileAsync(Image(project, "a.png", root: true));
        var b = await InsertFileAsync(Image(project, "b.png", root: true));
        var c = await InsertFileAsync(Image(project, "c.png", root: true));
        await InsertFileAsync(Folder(project, "folder", root: true));

        var view = await files.OrderAsync(actor, project, CancellationToken.None);
        Assert.Equal(3, view.Files.Count);
        Assert.Equal(64, view.Version.Length);
        Assert.Equal(new[] { a["_id"].AsObjectId.ToString(), b["_id"].AsObjectId.ToString(), c["_id"].AsObjectId.ToString() },
            view.DefaultFileIds);

        var reversed = new[] { c, b, a }
            .Select(file => file["_id"].AsObjectId.ToString())
            .ToList();
        await files.SaveOrderAsync(actor, project, reversed, view.Version, false, CancellationToken.None);
        Assert.Equal(1, (await LoadFileAsync(c["_id"].AsObjectId)).Integer("mo"));
        Assert.Equal(2, (await LoadFileAsync(b["_id"].AsObjectId)).Integer("mo"));
        Assert.Equal(3, (await LoadFileAsync(a["_id"].AsObjectId)).Integer("mo"));
        Assert.False((await LoadProjectAsync(project["_id"].AsObjectId)).Contains("fol"));

        var stale = await Assert.ThrowsAsync<ApiError>(() => files.SaveOrderAsync(
            actor, project, reversed, view.Version, false, CancellationToken.None));
        Assert.Equal("文件列表或顺序已变化，请取消编辑后重新加载", stale.Message);

        var current = await files.OrderAsync(actor, project, CancellationToken.None);
        var duplicate = await Assert.ThrowsAsync<ApiError>(() => files.SaveOrderAsync(
            actor, project, [reversed[0], reversed[0], reversed[2]], current.Version, false, CancellationToken.None));
        Assert.Equal("必须提供当前目录全部图片，且不可重复", duplicate.Message);

        await files.SaveOrderAsync(actor, project, current.Version == current.Version
            ? current.Files.Select(item => (string)item["id"]!).ToList()
            : reversed, current.Version, true, CancellationToken.None);
        Assert.True(NoManualOrder(await LoadFileAsync(a["_id"].AsObjectId)));

        // Saving the existing default order must be a no-op and must not enable mo.
        var freshProject = await InsertProjectAsync();
        var freshActor = await InsertUserAsync();
        await InsertMemberAsync(freshProject, freshActor, ["creator"]);
        var x = await InsertFileAsync(Image(freshProject, "x.png", root: true));
        var y = await InsertFileAsync(Image(freshProject, "y.png", root: true));
        var fresh = await files.OrderAsync(freshActor, freshProject, CancellationToken.None);
        await files.SaveOrderAsync(
            freshActor,
            freshProject,
            [x["_id"].AsObjectId.ToString(), y["_id"].AsObjectId.ToString()],
            fresh.Version,
            false,
            CancellationToken.None);
        Assert.True(NoManualOrder(await LoadFileAsync(x["_id"].AsObjectId)));
        Assert.True(NoManualOrder(await LoadFileAsync(y["_id"].AsObjectId)));
    }

    [Fact]
    public async Task Order_ConcurrentSaves_OnlyOneWins()
    {
        var files = Service(new TestClock(BaseTime));
        var actor = await InsertUserAsync();
        var project = await InsertProjectAsync();
        await InsertMemberAsync(project, actor, ["creator"]);
        var a = await InsertFileAsync(Image(project, "a.png", root: true));
        var b = await InsertFileAsync(Image(project, "b.png", root: true));
        var c = await InsertFileAsync(Image(project, "c.png", root: true));

        var view = await files.OrderAsync(actor, project, CancellationToken.None);
        // Both requested orders differ from the current default order. Python's
        // save_order (app/services/file_order.py) explicitly returns without
        // writing when file_ids equals the current order, so a two-file
        // "original vs reversed" pair is not a deterministic conflict: the
        // no-op can win the lease and both requests succeed. Using two
        // effectful permutations makes exactly one of them commit the version
        // CAS and the other observe the changed snapshot.
        var firstOrder = new[]
        {
            b["_id"].AsObjectId.ToString(), a["_id"].AsObjectId.ToString(),
            c["_id"].AsObjectId.ToString()
        };
        var secondOrder = new[]
        {
            c["_id"].AsObjectId.ToString(), b["_id"].AsObjectId.ToString(),
            a["_id"].AsObjectId.ToString()
        };

        var first = files.SaveOrderAsync(actor, project, firstOrder, view.Version, false, CancellationToken.None);
        var second = files.SaveOrderAsync(actor, project, secondOrder, view.Version, false, CancellationToken.None);
        var results = await Task.WhenAll(CatchAsync(first), CatchAsync(second));

        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is ApiError);
        var stored = await services.Store.Collection("file")
            .Find(Builders<BsonDocument>.Filter.Eq("p", project["_id"].AsObjectId)
                  & Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
                  & Builders<BsonDocument>.Filter.Eq("t", FileService.TypeImage)
                  & Builders<BsonDocument>.Filter.Eq("ac", true))
            .Sort(Builders<BsonDocument>.Sort.Ascending("mo"))
            .ToListAsync();
        var persisted = stored.Select(file => file["_id"].AsObjectId.ToString()).ToArray();
        Assert.True(
            persisted.SequenceEqual(firstOrder) || persisted.SequenceEqual(secondOrder),
            "the committed order must be exactly one of the two requested orders");
        Assert.False((await LoadProjectAsync(project["_id"].AsObjectId)).Contains("fol"));
    }

    [Fact]
    public async Task Order_PlainAccessWithoutProvenance_CannotOrder()
    {
        var files = Service(new TestClock(BaseTime));
        var actor = await InsertUserAsync();
        var project = await InsertProjectAsync();
        await InsertMemberAsync(project, actor, []);

        Assert.False(await files.CanOrderAsync(actor, project, CancellationToken.None));
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            files.OrderAsync(actor, project, CancellationToken.None));
        Assert.Equal(103, error.Code);
    }

    [Fact]
    public async Task Neighbors_NoManualThenManualOrdering()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var a = await InsertFileAsync(Image(project, "001a.png", root: true));
        var b = await InsertFileAsync(Image(project, "002b.png", root: true));
        var c = await InsertFileAsync(Image(project, "003c.png", root: true));

        var (prev, next) = await files.NeighborsAsync(b, CancellationToken.None);
        Assert.Equal(a["_id"].AsObjectId, prev!["_id"].AsObjectId);
        Assert.Equal(c["_id"].AsObjectId, next!["_id"].AsObjectId);

        a["mo"] = 3;
        b["mo"] = 1;
        c["mo"] = 2;
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", a["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("mo", 3));
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", b["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("mo", 1));
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", c["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("mo", 2));

        (prev, next) = await files.NeighborsAsync(c, CancellationToken.None);
        Assert.Equal(b["_id"].AsObjectId, prev!["_id"].AsObjectId);
        Assert.Equal(a["_id"].AsObjectId, next!["_id"].AsObjectId);
    }

    [Fact]
    public async Task List_FinishedProjectRejected()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync(status: 1);
        var error = await Assert.ThrowsAsync<ApiError>(() => files.ListAsync(
            project,
            new FileListQuery(null, false, false, [], null, null),
            new Pagination(1, 20),
            CancellationToken.None));
        Assert.Equal(4002, error.Code);
    }

    [Fact]
    public async Task AdminList_SortsByEditTimeAndCountsAll()
    {
        var files = Service(new TestClock(BaseTime));
        // Admin listing is global and Python raises 4016 for any inactive
        // revision, so clear leftovers from previous fixtures first.
        await services.Store.Collection("file").DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("ac", false));
        var baseline = await services.Store.Collection("file").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Empty);
        var project = await InsertProjectAsync();
        var old = await InsertFileAsync(Image(project, "old.png", root: true));
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", old["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("et", new BsonDateTime(BaseTime.AddHours(-2).UtcDateTime)));
        var recent = await InsertFileAsync(Image(project, "recent.png", root: true));
        await services.Store.Collection("file").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", recent["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("et", new BsonDateTime(BaseTime.AddHours(1).UtcDateTime)));

        var (items, count) = await files.AdminListAsync(new Pagination(1, 20), CancellationToken.None);
        Assert.Equal(baseline + 2, count);
        Assert.Equal(recent["_id"].AsObjectId.ToString(), items[0]["id"]);
    }

    [Fact]
    public async Task AdminList_InactiveRevisionReturns4016()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var revision = Image(project, "inactive.png", root: true);
        revision["ac"] = false;
        revision["et"] = new BsonDateTime(BaseTime.AddHours(3).UtcDateTime);
        await InsertFileAsync(revision);

        try
        {
            var error = await CatchAsync(files.AdminListAsync(new Pagination(1, 20), CancellationToken.None));
            Assert.NotNull(error);
            Assert.Equal(4016, error!.Code);
        }
        finally
        {
            await services.Store.Collection("file").DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", revision["_id"].AsObjectId));
        }
    }

    [Fact]
    public async Task Initialize_CreatesLegacyNamedIndexesWithoutUniqueNameIndex()
    {
        var files = Service(new TestClock(BaseTime));
        await files.InitializeAsync(CancellationToken.None);
        using var indexes = await services.Store.Collection("file").Indexes.ListAsync();
        var names = (await indexes.ToListAsync()).Select(index => index["name"].AsString).ToList();
        Assert.Contains("file_manual_order_v1", names);
        Assert.Contains("file_admin_edit_time_v1", names);
        Assert.Contains("file_list_v2", names);
        Assert.DoesNotContain(names, name => name.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetFile_MissingOrMalformedId_Returns8001()
    {
        var files = Service(new TestClock(BaseTime));
        var malformed = await Assert.ThrowsAsync<ApiError>(() =>
            files.GetFileAsync("not-an-object-id", CancellationToken.None));
        Assert.Equal(8001, malformed.Code);
        var missing = await Assert.ThrowsAsync<ApiError>(() =>
            files.GetFileAsync(ObjectId.GenerateNewId().ToString(), CancellationToken.None));
        Assert.Equal(8001, missing.Code);
    }

    [Fact]
    public async Task Upload_MetadataPersistedThenCacheFailure_KeepsReferencedObject()
    {
        var clock = new TestClock(BaseTime);
        var files = Service(clock);
        var project = await InsertProjectAsync();
        var tempFilesBefore = Directory.Exists(Path.Combine(Path.GetTempPath(), "opencode"))
            ? Directory.GetFiles(Path.Combine(Path.GetTempPath(), "opencode"), "moeflow-*").Length
            : 0;

        files.AfterMetadataWriteForTests = () => throw new InvalidOperationException("injected cache failure");
        var upload = Form("durable.png", [1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            files.UploadAsync(project, "durable.png", upload, null, CancellationToken.None));

        var stored = await services.Store.Collection("file")
            .Find(Builders<BsonDocument>.Filter.Eq("p", project["_id"].AsObjectId)
                  & Builders<BsonDocument>.Filter.Eq("n", "durable.png"))
            .FirstAsync();
        var saveName = stored.Text("sa");
        Assert.False(string.IsNullOrEmpty(saveName));
        Assert.NotNull(await Storage().HeadAsync(FileService.FilePrefix + saveName));
        await Storage().DeleteAsync(FileService.FilePrefix + saveName, CancellationToken.None);

        var tempFilesAfter = Directory.Exists(Path.Combine(Path.GetTempPath(), "opencode"))
            ? Directory.GetFiles(Path.Combine(Path.GetTempPath(), "opencode"), "moeflow-*").Length
            : 0;
        Assert.Equal(tempFilesBefore, tempFilesAfter);
    }

    [Fact]
    public async Task CreateFolder_UsesLabelEpochAndFolderCounters()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var target = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = project["_id"].AsObjectId,
            ["l"] = "zh",
            ["tsc"] = 0,
            ["csc"] = 0,
        };
        await services.Store.Collection("target").InsertOneAsync(target);

        var folder = await files.CreateFolderAsync(project, "001folder", null, CancellationToken.None);
        Assert.Equal((long)FileService.TypeFolder, (long)folder["type"]!);
        Assert.Null(folder["manual_order"]);
        Assert.Null(folder["parent_id"]);

        var stored = await LoadFileAsync(ObjectId.Parse((string)folder["id"]!));
        Assert.Equal(0L, stored.GetValue("label_epoch", 0).ToInt64());
        Assert.Equal(1, (await LoadProjectAsync(project["_id"].AsObjectId)).Integer("fo"));
        Assert.Equal(1, await services.Store.Collection("file_target_cache").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("f", stored["_id"].AsObjectId)));

        var child = await files.CreateFolderAsync(project, "sub", (string)folder["id"]!, CancellationToken.None);
        var childStored = await LoadFileAsync(ObjectId.Parse((string)child["id"]!));
        Assert.Equal("000001folder/", childStored.Text("dn"));
        Assert.Equal(2, (await LoadProjectAsync(project["_id"].AsObjectId)).Integer("fo"));
    }

    [Fact]
    public async Task Delete_ManySources_CascadesInBoundedBatches()
    {
        var files = Service(new TestClock(BaseTime));
        var project = await InsertProjectAsync();
        var file = await InsertFileAsync(Image(project, "many.png", root: true));

        var sources = new List<BsonDocument>();
        var translations = new List<BsonDocument>();
        var tips = new List<BsonDocument>();
        for (var index = 0; index < 205; index++)
        {
            var sourceId = ObjectId.GenerateNewId();
            sources.Add(new BsonDocument
            {
                ["_id"] = sourceId,
                ["f"] = file["_id"].AsObjectId,
                ["r"] = index,
                ["c"] = "source",
            });
            translations.Add(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = sourceId,
                ["t"] = ObjectId.GenerateNewId(),
                ["c"] = "translation",
            });
            tips.Add(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = sourceId,
                ["t"] = ObjectId.GenerateNewId(),
                ["c"] = "tip",
            });
        }

        await services.Store.Collection("source").InsertManyAsync(sources);
        await services.Store.Collection("translation").InsertManyAsync(translations);
        await services.Store.Collection("tip").InsertManyAsync(tips);

        await files.DeleteAsync(file, project, CancellationToken.None);

        Assert.Equal(0, await services.Store.Collection("source").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("f", file["_id"].AsObjectId)));
        Assert.Equal(0, await services.Store.Collection("translation").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.In("_id", translations.Select(row => row["_id"].AsObjectId))));
        Assert.Equal(0, await services.Store.Collection("tip").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.In("_id", tips.Select(row => row["_id"].AsObjectId))));
    }

    [Fact]
    public async Task Mutation_ReusesMiddlewareLabelSessionWithoutDeadlock()
    {
        var clock = new TestClock(BaseTime);
        var labels = new ProjectLabelLock(services.Store, clock);
        var context = new LabelRequestContext();
        var files = new FileService(
            services.Store,
            services.App.Services.GetRequiredService<ProjectService>(),
            services.App.Services.GetRequiredService<IdentityPermissions>(),
            Storage(),
            services.Configuration,
            clock,
            new FileMutationGate(),
            NullLogger<FileService>.Instance,
            labels,
            context);
        var project = await InsertProjectAsync();

        await using var middlewareSession = await labels.EnterAsync(
            context, project["_id"].AsObjectId, ct: CancellationToken.None);
        var data = await files.UploadAsync(
            project, "borrowed.png", Form("borrowed.png", [9, 8, 7]), null, CancellationToken.None);

        Assert.Equal("borrowed.png", data["name"]);
        Assert.Equal(1, (await LoadProjectAsync(project["_id"].AsObjectId)).Integer("fc"));
    }

    // ---------------------------------------------------------------- fixtures

    private FileService Service(TestClock clock) => new(
        services.Store,
        services.App.Services.GetRequiredService<ProjectService>(),
        services.App.Services.GetRequiredService<IdentityPermissions>(),
        Storage(),
        services.Configuration,
        clock,
        new FileMutationGate(),
        NullLogger<FileService>.Instance);

    private ObjectStorage Storage() => services.App.Services.GetRequiredService<ObjectStorage>();

    private async Task<BsonDocument> InsertProjectAsync(int status = 0, ObjectId? storageSourceId = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "project",
            ["ns"] = "project",
            ["t"] = ObjectId.GenerateNewId(),
            ["st"] = status,
            ["stv"] = 0,
            ["fo"] = 0,
            ["fc"] = 0,
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["m_e"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["dr"] = 1,
            ["m_a"] = 1,
            ["m_t"] = 1,
        };
        if (storageSourceId.HasValue)
        {
            document["csid"] = storageSourceId.Value;
        }

        await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertFileAsync(BsonDocument document)
    {
        if (!document.Contains("_id"))
        {
            document["_id"] = ObjectId.GenerateNewId();
        }

        if (!document.Contains("et"))
        {
            document["et"] = new BsonDateTime(BaseTime.UtcDateTime);
        }

        if (!document.Contains("v"))
        {
            document["v"] = 1;
        }

        if (!document.Contains("ac"))
        {
            document["ac"] = true;
        }

        await services.Store.Collection("file").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> ReplaceFileAsync(ObjectId id, BsonDocument document)
    {
        await services.Store.Collection("file").ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id), document);
        return document;
    }

    private async Task<BsonDocument> ReplaceProjectCountersAsync(ObjectId id, long fs, int fc)
    {
        await services.Store.Collection("project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("fs", new BsonInt64(fs)).Set("fc", fc));
        return await LoadProjectAsync(id);
    }

    private async Task<BsonDocument> LoadProjectAsync(ObjectId id)
        => await services.Store.Collection("project")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstAsync();

    private async Task<BsonDocument> LoadFileAsync(ObjectId id)
        => await services.Store.Collection("file")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstAsync();

    private async Task<BsonDocument> InsertUserAsync()
    {
        var user = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["e"] = $"user-{Guid.NewGuid():N}@example.test",
            ["n"] = $"user-{Guid.NewGuid():N}",
            ["password"] = "hash",
            ["b"] = false,
            ["admin"] = false,
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task InsertMemberAsync(BsonDocument project, BsonDocument user, string[] tags)
    {
        var now = new BsonDateTime(BaseTime.UtcDateTime);
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"].AsObjectId,
            ["user"] = user["_id"].AsObjectId,
            ["ik"] = "user:" + user["_id"].AsObjectId,
            ["display_name"] = "user",
            ["dns"] = "user",
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag)),
            ["status"] = "active",
            ["create_time"] = now,
            ["edit_time"] = now,
            ["version"] = 1,
        });
    }

    private static BsonDocument Folder(BsonDocument project, string name, bool root = false, BsonDocument? parent = null)
    {
        var id = ObjectId.GenerateNewId();
        var dir = parent is null ? "" : parent.Text("dn") + parent.Text("sn") + "/";
        return new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["t"] = FileService.TypeFolder,
            ["p"] = project["_id"].AsObjectId,
            ["f"] = parent is null ? BsonNull.Value : parent["_id"].AsObjectId,
            ["a"] = parent is null
                ? new BsonArray()
                : new BsonArray(parent["a"].AsBsonArray.Append(parent["_id"].AsObjectId)),
            ["mo"] = BsonNull.Value,
            ["sn"] = TestSort(name),
            ["dn"] = dir,
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fn"] = 0,
            ["th"] = 0,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ss"] = 0,
            ["ps"] = 0,
            ["op"] = 0,
        };
    }

    private static BsonDocument Image(BsonDocument project, string name, bool root = false, BsonDocument? parent = null)
    {
        var document = Folder(project, name, root, parent);
        document["t"] = FileService.TypeImage;
        document["sn"] = TestSort(name);
        return document;
    }

    private static string TestSort(string name)
    {
        var dot = name.LastIndexOf('.');
        var prefix = dot >= 0 ? name[..dot] : name;
        return string.Concat(System.Text.RegularExpressions.Regex.Matches(prefix, @"\d+|\D+")
            .Select(match => match.Value.All(char.IsDigit) && match.Value.Length < 6
                ? new string('0', 6 - match.Value.Length) + match.Value
                : match.Value));
    }

    private static IFormFile Form(string name, byte[] bytes)
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };

    private async Task PutAsync(ObjectStorage storage, string key)
    {
        await using var stream = new MemoryStream([1, 2, 3]);
        await storage.PutAsync(key, stream, "application/octet-stream", CancellationToken.None);
    }

    private static bool NoManualOrder(BsonDocument file)
        => !file.Contains("mo") || file["mo"].IsBsonNull;

    private static string Error(Action action)
    {
        var error = Assert.Throws<ApiError>(action);
        return error.Message;
    }

    private static int ErrorCode(Action action) => Assert.Throws<ApiError>(action).Code;

    private static async Task<ApiError?> CatchAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (ApiError error)
        {
            return error;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
