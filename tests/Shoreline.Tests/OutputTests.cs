using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Formats;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class OutputTests(LocalServices services)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_WritesCanonicalLegacyBsonAndQueuesJob()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var project = await ProjectAsync();
        var target = await TargetAsync(project);
        var include = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var exclude = new[] { ObjectId.GenerateNewId() };

        var output = await service.CreateAsync(project, target, Actor(), OutputTypes.OnlyText,
            include, exclude, CancellationToken.None);

        Assert.Equal(project["_id"].AsObjectId, output["p"].AsObjectId);
        Assert.Equal(target["_id"].AsObjectId, output["t"].AsObjectId);
        Assert.Equal(0, output.Integer("s"));
        Assert.Equal(1, output.Integer("ty"));
        Assert.Equal("", output.Text("file_name"));
        Assert.Equal(0L, output["sb"].ToInt64());
        Assert.True(output.Contains("file_name"));
        Assert.False(output.Contains("fn"));
        Assert.Equal(include, output["file_ids_include"].AsBsonArray.Select(v => v.AsObjectId));
        Assert.Equal(exclude, output["file_ids_exclude"].AsBsonArray.Select(v => v.AsObjectId));

        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument
            {
                ["kind"] = OutputService.JobKind,
                ["payload.output_id"] = output["_id"].AsObjectId,
            }).FirstAsync();
        Assert.Equal(output["_id"].AsObjectId, job["payload"]["output_id"].AsObjectId);
        Assert.Equal(project["_id"].AsObjectId, job["payload"]["project_id"].AsObjectId);
        Assert.Equal(0L, job["payload"]["label_revision"].ToInt64());
    }

    [Fact]
    public async Task Create_CooldownReturns9002()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var project = await ProjectAsync();
        var target = await TargetAsync(project);
        await InsertOutputAsync(project, target, status: OutputStatus.Succeeded,
            createTime: BaseTime.UtcDateTime);

        var error = await Assert.ThrowsAsync<ApiError>(() => service.CreateAsync(
            project, target, Actor(), OutputTypes.All, null, null, CancellationToken.None));
        Assert.Equal(9002, error.Code);
        Assert.Equal("导出过于频繁，请稍后再试", error.Message);
    }

    [Fact]
    public async Task Create_PooledStorageFailsClosedWith9003()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var project = await ProjectAsync();
        project["csid"] = ObjectId.GenerateNewId();
        await services.Store.Collection("project").ReplaceOneAsync(
            new BsonDocument("_id", project["_id"]), project);
        var target = await TargetAsync(project);

        var error = await Assert.ThrowsAsync<ApiError>(() => service.CreateAsync(
            project, target, Actor(), OutputTypes.All, null, null, CancellationToken.None));
        Assert.Equal(9003, error.Code);
        Assert.Equal(503, error.Status);
        Assert.Equal(0, await services.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("t", target["_id"])));
        Assert.Equal(0, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = OutputService.JobKind,
                ["payload.project_id"] = project["_id"].AsObjectId,
            }));
    }

    [Fact]
    public async Task CreateAll_SkipsCoolingTargetAndCreatesOthers()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var project = await ProjectAsync();
        var cooling = await TargetAsync(project);
        var free = await TargetAsync(project);
        await InsertOutputAsync(project, cooling, createTime: BaseTime.UtcDateTime);

        var created = await service.CreateAllAsync(project, Actor(), CancellationToken.None);

        Assert.Single(created);
        Assert.Equal(free["_id"].AsObjectId, created[0]["t"].AsObjectId);
        Assert.Equal(1, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = OutputService.JobKind,
                ["payload.project_id"] = project["_id"].AsObjectId,
            }));
    }

    [Fact]
    public async Task Prune_DeletesOldestObjectsAndRowsKeepingNewestTwo()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var project = await ProjectAsync(outputBytes: 3 * 2048, fileSize: 6);
        var target = await TargetAsync(project);
        var oldest = await InsertOutputAsync(project, target, file: "oldest.zip", bytes: 2048,
            createTime: BaseTime.AddDays(-3).UtcDateTime);
        var middle = await InsertOutputAsync(project, target, file: "middle.zip", bytes: 2048,
            createTime: BaseTime.AddDays(-2).UtcDateTime);
        var newest = await InsertOutputAsync(project, target, file: "newest.zip", bytes: 2048,
            createTime: BaseTime.AddDays(-1).UtcDateTime);
        foreach (var row in new[] { oldest, middle, newest })
            await PutObjectAsync("outputs/" + row["_id"].AsObjectId + "/" + row.Text("file_name"));

        await service.CreateAsync(project, target, Actor(), OutputTypes.All, null, null,
            CancellationToken.None);

        Assert.Equal(3, await services.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("t", target["_id"])));
        Assert.Null(await services.Store.Collection("output")
            .Find(new BsonDocument("_id", oldest["_id"])).FirstOrDefaultAsync());
        Assert.NotNull(await services.Store.Collection("output")
            .Find(new BsonDocument("_id", middle["_id"])).FirstOrDefaultAsync());
        Assert.NotNull(await services.Store.Collection("output")
            .Find(new BsonDocument("_id", newest["_id"])).FirstOrDefaultAsync());
        Assert.Null(await Storage().HeadAsync(
            "outputs/" + oldest["_id"].AsObjectId + "/oldest.zip"));
        var reloaded = await ProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(4096L, reloaded["ob"].ToInt64());
        Assert.Equal(4L, reloaded["fs"].ToInt64());
    }

    [Fact]
    public async Task Prune_PhysicalDeleteFailureKeepsRowsAndBytes()
    {
        var project = await ProjectAsync(outputBytes: 2048, fileSize: 2);
        var output = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"].AsObjectId,
            ["t"] = ObjectId.GenerateNewId(),
            ["file_name"] = "old.zip",
            ["sb"] = 2048L
        };
        await services.Store.Collection("output").InsertOneAsync(output);

        await Assert.ThrowsAsync<IOException>(() => OutputObjectCleanup.DeleteObjectsStrictAsync(
            services.Store,
            ProjectCleanupOptions.FromConfiguration(services.Configuration),
            (_, _) => throw new IOException("delete failed"),
            project,
            [output],
            CancellationToken.None));

        var stored = await services.Store.Collection("output")
            .Find(new BsonDocument("_id", output["_id"])).FirstAsync();
        Assert.Equal(2048L, stored["sb"].ToInt64());
        var reloaded = await ProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(2048L, reloaded["ob"].ToInt64());
        Assert.Equal(2L, reloaded["fs"].ToInt64());
    }

    [Fact]
    public void ForeignTarget_Is4024()
    {
        var project = new BsonDocument("_id", ObjectId.GenerateNewId());
        var target = new BsonDocument("t", ObjectId.GenerateNewId());
        var error = Assert.Throws<ApiError>(() =>
            OutputService.EnsureTargetBelongsToProject(project, target));
        Assert.Equal(4024, error.Code);
    }

    [Fact]
    public void StatusDetails_MatchPythonOrderAndNames()
    {
        Assert.Equal(6, OutputStatusDetails.All().Length);
        var json = JsonSerializer.Serialize(OutputStatusDetails.All());
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("源文件整理中", parsed.RootElement[1].GetProperty("name").GetString());
        Assert.Equal("翻译整理中", parsed.RootElement[2].GetProperty("name").GetString());
        Assert.Equal("导出错误，请重试", parsed.RootElement[5].GetProperty("name").GetString());
    }

    [Fact]
    public void LabelPlus_HeaderAndTranslationLinePreservePythonCrlfBehaviour()
    {
        Assert.Equal(
            "1,0\r\n-\r\n框内\r\n框外\r\n-\r\n可使用 LabelPlus Photoshop 脚本导入 psd 中\r\n",
            LabelPlusWriter.Header);
        Assert.Equal("a\r\nb\r\n", LabelPlusWriter.TranslationLine("a\nb"));
        Assert.Equal("a\r\r\nb\r\n", LabelPlusWriter.TranslationLine("a\r\nb"));
        Assert.Equal(">>>>>>>>[001.png]<<<<<<<<\r\n", LabelPlusWriter.FileHeader("001.png"));
    }

    [Fact]
    public void LabelPlus_SourceEntryUsesPythonFloatStrings()
    {
        Assert.Equal("----------------[1]----------------[0.0,0.5,1]\r\n",
            LabelPlusWriter.SourceEntry(1, 0, 0.5, 1));
        Assert.Equal("----------------[2]----------------[1.0,100.0,2]\r\n",
            LabelPlusWriter.SourceEntry(2, 1, 100, 2));
        Assert.Equal("1e-07", PythonValue.Float(1e-7));
        Assert.Equal("1e-05", PythonValue.Float(1e-5));
        Assert.Equal("0.0001", PythonValue.Float(1e-4));
        Assert.Equal("1000000000000000.0", PythonValue.Float(1e15));
        Assert.Equal("1e+16", PythonValue.Float(1e16));
        Assert.Equal("nan", PythonValue.Float(double.NaN));
    }

    [Fact]
    public void ExportNames_OnlyManualOrderUsesPaddedNames()
    {
        var a = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "a.png" };
        var b = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "b.png", ["mo"] = 1 };
        Assert.Empty(LabelPlusWriter.ExportFileNames([a]));

        var names = LabelPlusWriter.ExportFileNames([a, b]);
        Assert.Equal("0001_a.png", names[a["_id"].AsObjectId]);
        Assert.Equal("0002_b.png", names[b["_id"].AsObjectId]);
    }

    [Fact]
    public void StaffBlock_OrderExternalAndLineBreakNormalization()
    {
        var members = new[]
        {
            new BsonDocument { ["display_name"] = "译员\r\n甲", ["tags"] = new BsonArray { "translator" }, ["status"] = "active" },
            new BsonDocument { ["display_name"] = "图源", ["tags"] = new BsonArray { "raw_provider" }, ["status"] = "active" },
            new BsonDocument { ["display_name"] = "外部", ["tags"] = new BsonArray { "translator", "typesetter" }, ["status"] = "active" },
            new BsonDocument { ["display_name"] = "移除", ["tags"] = new BsonArray { "scanner" }, ["status"] = "removed" }
        };
        var block = LabelPlusWriter.StaffBlock(members);

        Assert.Equal(
            "----------------[0]----------------[0.5,0.5,2]\r\n" +
            "图源：图源\r\n" +
            "翻译：译员  甲、外部\r\n" +
            "嵌字：外部\r\n",
            block);
        Assert.Null(LabelPlusWriter.StaffBlock([]));
    }

    [Fact]
    public void StaffPage_ProjectTeamFallbackAndIndexClamp()
    {
        Assert.Equal(3, LabelPlusWriter.ResolveStaffPage(null, 3));
        Assert.Equal(1, LabelPlusWriter.ResolveStaffPage(null, null));
        Assert.Equal(0, LabelPlusWriter.ResolveStaffPage(0, 3));
        Assert.Equal(1, LabelPlusWriter.StaffTargetIndex(5, 2));
        Assert.Equal(5, LabelPlusWriter.StaffTargetIndex(5, 0));
        Assert.Equal(4, LabelPlusWriter.StaffTargetIndex(5, -1));
        Assert.Equal(4, LabelPlusWriter.StaffTargetIndex(5, 99));
        Assert.Equal(0, LabelPlusWriter.StaffTargetIndex(5, -99));
        Assert.Equal(-1, LabelPlusWriter.StaffTargetIndex(0, 1));
    }

    [Fact]
    public void ProjectJson_UsesExactPythonKeyOrderAndValues()
    {
        var project = new BsonDocument
        {
            ["n"] = "项目",
            ["i"] = "简介",
            ["m_a"] = 1,
            ["m_t"] = 2,
            ["ct"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["et"] = new BsonDateTime(BaseTime.AddHours(1).UtcDateTime),
            ["slp"] = 3,
            ["ple"] = true,
            ["plt"] = new BsonArray { "translator" }
        };
        var fields = ProjectOutputJson.Fields(project, "translator", "ja", ["zh-hans"]);
        Assert.Equal(
            new[] { "name", "intro", "default_role", "allow_apply_type", "application_check_type",
             "is_need_check_application", "create_time", "edit_time", "source_language",
             "target_languages", "staff_list_page", "position_lock_enabled", "position_lock_tags" },
            fields.Select(field => field.Key));
        Assert.Equal("项目", fields[0].Value);
        Assert.True((bool)fields[5].Value!);
        Assert.Equal("2026-11-09T12:00:00", fields[6].Value);
        Assert.Equal(3L, Convert.ToInt64(fields[10].Value));
    }

    [Fact]
    public void SafeNames_ReplaceUnsafeCharactersAndNeutralizeTraversal()
    {
        Assert.Equal("a□b□c", LabelPlusWriter.SafeDownloadName("a/b:c"));
        Assert.Equal("□/evil", LabelPlusWriter.SafeEntryPath("../evil"));
        Assert.Equal("a/□/b", LabelPlusWriter.SafeEntryPath("a\\..\\b"));
    }

    [Fact]
    public async Task Archive_StoresUtf8EntriesWithoutCompression()
    {
        var root = Path.Combine(Path.GetTempPath(), "shoreline-output-" + Guid.NewGuid().ToString("N"));
        var zip = root + ".zip";
        Directory.CreateDirectory(Path.Combine(root, "images", "子"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "translations.txt"), "翻译\r\n",
                new UTF8Encoding(false));
            await File.WriteAllBytesAsync(Path.Combine(root, "images", "子", "001.png"),
                [1, 2, 3, 4]);
            await OutputArchive.CreateAsync(root, zip,
                ["translations.txt", "images/子/001.png", "../evil"], CancellationToken.None);

            using var archive = ZipFile.OpenRead(zip);
            Assert.Equal(new[] { "translations.txt", "images/子/001.png" },
                archive.Entries.Select(entry => entry.FullName));
            var text = archive.GetEntry("translations.txt")!;
            using var reader = new StreamReader(text.Open(), Encoding.UTF8);
            Assert.Equal("翻译\r\n", await reader.ReadToEndAsync());
            Assert.Equal(text.Length, text.CompressedLength);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(".."));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
            try { File.Delete(zip); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Accounting_IsIdempotentAndUsesBankersRounding()
    {
        var project = await ProjectAsync();
        var output = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"].AsObjectId,
            ["s"] = 0,
            ["sb"] = 100L
        };
        await services.Store.Collection("output").InsertOneAsync(output);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", output["_id"]);

        Assert.True(await OutputAccounting.RecordArtifactAsync(
            services.Store, output["_id"].AsObjectId, 100, "first.zip", BaseTime.UtcDateTime,
            filter, CancellationToken.None));
        Assert.True(await OutputAccounting.ApplyProjectAccountingAsync(
            services.Store, project["_id"].AsObjectId, CancellationToken.None));
        var pending = await LoadOutputAsync(output["_id"].AsObjectId);
        Assert.Equal(0, pending.Integer("s"));
        Assert.True(pending.Contains("job_accounting"));

        Assert.True(await OutputAccounting.RecordArtifactAsync(
            services.Store, output["_id"].AsObjectId, 1124, "first.zip", BaseTime.UtcDateTime,
            filter, CancellationToken.None));
        Assert.True(await OutputAccounting.ApplyProjectAccountingAsync(
            services.Store, project["_id"].AsObjectId, CancellationToken.None));
        Assert.True(await OutputAccounting.ApplyProjectAccountingAsync(
            services.Store, project["_id"].AsObjectId, CancellationToken.None));
        Assert.True(await OutputAccounting.MarkSucceededAsync(
            services.Store, output["_id"].AsObjectId, "first.zip", BaseTime.UtcDateTime,
            filter, CancellationToken.None));

        var stored = await LoadOutputAsync(output["_id"].AsObjectId);
        Assert.Equal(1124L, stored["sb"].ToInt64());
        Assert.Equal(OutputStatus.Succeeded, stored.Integer("s"));
        Assert.False(stored.Contains("job_accounting"));
        var reloaded = await ProjectAsync(project["_id"].AsObjectId);
        Assert.Equal(1124L, reloaded["ob"].ToInt64());
        Assert.Equal(1L, reloaded["fs"].ToInt64());
    }

    [Fact]
    public async Task Export_ProducesLabelPlusProjectJsonAndImages()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);

        await ExportService(clock).RunAsync(fixture.Payload, CancellationToken.None);

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Succeeded, output.Integer("s"));
        Assert.Equal("导出项目 - 简体中文.zip", output.Text("file_name"));
        Assert.True(output["sb"].ToInt64() > 0);
        var storedProject = await ProjectAsync(fixture.ProjectId);
        Assert.Equal(output["sb"].ToInt64(), storedProject["ob"].ToInt64());
        Assert.Equal((long)Math.Round(output["sb"].ToInt64() / 1024.0, MidpointRounding.ToEven),
            storedProject["fs"].ToInt64());

        using var download = await Storage().GetAsync("outputs/" + fixture.OutputId + "/" + output.Text("file_name"), CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "translations.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "project.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "images/001.png");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "errors.txt");

        using var reader = new StreamReader(archive.GetEntry("translations.txt")!.Open(), Encoding.UTF8);
        var labelPlus = await reader.ReadToEndAsync();
        Assert.Equal(
            LabelPlusWriter.Header +
            ">>>>>>>>[第1话/001.png]<<<<<<<<\r\n" +
            "----------------[1]----------------[0.0,0.0,1]\r\n" +
            "校对\r\n" +
            "----------------[2]----------------[0.5,1.0,2]\r\n" +
            "校对内容\r\n",
            labelPlus);

        using var projectJson = new StreamReader(archive.GetEntry("project.json")!.Open(), Encoding.UTF8);
        var json = await projectJson.ReadToEndAsync();
        Assert.Contains("\"output_id\": \"" + fixture.OutputId + "\"", json);
        Assert.Contains("\"output_language\": \"zh-hans\"", json);
        Assert.True(json.IndexOf("\"name\"", StringComparison.Ordinal) <
                    json.IndexOf("\"output_id\"", StringComparison.Ordinal));

        using var image = new MemoryStream();
        await archive.GetEntry("images/001.png")!.Open().CopyToAsync(image);
        Assert.Equal(new byte[] { 9, 8, 7 }, image.ToArray());

        var staging = await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/.work/", null, 100, CancellationToken.None);
        Assert.Empty(staging.Objects);
    }

    [Fact]
    public async Task Export_MissingImageWritesErrorsTxtAndStillSucceeds()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: false);

        await ExportService(clock).RunAsync(fixture.Payload, CancellationToken.None);

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Succeeded, output.Integer("s"));
        using var download = await Storage().GetAsync("outputs/" + fixture.OutputId + "/" + output.Text("file_name"), CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("errors.txt")!.Open(), Encoding.UTF8);
        var errors = await reader.ReadToEndAsync();
        Assert.Contains("Project Name: 导出项目\r\n", errors);
        Assert.Contains($"File 001.png<{fixture.FileId}> is not found in server.\r\n", errors);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "images/001.png");
    }

    [Fact]
    public async Task Export_ReplayDoesNotDoubleCountAndDoesNotReplaceFinalObject()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var service = ExportService(clock);
        await service.RunAsync(fixture.Payload, CancellationToken.None);
        var first = await LoadOutputAsync(fixture.OutputId);
        var projectAfterFirst = await ProjectAsync(fixture.ProjectId);

        await service.RunAsync(fixture.Payload, CancellationToken.None);

        var second = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(first["sb"].ToInt64(), second["sb"].ToInt64());
        Assert.Equal(first.Text("file_name"), second.Text("file_name"));
        Assert.Equal(second["sb"].ToInt64(), projectAfterFirst["ob"].ToInt64());
        Assert.Equal(projectAfterFirst["ob"].ToInt64(),
            (await ProjectAsync(fixture.ProjectId))["ob"].ToInt64());
    }

    [Fact]
    public async Task Export_RetryAfterAccountingCrashConvergesWithoutDoubleCounting()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);

        var beforeLedger = ExportService(clock);
        beforeLedger.AfterArtifactAsync = _ =>
            throw new InvalidOperationException("injected crash after artifact");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            beforeLedger.RunAsync(fixture.Payload, CancellationToken.None));

        var crashed = await LoadOutputAsync(fixture.OutputId);
        var bytes = crashed["sb"].ToInt64();
        Assert.True(bytes > 0);
        Assert.Equal(OutputStatus.Error, crashed.Integer("s"));
        Assert.True(crashed.Contains("job_accounting"));
        Assert.Equal(0L, (await ProjectAsync(fixture.ProjectId))["ob"].ToInt64());

        await ResetForRetryAsync(fixture.OutputId);
        await ExportService(clock).RunAsync(fixture.Payload, CancellationToken.None);
        Assert.Equal(bytes, (await LoadOutputAsync(fixture.OutputId))["sb"].ToInt64());
        Assert.Equal(bytes, (await ProjectAsync(fixture.ProjectId))["ob"].ToInt64());

        var afterLedger = ExportService(clock);
        afterLedger.AfterLedgerAsync = _ =>
            throw new InvalidOperationException("injected crash after ledger");
        await ResetForRetryAsync(fixture.OutputId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            afterLedger.RunAsync(fixture.Payload, CancellationToken.None));

        var ledgerApplied = await ProjectAsync(fixture.ProjectId);
        Assert.Equal(bytes, ledgerApplied["ob"].ToInt64());
        Assert.Equal(OutputStatus.Error, (await LoadOutputAsync(fixture.OutputId)).Integer("s"));

        await ResetForRetryAsync(fixture.OutputId);
        await ExportService(clock).RunAsync(fixture.Payload, CancellationToken.None);

        var after = await LoadOutputAsync(fixture.OutputId);
        var project = await ProjectAsync(fixture.ProjectId);
        Assert.Equal(OutputStatus.Succeeded, after.Integer("s"));
        Assert.Equal(bytes, after["sb"].ToInt64());
        Assert.False(after.Contains("job_accounting"));
        Assert.Equal(bytes, project["ob"].ToInt64());
        Assert.Equal((long)Math.Round(bytes / 1024.0, MidpointRounding.ToEven),
            project["fs"].ToInt64());
    }

    private async Task ResetForRetryAsync(ObjectId outputId)
    {
        await services.Store.Collection("output").UpdateOneAsync(
            new BsonDocument("_id", outputId),
            new BsonDocument("$set", new BsonDocument
            {
                ["s"] = OutputStatus.Ziping,
                ["job_owner"] = BsonNull.Value,
                ["job_lease_until"] = BsonNull.Value
            }));
    }

    [Fact]
    public async Task Export_DeletedFrozenFileMetadataFailsWithoutPublishing()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var service = ExportService(clock);
        service.BeforePublishVerifyAsync = async _ =>
        {
            await services.Store.Collection("file")
                .DeleteOneAsync(new BsonDocument("_id", fixture.FileId));
        };

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.RunAsync(fixture.Payload, CancellationToken.None));
        Assert.Equal(409, error.Status);

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Error, output.Integer("s"));
        Assert.Empty((await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    [Fact]
    public async Task Export_ChangedFrozenSaveNameFailsWithoutPublishing()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var service = ExportService(clock);
        service.BeforePublishVerifyAsync = async _ =>
        {
            await services.Store.Collection("file").UpdateOneAsync(
                new BsonDocument("_id", fixture.FileId),
                new BsonDocument("$set", new BsonDocument("sa", "changed.png")));
        };

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.RunAsync(fixture.Payload, CancellationToken.None));
        Assert.Equal(409, error.Status);
        Assert.Equal(OutputStatus.Error, (await LoadOutputAsync(fixture.OutputId)).Integer("s"));
        Assert.Empty((await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    [Fact]
    public async Task Export_ConcurrentRunsPublishOnce()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var firstService = ExportService(clock);
        var secondService = ExportService(clock);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstService.BeforePublishVerifyAsync = async _ =>
        {
            paused.SetResult();
            await resume.Task;
        };

        var first = firstService.RunAsync(fixture.Payload, CancellationToken.None);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = secondService.RunAsync(fixture.Payload, CancellationToken.None);
        resume.SetResult();
        await Task.WhenAll(first, second);

        var output = await LoadOutputAsync(fixture.OutputId);
        var project = await ProjectAsync(fixture.ProjectId);
        Assert.Equal(OutputStatus.Succeeded, output.Integer("s"));
        Assert.Equal(output["sb"].ToInt64(), project["ob"].ToInt64());
        var objects = await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None);
        Assert.Single(objects.Objects);
    }

    [Fact]
    public async Task Export_StaleOwnerCannotPublishAfterLeaseExpiry()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var firstService = ExportService(clock);
        var secondService = ExportService(clock);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstService.BeforePublishVerifyAsync = async _ =>
        {
            await services.Store.Collection("output").UpdateOneAsync(
                new BsonDocument("_id", fixture.OutputId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["job_lease_until"] = BaseTime.AddMinutes(-1).UtcDateTime
                }));
            paused.SetResult();
            await resume.Task;
        };

        var first = firstService.RunAsync(fixture.Payload, CancellationToken.None);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = secondService.RunAsync(fixture.Payload, CancellationToken.None);
        resume.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second;

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Succeeded, output.Integer("s"));
        Assert.Equal(output["sb"].ToInt64(),
            (await ProjectAsync(fixture.ProjectId))["ob"].ToInt64());
        Assert.Single((await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    [Fact]
    public async Task Export_RenewExceptionCancelsIoAndCleansStaging()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var service = ExportService(clock);
        service.RenewInterval = TimeSpan.FromMilliseconds(50);
        service.BeforeRenewAsync = _ => throw new IOException("injected renewal failure");
        service.BeforePublishVerifyAsync = async token =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(fixture.Payload, CancellationToken.None));

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.NotEqual(OutputStatus.Succeeded, output.Integer("s"));
        var objects = await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None);
        Assert.DoesNotContain(objects.Objects, item => item.Key.Contains("/.work/", StringComparison.Ordinal));
        Assert.DoesNotContain(objects.Objects, item => !item.Key.Contains("/.work/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_StaleRevisionMarksErrorWithoutPublishing()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        var payload = fixture.Payload.DeepClone().AsBsonDocument;
        payload["label_revision"] = 99L;

        await ExportService(clock).RunAsync(payload, CancellationToken.None);

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Error, output.Integer("s"));
        Assert.Equal("stale_revision", output.Text("job_error"));
        Assert.Empty((await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    [Fact]
    public async Task Export_TargetDeletedMarksErrorWithoutPublishing()
    {
        var clock = new TestClock(BaseTime);
        var fixture = await ExportFixtureAsync(clock, putImage: true);
        await services.Store.Collection("target")
            .DeleteOneAsync(new BsonDocument("_id", fixture.TargetId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExportService(clock).RunAsync(fixture.Payload, CancellationToken.None));

        var output = await LoadOutputAsync(fixture.OutputId);
        Assert.Equal(OutputStatus.Error, output.Integer("s"));
        Assert.Equal("", output.Text("file_name"));
        Assert.Empty((await Storage().ListAsync(
            "outputs/" + fixture.OutputId + "/", null, 100, CancellationToken.None)).Objects);
    }

    // ---------------------------------------------------------------- fixtures

    private OutputService Outputs(TestClock clock) => new(
        services.Store,
        services.App.Services.GetRequiredService<ProjectService>(),
        new ProjectLabelLock(services.Store, clock),
        new LabelRequestContext(),
        Storage(),
        new UserView(services.Configuration, services.App.Services),
        new JobQueue(services.Store, clock),
        services.Configuration,
        clock,
        NullLogger<OutputService>.Instance);

    private OutputExportService ExportService(TestClock clock) => new(
        services.Store,
        new ProjectLabelLock(services.Store, clock),
        new LabelRequestContext(),
        Storage(),
        services.Configuration,
        clock,
        NullLogger<OutputExportService>.Instance);

    private ObjectStorage Storage() => services.App.Services.GetRequiredService<ObjectStorage>();

    private static BsonDocument Actor() => new("_id", ObjectId.GenerateNewId());

    private async Task<BsonDocument> ProjectAsync(
        ObjectId? id = null,
        long outputBytes = 0,
        long fileSize = 0)
    {
        if (id is not null)
            return await services.Store.Collection("project")
                .Find(new BsonDocument("_id", id.Value)).FirstAsync();

        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "项目",
            ["ns"] = "项目",
            ["st"] = 0,
            ["stv"] = 0,
            ["fs"] = fileSize,
            ["ob"] = outputBytes,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["m_a"] = 1,
            ["m_t"] = 1,
            ["ple"] = false,
            ["plt"] = new BsonArray(),
            ["cip"] = false,
            ["label_revision"] = 0L
        };
        if (id is null) await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> TargetAsync(BsonDocument project)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = project["_id"].AsObjectId,
            ["l"] = ObjectId.GenerateNewId(),
            ["ct"] = new BsonDateTime(BaseTime.UtcDateTime)
        };
        await services.Store.Collection("target").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertOutputAsync(
        BsonDocument project,
        BsonDocument target,
        int status = OutputStatus.Queuing,
        DateTime? createTime = null,
        string file = "",
        long bytes = 0)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"].AsObjectId,
            ["t"] = target["_id"].AsObjectId,
            ["u"] = BsonNull.Value,
            ["s"] = status,
            ["ty"] = OutputTypes.All,
            ["file_name"] = file,
            ["sb"] = bytes,
            ["ct"] = new BsonDateTime((createTime ?? BaseTime.UtcDateTime)),
            ["file_ids_include"] = new BsonArray(),
            ["file_ids_exclude"] = new BsonArray()
        };
        await services.Store.Collection("output").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> LoadOutputAsync(ObjectId id) =>
        await services.Store.Collection("output").Find(new BsonDocument("_id", id)).FirstAsync();

    private async Task PutObjectAsync(string key)
    {
        await using var stream = new MemoryStream([1, 2, 3]);
        await Storage().PutAsync(key, stream, "application/octet-stream", CancellationToken.None);
    }

    private async Task<ExportFixture> ExportFixtureAsync(TestClock clock, bool putImage)
    {
        var projectId = ObjectId.GenerateNewId();
        var targetId = ObjectId.GenerateNewId();
        var languageId = ObjectId.GenerateNewId();
        var setId = ObjectId.GenerateNewId();
        var folderId = ObjectId.GenerateNewId();
        var fileId = ObjectId.GenerateNewId();
        var saveName = ObjectId.GenerateNewId() + ".png";
        var source1 = ObjectId.GenerateNewId();
        var source2 = ObjectId.GenerateNewId();
        var outputId = ObjectId.GenerateNewId();

        await services.Store.Collection("project_set").InsertOneAsync(new BsonDocument
        {
            ["_id"] = setId,
            ["n"] = "默认集",
            ["d"] = true
        });
        await services.Store.Collection("language")
            .DeleteManyAsync(new BsonDocument("co", "zh-hans"));
        await services.Store.Collection("language").InsertOneAsync(new BsonDocument
        {
            ["_id"] = languageId,
            ["c"] = "简体中文",
            ["e"] = "Chinese",
            ["co"] = "zh-hans"
        });
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "导出项目",
            ["ps"] = setId,
            ["ol"] = languageId,
            ["st"] = 0,
            ["fs"] = 0L,
            ["ob"] = 0L,
            ["ct"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["et"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["m_a"] = 1,
            ["m_t"] = 1,
            ["ple"] = false,
            ["plt"] = new BsonArray(),
            ["cip"] = false,
            ["label_revision"] = 0L
        });
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = targetId,
            ["t"] = projectId,
            ["l"] = languageId,
            ["ct"] = new BsonDateTime(BaseTime.UtcDateTime)
        });
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = folderId,
            ["n"] = "第1话",
            ["t"] = 1,
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["dn"] = "",
            ["sn"] = "001",
            ["mo"] = BsonNull.Value,
            ["ac"] = true
        });
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = fileId,
            ["n"] = "001.png",
            ["t"] = 2,
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray { folderId },
            ["dn"] = "第1话/",
            ["sn"] = "000001",
            ["mo"] = BsonNull.Value,
            ["sa"] = saveName,
            ["ac"] = true
        });
        await services.Store.Collection("source").InsertManyAsync([
            new BsonDocument { ["_id"] = source1, ["f"] = fileId, ["r"] = 1, ["c"] = "甲", ["x"] = 0.0, ["y"] = 0.0, ["p"] = BsonNull.Value },
            new BsonDocument { ["_id"] = source2, ["f"] = fileId, ["r"] = 2, ["c"] = "乙", ["x"] = 0.5, ["y"] = 1.0, ["p"] = 2 }
        ]);
        await services.Store.Collection("translation").InsertManyAsync([
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["o"] = source1, ["t"] = targetId, ["u"] = ObjectId.GenerateNewId(), ["c"] = "旧", ["p"] = "", ["s"] = false, ["e"] = new BsonDateTime(BaseTime.AddMinutes(1).UtcDateTime) },
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["o"] = source1, ["t"] = targetId, ["u"] = ObjectId.GenerateNewId(), ["c"] = "新", ["p"] = "校对", ["s"] = true, ["e"] = new BsonDateTime(BaseTime.AddMinutes(2).UtcDateTime) },
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["o"] = source2, ["t"] = targetId, ["u"] = ObjectId.GenerateNewId(), ["c"] = "原文", ["p"] = "", ["s"] = false, ["e"] = new BsonDateTime(BaseTime.AddMinutes(1).UtcDateTime) },
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["o"] = source2, ["t"] = targetId, ["u"] = ObjectId.GenerateNewId(), ["c"] = "旧2", ["p"] = "校对内容", ["s"] = false, ["e"] = new BsonDateTime(BaseTime.AddMinutes(2).UtcDateTime) }
        ]);
        await services.Store.Collection("output").InsertOneAsync(new BsonDocument
        {
            ["_id"] = outputId,
            ["p"] = projectId,
            ["t"] = targetId,
            ["u"] = BsonNull.Value,
            ["s"] = OutputStatus.Queuing,
            ["ty"] = OutputTypes.All,
            ["file_name"] = "",
            ["sb"] = 0L,
            ["ct"] = new BsonDateTime(BaseTime.UtcDateTime),
            ["file_ids_include"] = new BsonArray(),
            ["file_ids_exclude"] = new BsonArray()
        });
        if (putImage)
        {
            await using var stream = new MemoryStream([9, 8, 7]);
            await Storage().PutAsync("files/" + saveName, stream, "image/png", CancellationToken.None);
        }

        return new ExportFixture(
            new BsonDocument
            {
                ["output_id"] = outputId,
                ["project_id"] = projectId,
                ["label_revision"] = 0L
            },
            projectId,
            targetId,
            outputId,
            fileId);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ExportFixture(
        BsonDocument Payload,
        ObjectId ProjectId,
        ObjectId TargetId,
        ObjectId OutputId,
        ObjectId FileId);
}

