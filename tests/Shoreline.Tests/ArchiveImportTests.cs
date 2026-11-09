using System.IO.Compression;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.ArchiveImport;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class ArchiveImportTests(LocalServices services)
{
    private static readonly byte[] A = Encoding.UTF8.GetBytes("A-image");
    private static readonly byte[] B = Encoding.UTF8.GetBytes("B-image");
    private static readonly byte[] C = Encoding.UTF8.GetBytes("C-image");

    [Fact]
    public void StatusNamesMatchLegacyContract()
    {
        Assert.Equal("排队中", ArchiveImportText.Name(0));
        Assert.Equal("解析归档链接", ArchiveImportText.Name(1));
        Assert.Equal("下载归档中", ArchiveImportText.Name(2));
        Assert.Equal("校验归档中", ArchiveImportText.Name(3));
        Assert.Equal("导入图片中", ArchiveImportText.Name(4));
        Assert.Equal("已完成", ArchiveImportText.Name(5));
        Assert.Equal("导入失败", ArchiveImportText.Name(6));
        Assert.True(ArchiveImportStatus.IsRunning(0));
        Assert.True(ArchiveImportStatus.IsRunning(4));
        Assert.False(ArchiveImportStatus.IsRunning(5));
    }

    [Fact]
    public async Task CreatePersistsTaskAndEnqueuesLegacyPayload()
    {
        await using var harness = await Harness.CreateAsync(services);
        var task = await harness.CreateTaskAsync();

        Assert.Equal(0, (int)task.Integer("s"));
        Assert.Equal("", task.Text("st"));
        Assert.Equal("12345", task.Text("g"));
        Assert.Equal("synthetic-token", task.Text("t"));
        Assert.Equal("", task.Text("gu"));
        Assert.False(task.Flag("d"));
        Assert.Equal(0, task.Integer("n"));
        Assert.Equal(0, task.Integer("c"));
        Assert.Equal("", task.Text("zu"));
        Assert.Equal(harness.ProjectId, task["p"].AsObjectId);

        var payload = Assert.Single(harness.Queue.Payloads);
        Assert.Equal(ArchiveImportService.JobKind, harness.Queue.Kinds[0]);
        Assert.Equal(harness.ProjectId.ToString(), payload.Text("project"));
        Assert.Equal(task["_id"].AsObjectId.ToString(), payload.Text("taskid"));
        Assert.Equal(0, payload.Integer("label_revision"));
    }

    [Fact]
    public async Task CreateRejectsSecondActiveTask()
    {
        await using var harness = await Harness.CreateAsync(services);
        await harness.CreateTaskAsync();

        var error = await Assert.ThrowsAsync<ApiError>(() => harness.Service.CreateAsync(
            harness.Project, null, "222", "token", "", CancellationToken.None));
        Assert.Equal("ValidateError", error.Error);
        Assert.Equal("该项目已有归档导入任务进行中", error.Message);
        Assert.Equal(1, (int)await harness.Store.Collection("archive_import_task")
            .CountDocumentsAsync(new BsonDocument("p", harness.ProjectId)));
    }

    [Fact]
    public async Task CreateRejectsLegacyRunningTaskWithoutClaim()
    {
        await using var harness = await Harness.CreateAsync(services);
        var legacy = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["p"] = harness.ProjectId, ["g"] = "1", ["t"] = "x",
            ["s"] = 4, ["st"] = "导入图片", ["n"] = 1, ["c"] = 0, ["ct"] = harness.Clock.GetUtcNow().UtcDateTime,
            ["ut"] = harness.Clock.GetUtcNow().UtcDateTime,
        };
        await harness.Store.Collection("archive_import_task").InsertOneAsync(legacy);

        var error = await Assert.ThrowsAsync<ApiError>(() => harness.Service.CreateAsync(
            harness.Project, null, "222", "token", "", CancellationToken.None));
        Assert.Equal("该项目已有归档导入任务进行中", error.Message);
    }

    [Fact]
    public async Task CreateRecoversStaleRunningClaimAndRetiresOldRow()
    {
        await using var harness = await Harness.CreateAsync(services);
        var oldId = ObjectId.GenerateNewId();
        var oldTime = harness.Clock.GetUtcNow().UtcDateTime.AddHours(-2);
        await harness.Store.Collection("archive_import_task").InsertOneAsync(new BsonDocument
        {
            ["_id"] = oldId, ["p"] = harness.ProjectId, ["g"] = "1", ["t"] = "x", ["s"] = 4,
            ["st"] = "导入图片", ["n"] = 1, ["c"] = 0, ["ct"] = oldTime, ["ut"] = oldTime, ["lo"] = "dead",
        });
        await harness.Claims.TryInsertAsync(harness.ProjectId, oldId, CancellationToken.None);
        await harness.Claims.TryTakeAsync(harness.ProjectId, oldId, "dead",
            harness.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1), harness.Clock.GetUtcNow().UtcDateTime,
            CancellationToken.None);

        var task = await harness.CreateTaskAsync();
        Assert.NotEqual(oldId, task["_id"].AsObjectId);

        var old = await harness.Store.Collection("archive_import_task")
            .Find(new BsonDocument("_id", oldId)).FirstAsync();
        Assert.Equal(6, (int)old.Integer("s"));
        Assert.Equal("任务已中断", old.Text("e"));
    }

    [Fact]
    public async Task CreateQueueFailureMarksTaskFailedAndReleasesClaim()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Queue.Error = new InvalidOperationException("queue unavailable");

        var error = await Assert.ThrowsAsync<ApiError>(() => harness.CreateTaskAsync());
        Assert.Equal("ArchiveImportQueueError", error.Error);
        Assert.Equal(500, error.Status);

        var task = await harness.Tasks.LatestAsync(harness.ProjectId, CancellationToken.None);
        Assert.NotNull(task);
        Assert.Equal(6, (int)task!.Integer("s"));
        Assert.Equal("任务入队失败", task.Text("e"));
        Assert.Null(await harness.Claims.GetAsync(harness.ProjectId, CancellationToken.None));
    }

    [Fact]
    public async Task RunImportsImagesInArchiveOrderAndSkipsNonImages()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(
            ("images/a.jpg", A),
            ("images/sub/b.PNG", B),
            ("notes.txt", Encoding.UTF8.GetBytes("not an image")),
            ("images/.hidden.jpg", C),
            ("images/", []),
            ("images/../../escape.jpg", C));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(5, (int)task.Integer("s"));
        Assert.Equal("已完成", ArchiveImportText.Name((int)task.Integer("s")));
        Assert.Equal("导入完成（3 张）", task.Text("st"));
        Assert.Equal(3, (int)task.Integer("n"));
        Assert.Equal(3, (int)task.Integer("c"));
        Assert.Equal("", task.Text("e"));
        Assert.Equal(["a.jpg", "b.PNG", "escape.jpg"], harness.Uploader.Names);
        Assert.Empty(harness.TempDirectories());
    }

    [Fact]
    public async Task RunKeepsDuplicateNamesInOrderAndLastOverwrites()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("images/same.jpg", A), ("images/same.jpg", B));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        Assert.Equal(["same.jpg", "same.jpg"], harness.Uploader.Names);
        Assert.Equal(A, harness.Uploader.Contents[0]);
        Assert.Equal(B, harness.Uploader.Contents[1]);
        var task = await harness.LatestAsync();
        Assert.Equal(5, (int)task.Integer("s"));
        Assert.Equal(2, (int)task.Integer("c"));
    }

    [Fact]
    public async Task RunAcceptsUnicodeAndFolderBasenames()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("图片/猫.jpg", A), ("images/深/层.png", B));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        Assert.Equal(["猫.jpg", "层.png"], harness.Uploader.Names);
        Assert.Equal(5, (int)(await harness.LatestAsync()).Integer("s"));
    }

    [Fact]
    public async Task RunFailsWhenArchiveHasNoImages()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("readme.txt", A), ("images/.hidden.png", B));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("归档内没有可导入的图片", task.Text("e"));
        Assert.Empty(harness.Uploader.Names);
    }

    [Fact]
    public async Task RunRejectsCorruptCrc()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Corrupt(Zip(("images/a.jpg", A)), A);
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("归档不是有效的 zip 或已损坏", task.Text("e"));
        Assert.Empty(harness.Uploader.Names);
    }

    [Fact]
    public async Task RunEnforcesDeclaredEntryAndTotalSizeCaps()
    {
        await using var harness = await Harness.CreateAsync(services,
            configure: options => options.MaxEntryBytes = 50);
        harness.Transport.Zip = Zip(("images/big.jpg", new byte[100]));
        await harness.CreateTaskAsync();
        await harness.RunAsync();
        Assert.Contains("单条目大小超过上限", (await harness.LatestAsync()).Text("e"));

        await using var total = await Harness.CreateAsync(services,
            configure: options => options.MaxUncompressedBytes = 100);
        total.Transport.Zip = Zip(("images/a.jpg", new byte[60]), ("images/b.jpg", new byte[60]));
        await total.CreateTaskAsync();
        await total.RunAsync();
        Assert.Contains("解压后大小超过上限", (await total.LatestAsync()).Text("e"));
    }

    [Fact]
    public async Task RunEnforcesEntryCountCap()
    {
        await using var harness = await Harness.CreateAsync(services,
            configure: options => options.MaxEntries = 1);
        harness.Transport.Zip = Zip(("images/a.jpg", A), ("images/b.jpg", B));
        await harness.CreateTaskAsync();
        await harness.RunAsync();

        Assert.Contains("条目数量超过上限", (await harness.LatestAsync()).Text("e"));
    }

    [Fact]
    public async Task RunSkipsSingleBadImageAndContinues()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("images/a.jpg", A), ("images/b.jpg", B));
        harness.Uploader.Error = (index, _) => index == 0 ? ApiError.Invalid("bad image") : null;
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(5, (int)task.Integer("s"));
        Assert.Equal("导入完成（1 张）", task.Text("st"));
        Assert.Equal(2, (int)task.Integer("c"));
        Assert.Equal(["b.jpg"], harness.Uploader.Names);
    }

    [Fact]
    public async Task RunFailsOnFatalUploaderError()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("images/a.jpg", A));
        harness.Uploader.Error = (_, _) => new ApiError("StoragePoolUnsupported", 2, "storage pool", 409);
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("导入失败", task.Text("st"));
    }

    [Fact]
    public async Task RunTriesEveryKeyAndSucceedsOnLaterKey()
    {
        await using var harness = await Harness.CreateAsync(services, keys: ["key-one", "key-two"]);
        harness.Transport.Resolver = (call, gid, _, _) =>
            call == 0 ? null : Canonical(gid);
        harness.Transport.Zip = Zip(("images/a.jpg", A));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        Assert.Equal(["key-one", "key-two"], harness.Transport.ResolveCalls.Select(call => call.Key));
        Assert.Equal(5, (int)(await harness.LatestAsync()).Integer("s"));
    }

    [Fact]
    public async Task RunFailsWhenAllKeysFailAndNeverLeaksThem()
    {
        await using var harness = await Harness.CreateAsync(services, keys: ["key-one", "key-two"]);
        harness.Transport.Resolver = (_, _, _, _) => null;
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("解析归档链接失败", task.Text("st"));
        Assert.Equal("无法从档案 API 获取归档链接（所有 key 均已尝试）", task.Text("e"));
        Assert.DoesNotContain("key-one", task.Text("e"));
        Assert.DoesNotContain("key-two", task.Text("e"));
        Assert.Empty(harness.Transport.DownloadUrls);
    }

    [Fact]
    public async Task RunSkipsDisabledAndUndecryptableKeys()
    {
        await using var harness = await Harness.CreateAsync(services, keys: ["good-key"]);
        var team = await harness.Store.Collection("team").Find(new BsonDocument("_id", harness.TeamId)).FirstAsync();
        team["aak"] = new BsonArray
        {
            new BsonDocument { ["id"] = "1", ["enabled"] = false, ["key"] = harness.Secrets.Encrypt("disabled-key") },
            new BsonDocument { ["id"] = "2", ["enabled"] = true, ["key"] = "fernet:not-a-real-token" },
            new BsonDocument { ["id"] = "3", ["enabled"] = true, ["key"] = harness.Secrets.Encrypt("good-key") },
        };
        await harness.Store.Collection("team").ReplaceOneAsync(new BsonDocument("_id", harness.TeamId), team);
        harness.Transport.Zip = Zip(("images/a.jpg", A));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        Assert.Equal(["good-key"], harness.Transport.ResolveCalls.Select(call => call.Key));
        Assert.Equal(5, (int)(await harness.LatestAsync()).Integer("s"));
    }

    [Fact]
    public async Task RunFailsWhenNoTeamKeysConfigured()
    {
        await using var harness = await Harness.CreateAsync(services, keys: []);
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("团队未配置档案 API key，请联系管理员", task.Text("e"));
    }

    [Fact]
    public async Task RunFailsWhenProviderUrlMissing()
    {
        await using var harness = await Harness.CreateAsync(services,
            configure: options => options.ProviderApiUrl = "");
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal("系统未配置档案 API 地址", task.Text("e"));
    }

    [Fact]
    public async Task RunRejectsTeamUrlOutsideAllowlist()
    {
        await using var harness = await Harness.CreateAsync(services);
        await harness.Store.Collection("team").UpdateOneAsync(new BsonDocument("_id", harness.TeamId),
            new BsonDocument("$set", new BsonDocument("aau", "https://evil.example.test")));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("档案 API 地址无效", task.Text("st"));
        Assert.Contains("白名单", task.Text("e"));
    }

    [Fact]
    public async Task RunRejectsPrivateProviderUrl()
    {
        await using var harness = await Harness.CreateAsync(services);
        await harness.Store.Collection("team").UpdateOneAsync(new BsonDocument("_id", harness.TeamId),
            new BsonDocument("$set", new BsonDocument("aau", "https://127.0.0.1")));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal("档案 API 地址无效", task.Text("st"));
        Assert.Contains("私有", task.Text("e"));
    }

    [Fact]
    public async Task RunFailsWhenProjectFinishedOrMissingOrEpochChanged()
    {
        await using var finished = await Harness.CreateAsync(services);
        finished.Transport.Zip = Zip(("images/a.jpg", A));
        await finished.CreateTaskAsync();
        await finished.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", finished.ProjectId), new BsonDocument("$set", new BsonDocument("st", 1)));
        await finished.RunAsync();
        Assert.Equal("项目已完结", (await finished.LatestAsync()).Text("e"));

        await using var missing = await Harness.CreateAsync(services);
        await missing.CreateTaskAsync();
        await missing.Store.Collection("project").DeleteOneAsync(new BsonDocument("_id", missing.ProjectId));
        await missing.RunAsync();
        var missingTask = await missing.LatestAsync();
        Assert.Equal("项目不存在", missingTask.Text("st"));
        Assert.Equal("项目不存在", missingTask.Text("e"));

        await using var epoch = await Harness.CreateAsync(services);
        await epoch.CreateTaskAsync();
        await epoch.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", epoch.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_revision", 9L)));
        await epoch.RunAsync();
        Assert.Equal("项目标签版本已变更，导入已取消", (await epoch.LatestAsync()).Text("e"));
    }

    [Fact]
    public async Task RunReportsDownloadLimitFailure()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Error = new ArchiveDownloadLimitException(123);
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(6, (int)task.Integer("s"));
        Assert.Equal("下载归档", task.Text("st"));
        Assert.Contains("123", task.Text("e"));
    }

    [Fact]
    public async Task RunResumesFromCheckpointAfterLostClaim()
    {
        await using var harness = await Harness.CreateAsync(services);
        harness.Transport.Zip = Zip(("images/a.jpg", A), ("images/b.jpg", B), ("images/c.jpg", C));
        harness.Uploader.Error = (index, _) => index == 2 ? new ArchiveLostException("lost") : null;
        await harness.CreateTaskAsync();

        await Assert.ThrowsAsync<ArchiveLostException>(harness.RunAsync);

        var checkpoint = await harness.LatestAsync();
        Assert.Equal(4, (int)checkpoint.Integer("s"));
        Assert.Equal(2, (int)checkpoint.Integer("pi"));
        Assert.Equal(2, (int)checkpoint.Integer("ic"));
        Assert.Equal(["a.jpg", "b.jpg"], harness.Uploader.Names);

        harness.Uploader.Error = null;
        await harness.RunAsync();

        var task = await harness.LatestAsync();
        Assert.Equal(5, (int)task.Integer("s"));
        Assert.Equal(3, (int)task.Integer("c"));
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], harness.Uploader.Names);
    }

    [Fact]
    public async Task DismissOnlyTouchesNonRunningRows()
    {
        await using var harness = await Harness.CreateAsync(services);
        var store = harness.Store.Collection("archive_import_task");
        var now = harness.Clock.GetUtcNow().UtcDateTime;
        var done = ObjectId.GenerateNewId();
        var running = ObjectId.GenerateNewId();
        var failed = ObjectId.GenerateNewId();
        await store.InsertManyAsync([
            new BsonDocument { ["_id"] = done, ["p"] = harness.ProjectId, ["s"] = 5, ["d"] = false, ["ct"] = now, ["ut"] = now },
            new BsonDocument { ["_id"] = running, ["p"] = harness.ProjectId, ["s"] = 4, ["d"] = false, ["ct"] = now, ["ut"] = now },
            new BsonDocument { ["_id"] = failed, ["p"] = harness.ProjectId, ["s"] = 6, ["d"] = false, ["ct"] = now, ["ut"] = now }
        ]);

        await harness.Service.DismissAsync(harness.ProjectId, CancellationToken.None);

        Assert.True((await store.Find(new BsonDocument("_id", done)).FirstAsync()).Flag("d"));
        Assert.False((await store.Find(new BsonDocument("_id", running)).FirstAsync()).Flag("d"));
        Assert.True((await store.Find(new BsonDocument("_id", failed)).FirstAsync()).Flag("d"));
        Assert.Equal(running, (await harness.LatestAsync())["_id"].AsObjectId);
    }

    [Fact]
    public async Task LatestReturnsDismissedTaskForFrontend()
    {
        await using var harness = await Harness.CreateAsync(services);
        var task = await harness.CreateTaskAsync();
        await harness.Store.Collection("archive_import_task").UpdateOneAsync(
            new BsonDocument("_id", task["_id"]), new BsonDocument("$set", new BsonDocument("d", true)));

        var latest = await harness.LatestAsync();
        Assert.NotNull(latest);
        Assert.True(latest!.Flag("d"));
        Assert.True(ArchiveImportView.ToApi(latest)["dismissed"] is true);
    }

    [Fact]
    public async Task ViewMatchesLegacyFieldsAndHidesZipUrlAndKeys()
    {
        await using var harness = await Harness.CreateAsync(services);
        var task = await harness.CreateTaskAsync();
        var view = ArchiveImportView.ToApi(task);

        Assert.Equal(["id", "project_id", "gid", "token", "gallery_url", "status", "status_name", "stage",
            "total_pages", "completed_pages", "error", "dismissed", "create_time", "update_time"], view.Keys);
        Assert.Equal("排队中", view["status_name"]);
        Assert.Equal("12345", view["gid"]);
        Assert.Equal("synthetic-token", view["token"]);
        Assert.Equal("", view["gallery_url"]);
        Assert.Equal(0, view["status"]);
        Assert.False(view.ContainsKey("zip_url"));
        Assert.False(view.ContainsKey("api_key"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$", (string)view["create_time"]!);
        Assert.Equal((string)view["create_time"]!, (string)view["update_time"]!);
    }

    [Fact]
    public async Task ClaimLeaseIsFencedAndTakenOverAfterExpiry()
    {
        await using var harness = await Harness.CreateAsync(services);
        var taskId = ObjectId.GenerateNewId();
        await harness.Store.Collection("archive_import_task").InsertOneAsync(new BsonDocument
        {
            ["_id"] = taskId, ["p"] = harness.ProjectId, ["s"] = 4, ["st"] = "导入图片",
            ["lo"] = "old", ["ut"] = harness.Clock.GetUtcNow().UtcDateTime,
        });
        await harness.Claims.TryInsertAsync(harness.ProjectId, taskId, CancellationToken.None);

        var now = harness.Clock.GetUtcNow().UtcDateTime;
        var oldLease = now.AddMinutes(2);
        Assert.True(await harness.Claims.TryTakeAsync(harness.ProjectId, taskId, "old", oldLease, now,
            CancellationToken.None));
        Assert.False(await harness.Claims.TryTakeAsync(harness.ProjectId, taskId, "new", oldLease, now,
            CancellationToken.None));

        harness.Clock.Current = harness.Clock.Current.AddMinutes(3);
        var newLease = harness.Clock.GetUtcNow().UtcDateTime.AddMinutes(2);
        Assert.True(await harness.Claims.TryTakeAsync(harness.ProjectId, taskId, "new", newLease,
            harness.Clock.GetUtcNow().UtcDateTime, CancellationToken.None));
        Assert.False(await harness.Claims.RenewAsync(harness.ProjectId, taskId, "old", newLease,
            CancellationToken.None));
        Assert.True(await harness.Claims.RenewAsync(harness.ProjectId, taskId, "new", newLease,
            CancellationToken.None));

        await harness.Store.Collection("archive_import_task").UpdateOneAsync(
            new BsonDocument("_id", taskId), new BsonDocument("$set", new BsonDocument("lo", "new")));
        var oldOwner = new ArchiveClaimOwner(harness.ProjectId, taskId, "old", oldLease);
        var newOwner = new ArchiveClaimOwner(harness.ProjectId, taskId, "new", newLease);
        Assert.False(await harness.Tasks.TryAdvanceAsync(taskId, oldOwner, 1, 1, CancellationToken.None));
        Assert.True(await harness.Tasks.TryAdvanceAsync(taskId, newOwner, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCreateAllowsExactlyOneTask()
    {
        await using var harness = await Harness.CreateAsync(services);
        var results = await Task.WhenAll(
            Attempt(() => harness.Service.CreateAsync(harness.Project, null, "111", "t1", "", CancellationToken.None)),
            Attempt(() => harness.Service.CreateAsync(harness.Project, null, "222", "t2", "", CancellationToken.None)));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(1, results.Count(result => result.Error is ApiError));
        Assert.Equal(1, (int)await harness.Store.Collection("archive_import_task")
            .CountDocumentsAsync(new BsonDocument("p", harness.ProjectId)));
    }

    [Fact]
    public async Task TransportNeverSendsProviderKeyToDownloadHost()
    {
        await using var harness = await Harness.CreateAsync(services, keys: ["super-secret-key"]);
        harness.Transport.Zip = Zip(("images/a.jpg", A));
        await harness.CreateTaskAsync();

        await harness.RunAsync();

        Assert.Single(harness.Transport.DownloadUrls);
        Assert.DoesNotContain("super-secret-key", harness.Transport.DownloadUrls[0]);
        Assert.DoesNotContain("super-secret-key", (await harness.LatestAsync()).ToJson());
    }

    [Fact]
    public async Task TempDirectoryIsRemovedAfterSuccessAndFailure()
    {
        await using var success = await Harness.CreateAsync(services);
        success.Transport.Zip = Zip(("images/a.jpg", A));
        await success.CreateTaskAsync();
        await success.RunAsync();
        Assert.Empty(success.TempDirectories());

        await using var failure = await Harness.CreateAsync(services);
        failure.Transport.Zip = Corrupt(Zip(("images/a.jpg", A)), A);
        await failure.CreateTaskAsync();
        await failure.RunAsync();
        Assert.Empty(failure.TempDirectories());
    }

    private static async Task<(bool Success, Exception? Error)> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return (true, null);
        }
        catch (Exception error)
        {
            return (false, error);
        }
    }

    internal static string Canonical(string gid) => $"https://eu.hath.network/archive/{gid}/aa/bb/2?start=1";

    internal static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return memory.ToArray();
    }

    private static byte[] Corrupt(byte[] zip, byte[] needle)
    {
        var copy = (byte[])zip.Clone();
        var index = copy.AsSpan().IndexOf(needle);
        Assert.True(index >= 0);
        copy[index + needle.Length / 2] ^= 0xff;
        return copy;
    }

    internal sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    internal sealed class FakeQueue : IArchiveJobQueue
    {
        public List<BsonDocument> Payloads { get; } = [];
        public List<string> Kinds { get; } = [];
        public Exception? Error { get; set; }

        public Task EnqueueAsync(string kind, BsonDocument payload, CancellationToken ct)
        {
            if (Error is not null) throw Error;
            Kinds.Add(kind);
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeTransport : IArchiveImportTransport
    {
        public List<(string Gid, string Token, string Key)> ResolveCalls { get; } = [];
        public List<string> DownloadUrls { get; } = [];
        public Func<int, string, string, string, string?>? Resolver { get; set; }
        public byte[] Zip { get; set; } = [];
        public Exception? Error { get; set; }

        public Task<string?> ResolveArchiveUrlAsync(string apiUrl, string gid, string token, string apiKey,
            CancellationToken ct)
        {
            var index = ResolveCalls.Count;
            ResolveCalls.Add((gid, token, apiKey));
            var url = Resolver is null ? ArchiveImportTests.Canonical(gid) : Resolver(index, gid, token, apiKey);
            return Task.FromResult(url);
        }

        public async Task DownloadAsync(string zipUrl, string destinationPath, long maxBytes, CancellationToken ct)
        {
            DownloadUrls.Add(zipUrl);
            if (Error is not null) throw Error;
            await File.WriteAllBytesAsync(destinationPath, Zip, ct);
        }
    }

    internal sealed class FakeUploader : IArchiveProjectUploader
    {
        public List<string> Names { get; } = [];
        public List<byte[]> Contents { get; } = [];
        public Func<int, string, Exception?>? Error { get; set; }

        public async Task UploadAsync(BsonDocument project, string filename, Stream content, long length,
            CancellationToken ct)
        {
            var index = Names.Count;
            var error = Error?.Invoke(index, filename);
            if (error is not null) throw error;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            Names.Add(filename);
            Contents.Add(memory.ToArray());
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public required LocalServices Services { get; init; }
        public MongoStore Store => Services.Store;
        public required TestClock Clock { get; init; }
        public required ArchiveImportOptions Options { get; init; }
        public required ArchiveSecrets Secrets { get; init; }
        public required FakeQueue Queue { get; init; }
        public required FakeTransport Transport { get; init; }
        public required FakeUploader Uploader { get; init; }
        public required ArchiveImportTaskStore Tasks { get; init; }
        public required ArchiveImportClaimStore Claims { get; init; }
        public required ArchiveImportService Service { get; init; }
        public required ObjectId TeamId { get; init; }
        public required ObjectId ProjectId { get; init; }
        public required BsonDocument Project { get; init; }

        public static async Task<Harness> CreateAsync(LocalServices services, string[]? keys = null,
            Action<ArchiveImportOptions>? configure = null)
        {
            var clock = new TestClock();
            var options = new ArchiveImportOptions
            {
                ProviderApiUrl = "https://api.example.test",
                AllowedHosts = ["api.example.test"],
                TempRoot = Path.Combine(Path.GetTempPath(), "shoreline-archive-tests",
                    Guid.NewGuid().ToString("N")),
            };
            configure?.Invoke(options);
            Directory.CreateDirectory(options.TempRoot!);

            var secrets = new ArchiveSecrets(services.Configuration, clock);
            var teamId = ObjectId.GenerateNewId();
            var projectId = ObjectId.GenerateNewId();
            var keyArray = new BsonArray();
            foreach (var key in keys ?? ["synthetic-key"])
            {
                keyArray.Add(new BsonDocument
                {
                    ["id"] = ObjectId.GenerateNewId().ToString(),
                    ["remark"] = "",
                    ["enabled"] = true,
                    ["key"] = secrets.Encrypt(key),
                });
            }
            await services.Store.Collection("team").InsertOneAsync(new BsonDocument
            {
                ["_id"] = teamId, ["n"] = "archive-team-" + teamId, ["aau"] = "", ["aak"] = keyArray,
            });
            await services.Store.Collection("project").InsertOneAsync(new BsonDocument
            {
                ["_id"] = projectId, ["t"] = teamId, ["n"] = "archive-project-" + projectId, ["st"] = 0,
                ["fc"] = 0, ["label_revision"] = 0L,
            });
            var project = await services.Store.Collection("project")
                .Find(new BsonDocument("_id", projectId)).FirstAsync();

            var tasks = new ArchiveImportTaskStore(services.Store, clock);
            var claims = new ArchiveImportClaimStore(services.Store, clock);
            var queue = new FakeQueue();
            var transport = new FakeTransport();
            var uploader = new FakeUploader();
            return new Harness
            {
                Services = services,
                Clock = clock,
                Options = options,
                Secrets = secrets,
                Queue = queue,
                Transport = transport,
                Uploader = uploader,
                Tasks = tasks,
                Claims = claims,
                TeamId = teamId,
                ProjectId = projectId,
                Project = project,
                Service = new ArchiveImportService(services.Store, tasks, claims, queue, transport, uploader,
                    secrets, options, clock),
            };
        }

        public Task<BsonDocument> CreateTaskAsync() =>
            Service.CreateAsync(Project, null, "12345", "synthetic-token", "", CancellationToken.None);

        public Task RunAsync()
        {
            var payload = Queue.Payloads.Count != 0
                ? Queue.Payloads[^1]
                : new BsonDocument
                {
                    ["project"] = ProjectId.ToString(),
                    ["taskid"] = Tasks.LatestAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult()!["_id"]
                        .AsObjectId.ToString(),
                    ["label_revision"] = 0,
                };
            return Service.RunAsync(payload, CancellationToken.None);
        }

        public Task<BsonDocument> LatestAsync() =>
            Tasks.LatestAsync(ProjectId, CancellationToken.None)!;

        public string[] TempDirectories() => Directory.GetDirectories(Options.TempRoot!);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Options.TempRoot is not null && Directory.Exists(Options.TempRoot))
                    Directory.Delete(Options.TempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return ValueTask.CompletedTask;
        }
    }
}
