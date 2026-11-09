using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class ProjectImportTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");
    private static readonly TimeProvider Clock = TimeProvider.System;

    // The parser regression uses the shape of tools/legacy-reference/fixtures/goldens.json
    // (fixtures.labelplus_load), reduced to a fixed inline fixture so the test has no
    // filesystem dependency. It covers header lines, a multi-line translation, an empty
    // label, negative coordinates, CRLF input and a second file.
    private const string GoldenText =
        "LabelPlus export\r\n" +
        ">>>>>>>>[001 - 封面.png]<<<<<<<<\r\n" +
        "----------------[1]----------------[-0.25,0.5,1]\r\n" +
        "第一行\r\n第二行\r\n" +
        "----------------[2]----------------[0.75,0.25,2]\r\n" +
        "\r\n" +
        ">>>>>>>>[002.txt]<<<<<<<<\r\n" +
        "----------------[3]----------------[0.1,0.2,1]\r\n" +
        "text\n";

    // Production FileType.SUPPORTED only admits images, and Source coordinates
    // must be inside [0,1]; this variant exercises a successful import.
    private const string ImportText =
        ">>>>>>>>[cover.png]<<<<<<<<\n" +
        "----------------[1]----------------[0.25,0.5,1]\n" +
        "第一行\n第二行\n" +
        "----------------[2]----------------[0.75,0.25,2]\n" +
        "\n";

    [Fact]
    public void ReaderMatchesLegacyGoldenAndTrimsOnlyOneFinalNewline()
    {
        var files = LabelPlusReader.Read(GoldenText);

        Assert.Equal(new[] { "001 - 封面.png", "002.txt" }, files.Select(file => file.FileName));
        Assert.Equal(2, files[0].Labels.Count);
        Assert.Equal(-0.25, files[0].Labels[0].X);
        Assert.Equal(0.5, files[0].Labels[0].Y);
        Assert.Equal(1, files[0].Labels[0].PositionType);
        Assert.Equal("第一行\n第二行", files[0].Labels[0].Translation);
        Assert.Equal("", files[0].Labels[1].Translation);
        Assert.Equal("text", files[1].Labels[0].Translation);
    }

    [Fact]
    public void ReaderMatchesCommittedLegacyAstGolden()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var golden = fixture.RootElement.GetProperty("fixtures").GetProperty("labelplus_load");
        var files = LabelPlusReader.Read(golden.GetProperty("input").GetString());
        var expected = golden.GetProperty("expected");

        Assert.Equal(expected.GetArrayLength(), files.Count);
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var expectedFile = expected[fileIndex];
            Assert.Equal(expectedFile.GetProperty("file_name").GetString(), files[fileIndex].FileName);
            var expectedLabels = expectedFile.GetProperty("labels");
            Assert.Equal(expectedLabels.GetArrayLength(), files[fileIndex].Labels.Count);
            for (var labelIndex = 0; labelIndex < expectedLabels.GetArrayLength(); labelIndex++)
            {
                var expectedLabel = expectedLabels[labelIndex];
                var actual = files[fileIndex].Labels[labelIndex];
                Assert.Equal(expectedLabel.GetProperty("x").GetDouble(), actual.X);
                Assert.Equal(expectedLabel.GetProperty("y").GetDouble(), actual.Y);
                Assert.Equal(expectedLabel.GetProperty("position_type").GetInt64(), actual.PositionType);
                Assert.Equal(expectedLabel.GetProperty("translation").GetString(), actual.Translation);
            }
        }
    }

    [Fact]
    public void ReaderHandlesEmptyInputAndRejectsLabelBeforeFileOrBadNumbers()
    {
        Assert.Empty(LabelPlusReader.Read(""));
        Assert.Empty(LabelPlusReader.Read(null));
        Assert.Throws<FormatException>(() =>
            LabelPlusReader.Read("----------------[1]----------------[0,0,1]\n"));
        Assert.ThrowsAny<Exception>(() =>
            LabelPlusReader.Read(">>>>>>>>[a.png]<<<<<<<<\n----------------[1]----------------[abc,0,1]\n"));
    }

    [Fact]
    public async Task ValidateBeforeCreateMapsMalformedTextTo4029WithoutDatabaseWrites()
    {
        _ = await SetupAsync();
        var before = await services.Store.Collection("project").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        var (imports, _) = await ImportServicesAsync();

        var error = Assert.Throws<ApiError>(() =>
            imports.ValidateBeforeCreate("----------------[1]----------------[0,0,1]\n"));
        Assert.Equal(4029, error.Code);
        Assert.Equal("LabelplusParseFailedError", error.Error);
        Assert.Equal(before, await services.Store.Collection("project")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task PrepareValidatesSchemaBeforeCreateAndIgnoresExtraExportFields()
    {
        var (team, project) = await SetupAsync();
        var (imports, _) = await ImportServicesAsync();
        var set = await services.Store.Collection("project_set")
            .Find(new BsonDocument("_id", (await ProjectAsync(project))["ps"])).FirstAsync();

        var source = JsonSerializer.SerializeToElement(new
        {
            name = "导入项目", intro = "intro", allow_apply_type = 1, application_check_type = 2,
            default_role = "translator", source_language = "ja", output_language = "zh-CN",
            project_set = ObjectId.GenerateNewId().ToString(),
            unknown_export_field = "ignored", labelplus_txt = "should be ignored"
        });
        var prepared = await imports.PrepareProjectJsonAsync(source, team, set, CancellationToken.None);
        Assert.Equal(set["_id"].AsObjectId.ToString(), prepared.GetProperty("project_set").GetString());
        Assert.Equal(new[] { "zh-CN" }, prepared.GetProperty("target_languages")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.False(prepared.TryGetProperty("output_language", out _));
        Assert.False(prepared.TryGetProperty("labelplus_txt", out _));
        Assert.True(ObjectId.TryParse(prepared.GetProperty("default_role").GetString(), out _));

        await AssertFieldErrorAsync(imports, team, set, new { intro = "x" }, "name");
        await AssertFieldErrorAsync(imports, team, set,
            new { name = "x", intro = "", allow_apply_type = 1, application_check_type = 2,
                default_role = "creator", source_language = "ja", output_language = "zh-CN" }, "default_role");
        await AssertFieldErrorAsync(imports, team, set,
            new { name = "x", intro = "", allow_apply_type = 1, application_check_type = 2,
                default_role = "translator", source_language = "ja", output_language = "ja" }, "6002");
    }

    private static async Task AssertFieldErrorAsync(ProjectImportService imports, BsonDocument team,
        BsonDocument set, object payload, string expected)
    {
        var error = await Assert.ThrowsAsync<ApiError>(async () =>
            await imports.PrepareProjectJsonAsync(JsonSerializer.SerializeToElement(payload), team, set,
                CancellationToken.None));
        if (expected == "6002") Assert.Equal(6002, error.Code);
        else Assert.Equal(2, error.Code);
    }

    [Fact]
    public async Task InitializeWritesPendingFlagsAndCanonicalQueuedPayload()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var (imports, _) = await ImportServicesAsync();

        var jobId = await imports.InitializeAfterCreateAsync(projectDoc, creator, GoldenText, "req-1",
            CancellationToken.None);
        Assert.NotNull(jobId);

        projectDoc = await ProjectAsync(project);
        Assert.Equal(0L, projectDoc.Integer("is"));
        Assert.Equal(0L, projectDoc.Integer("ip"));
        Assert.Equal(GoldenText, projectDoc.Text("it"));

        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", jobId.Value)).FirstAsync();
        Assert.Equal(ProjectImportService.JobKind, job.Text("kind"));
        Assert.Equal(project, job["payload"]["project_id"].AsObjectId);
        Assert.Equal(projectDoc.Integer("label_revision"),
            job["payload"]["label_revision"].ToInt32());
    }

    [Fact]
    public async Task InitializeRequiresOneTargetAndAnActiveCreatorWithoutWritingFlags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var (imports, _) = await ImportServicesAsync();

        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["t"] = project, ["l"] = ObjectId.GenerateNewId()
        });
        Assert.Null(await imports.InitializeAfterCreateAsync(projectDoc, creator, GoldenText, "req",
            CancellationToken.None));
        Assert.Equal(1L, (await ProjectAsync(project)).Integer("is"));
        Assert.Equal(0, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = ProjectImportService.JobKind,
                ["payload.project_id"] = project
            }));

        await services.Store.Collection("target").DeleteManyAsync(new BsonDocument("t", project));
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["t"] = project, ["l"] = ObjectId.GenerateNewId()
        });
        await services.Store.Collection("project_member").DeleteManyAsync(
            new BsonDocument { ["project"] = project, ["tags"] = "creator" });
        Assert.Null(await imports.InitializeAfterCreateAsync(projectDoc, creator, GoldenText, "req",
            CancellationToken.None));
        Assert.Equal(1L, (await ProjectAsync(project)).Integer("is"));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task RunImportsFilesSourcesTranslationsAndCachesWithoutImageObjects()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(projectDoc, creator, ImportText, "req", CancellationToken.None);

        await RunQueuedAsync(imports, project);

        projectDoc = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Succeeded, (int)projectDoc.Integer("is"));
        Assert.Equal(0L, projectDoc.Integer("ip"));
        Assert.Equal("", projectDoc.Text("it"));

        var files = await services.Store.Collection("file")
            .Find(new BsonDocument("p", project)).Sort(new BsonDocument("n", 1)).ToListAsync();
        Assert.Single(files);
        Assert.All(files, file =>
        {
            Assert.Equal(FileService.TypeImage, (int)file.Integer("t"));
            Assert.Equal(0L, file.Integer("fs"));
            Assert.Equal(0L, file.Integer("cover_bytes"));
            Assert.True(file["ac"].AsBoolean);
            Assert.Equal(FileService.FileNotExistNotUpload, (int)file.Integer("fn"));
        });
        Assert.Equal(1L, projectDoc.Integer("fc"));

        var sources = await services.Store.Collection("source")
            .Find(new BsonDocument("f", files[0]["_id"])).Sort(new BsonDocument("r", 1)).ToListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Equal(new[] { 1L, 2L }, sources.Select(source => source.Integer("p")));
        Assert.Equal(0.25, sources[0]["x"].ToDouble());
        Assert.Equal("第一行\n第二行", (await services.Store.Collection("translation")
            .Find(new BsonDocument("o", sources[0]["_id"])).FirstAsync()).Text("c"));
        Assert.Equal(creator["_id"].AsObjectId, (await services.Store.Collection("translation")
            .Find(new BsonDocument("o", sources[0]["_id"])).FirstAsync())["u"].AsObjectId);
        Assert.Equal(1, await services.Store.Collection("file_target_cache")
            .CountDocumentsAsync(new BsonDocument("f", files[0]["_id"])));
    }

    [Fact]
    public async Task RunIsIdempotentAfterSuccess()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, ImportText, "req",
            CancellationToken.None);
        var payload = await QueuedPayloadAsync(project);
        await imports.RunImportAsync(payload, CancellationToken.None);
        var files = await services.Store.Collection("file").CountDocumentsAsync(new BsonDocument("p", project));
        var sources = await services.Store.Collection("source").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

        await imports.RunImportAsync(payload, CancellationToken.None);

        Assert.Equal(files, await services.Store.Collection("file")
            .CountDocumentsAsync(new BsonDocument("p", project)));
        Assert.Equal(sources, await services.Store.Collection("source")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task CancellationAfterSourceCommitDoesNotDuplicateSourceOnRetry()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, ImportText, "req",
            CancellationToken.None);
        SetHook(imports, step =>
        {
            if (step == "after-source") throw new OperationCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunQueuedAsync(imports, project));

        var file = await FileAsync(project);
        Assert.Equal(1, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(0, await TranslationCountAsync(file["_id"].AsObjectId));

        SetHook(imports, null);
        await RunQueuedAsync(imports, project);
        await RunQueuedAsync(imports, project);

        Assert.Equal(ProjectImportService.Succeeded, (int)(await ProjectAsync(project)).Integer("is"));
        Assert.Equal(2, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(1, await TranslationCountAsync(file["_id"].AsObjectId));
        file = await FileAsync(project);
        Assert.Equal(2L, file.Integer("sc"));
        Assert.Equal(1L, file.Integer("tsc"));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task CancellationAfterTranslationCommitDoesNotDuplicateTranslationOnRetry()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, ImportText, "req",
            CancellationToken.None);
        SetHook(imports, step =>
        {
            if (step == "after-translation") throw new OperationCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunQueuedAsync(imports, project));

        var file = await FileAsync(project);
        Assert.Equal(1, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(1, await TranslationCountAsync(file["_id"].AsObjectId));

        SetHook(imports, null);
        await RunQueuedAsync(imports, project);
        await RunQueuedAsync(imports, project);

        Assert.Equal(2, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(1, await TranslationCountAsync(file["_id"].AsObjectId));
        file = await FileAsync(project);
        Assert.Equal(2L, file.Integer("sc"));
        Assert.Equal(1L, file.Integer("tsc"));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task UnknownSourceResultIsRecoveredFromDurableIntentOnReplay()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, ImportText, "req",
            CancellationToken.None);
        SetHook(imports, step =>
        {
            if (step == "after-source") throw new IOException("connection lost after commit");
        });

        await RunQueuedAsync(imports, project);
        var file = await FileAsync(project);
        Assert.Equal(1, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(ProjectImportService.Error, (int)(await ProjectAsync(project)).Integer("is"));

        SetHook(imports, null);
        await SetPendingTextAsync(project, ImportText);
        await RunQueuedAsync(imports, project);

        Assert.Equal(ProjectImportService.Succeeded, (int)(await ProjectAsync(project)).Integer("is"));
        Assert.Equal(2, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(1, await TranslationCountAsync(file["_id"].AsObjectId));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task NewEpochNeverReplaysOldJournalIntent()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, ImportText, "req",
            CancellationToken.None);
        SetHook(imports, step =>
        {
            if (step == "after-source") throw new OperationCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunQueuedAsync(imports, project));
        SetHook(imports, null);

        var oldSource = await services.Store.Collection("source").Find(new BsonDocument("f",
            (await FileAsync(project))["_id"])).FirstAsync();
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project),
            new BsonDocument
            {
                ["$inc"] = new BsonDocument("label_revision", 1),
                ["$set"] = new BsonDocument { ["it"] = ImportText, ["is"] = 0, ["ip"] = 0, ["ie"] = 0 }
            });
        var revision = (await ProjectAsync(project)).Integer("label_revision", 0);
        // A real restore moves every live label row to the new epoch together;
        // only the old journal/source intents must remain unusable.
        await services.Store.Collection("file").UpdateManyAsync(
            new BsonDocument("p", project),
            new BsonDocument("$set", new BsonDocument("label_epoch", revision)));
        await services.Store.Collection("target").UpdateManyAsync(
            new BsonDocument("t", project),
            new BsonDocument("$set", new BsonDocument("label_epoch", revision)));
        await imports.RunImportAsync(new BsonDocument
        {
            ["project_id"] = project, ["label_revision"] = revision
        }, CancellationToken.None);

        var file = await FileAsync(project);
        Assert.Equal(ProjectImportService.Succeeded, (int)(await ProjectAsync(project)).Integer("is"));
        Assert.Equal(1, await services.Store.Collection("file")
            .CountDocumentsAsync(new BsonDocument("p", project)));
        var sources = await services.Store.Collection("source")
            .Find(new BsonDocument("f", file["_id"])).ToListAsync();
        // The epoch-changed replay creates both labels in the new epoch while the
        // single pre-epoch source from the cancelled run remains untouched.
        Assert.Equal(3, sources.Count);
        Assert.Contains(sources, source => source["_id"] == oldSource["_id"]);
        Assert.Equal(2, sources.Count(source =>
            source["_id"] != oldSource["_id"] && source.Integer("label_epoch", 0) == revision));
        var journals = await services.Store.Collection("project_import_journal")
            .Find(new BsonDocument("project_id", project)).ToListAsync();
        Assert.NotEmpty(journals);
        Assert.All(journals, journal => Assert.Equal(revision, journal.Integer("revision", -1)));
        Assert.Equal(ProjectImportService.Succeeded, (int)(await ProjectAsync(project)).Integer("is"));
    }

    [Fact]
    public async Task InitializeMarksErrorWhenQueueingFailsInsteadOfLeavingPendingWithoutJob()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        var sentinelJob = ObjectId.GenerateNewId();
        await services.Store.Collection("csharp_job").InsertOneAsync(
            new BsonDocument("_id", sentinelJob));
        await services.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = "csharp_job",
            ["validator"] = new BsonDocument("$expr", false)
        });

        try
        {
            await Assert.ThrowsAsync<MongoWriteException>(async () =>
                await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator,
                    ImportText, "queue-failure", CancellationToken.None));

            var updated = await ProjectAsync(project);
            Assert.Equal(ProjectImportService.Error, (int)updated.Integer("is"));
            Assert.Equal(ProjectImportService.ErrorUnknown, (int)updated.Integer("ie"));
            Assert.Equal(ImportText, updated.Text("it"));
        }
        finally
        {
            // Tests in this collection share a database (and the collection's
            // unique indexes); disable the validator instead of dropping it.
            await services.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                ["collMod"] = "csharp_job",
                ["validator"] = new BsonDocument(),
                ["validationLevel"] = "off"
            });
            await services.Store.Collection("csharp_job")
                .DeleteOneAsync(new BsonDocument("_id", sentinelJob));
        }
    }

    [Fact]
    public async Task RunWithoutTargetOrCreatorSetsLegacyErrorFlags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var (imports, _) = await ImportServicesAsync();
        var payload = new BsonDocument
        {
            ["project_id"] = project, ["label_revision"] = projectDoc.GetValue("label_revision", 0)
        };

        await services.Store.Collection("target").DeleteManyAsync(new BsonDocument("t", project));
        await SetPendingTextAsync(project, GoldenText);
        await imports.RunImportAsync(payload, CancellationToken.None);
        projectDoc = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Error, (int)projectDoc.Integer("is"));
        Assert.Equal(ProjectImportService.ErrorNoTarget, (int)projectDoc.Integer("ie"));
        Assert.Equal("", projectDoc.Text("it"));

        await services.Store.Collection("project_member").DeleteManyAsync(
            new BsonDocument { ["project"] = project, ["tags"] = "creator" });
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["t"] = project, ["l"] = ObjectId.GenerateNewId()
        });
        await SetPendingTextAsync(project, GoldenText);
        await imports.RunImportAsync(payload, CancellationToken.None);
        projectDoc = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Error, (int)projectDoc.Integer("is"));
        Assert.Equal(ProjectImportService.ErrorNoCreator, (int)projectDoc.Integer("ie"));
    }

    [Fact]
    public async Task RunStaleRevisionReturnsBusyWithoutChangingFlags()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        await imports.InitializeAfterCreateAsync(await ProjectAsync(project), creator, GoldenText, "req",
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<ApiError>(() => imports.RunImportAsync(new BsonDocument
        {
            ["project_id"] = project,
            ["label_revision"] = 99
        }, CancellationToken.None));
        Assert.Equal(1, error.Code);
        Assert.Equal(409, error.Status);
        var projectDoc = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Pending, (int)projectDoc.Integer("is"));
        Assert.Equal(GoldenText, projectDoc.Text("it"));
    }

    [Fact(Skip = "Requires E1 CreateSourceOnceAsync stable-id writer")]
    public async Task RunResumesFromCheckpointAfterPartialFailureWithoutDuplicates()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var (imports, _) = await ImportServicesAsync();
        const string bad = ">>>>>>>>[a.png]<<<<<<<<\n" +
                           "----------------[1]----------------[0.1,0.1,1]\nfirst\n" +
                           "----------------[2]----------------[2,0.2,2]\nsecond\n";
        const string fixedText = ">>>>>>>>[a.png]<<<<<<<<\n" +
                                 "----------------[1]----------------[0.1,0.1,1]\nfirst\n" +
                                 "----------------[2]----------------[0.15,0.2,2]\nsecond\n";
        await SetPendingTextAsync(project, bad);
        var payload = new BsonDocument { ["project_id"] = project, ["label_revision"] = 0 };
        await imports.RunImportAsync(payload, CancellationToken.None);

        var checkpoint = await services.Store.Collection("project_import_checkpoint")
            .Find(new BsonDocument("_id", project)).FirstAsync();
        Assert.Equal(0L, checkpoint.Integer("file_index"));
        Assert.Equal(1L, checkpoint.Integer("labels_done"));
        Assert.Equal(1, await services.Store.Collection("source").CountDocumentsAsync(
            new BsonDocument("f", (await FileAsync(project))["_id"])));

        await SetPendingTextAsync(project, fixedText);
        await imports.RunImportAsync(payload, CancellationToken.None);
        Assert.Equal(ProjectImportService.Succeeded, (int)(await ProjectAsync(project)).Integer("is"));
        Assert.Equal(2, await services.Store.Collection("source").CountDocumentsAsync(
            new BsonDocument("f", (await FileAsync(project))["_id"])));
    }

    [Fact]
    public async Task PlaceholderReusesActiveImageAndRejectsTextOrIllegalNames()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var (imports, _) = await ImportServicesAsync();

        var first = await imports.EnsurePlaceholderFileAsync(projectDoc, "cover.png", CancellationToken.None);
        var second = await imports.EnsurePlaceholderFileAsync(projectDoc, "COVER.PNG", CancellationToken.None);
        Assert.Equal(first["_id"], second["_id"]);
        Assert.Equal(1, await services.Store.Collection("file")
            .CountDocumentsAsync(new BsonDocument("p", project)));

        var textError = await Assert.ThrowsAsync<ApiError>(async () =>
            await imports.EnsurePlaceholderFileAsync(projectDoc, "notes.txt", CancellationToken.None));
        Assert.Equal(107, textError.Code);
        var illegal = await Assert.ThrowsAsync<ApiError>(async () =>
            await imports.EnsurePlaceholderFileAsync(projectDoc, "bad?.png", CancellationToken.None));
        Assert.Equal(107, illegal.Code);
    }

    [Fact]
    public async Task PlaceholderCreatesOneCachePerTargetAndIncrementsProjectFileCount()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["t"] = project, ["l"] = ObjectId.GenerateNewId()
        });
        var targetCount = await services.Store.Collection("target")
            .CountDocumentsAsync(new BsonDocument("t", project));
        var (imports, _) = await ImportServicesAsync();

        var file = await imports.EnsurePlaceholderFileAsync(projectDoc, "page.webp", CancellationToken.None);
        Assert.Equal(targetCount, await services.Store.Collection("file_target_cache")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(1L, (await ProjectAsync(project)).Integer("fc"));
    }

    [Fact]
    public async Task InitializeSkipsEmptyLabelplusTextAndDoesNotQueueAJob()
    {
        var imported = await ImportServicesAsync();
        var (_team, project) = await SetupAsync();
        var projectDoc = await ProjectAsync(project);
        var actor = await UserAsync();

        var job = await imported.Imports.InitializeAfterCreateAsync(
            projectDoc, actor, "", "request-empty", CancellationToken.None);

        Assert.Null(job);
        projectDoc = await ProjectAsync(project);
        Assert.Equal(1L, projectDoc.Integer("is"));
        Assert.Equal("", projectDoc.Text("it"));
        Assert.Equal(0L, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = ProjectImportService.JobKind,
                ["payload.project_id"] = project
            }));
    }

    [Fact]
    public async Task JournalPreallocatesStableIdsAndNeverAdoptsAForeignRankSource()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var revision = projectDoc.Integer("label_revision", 0);
        var (imports, _) = await ImportServicesAsync();
        await SetPendingTextAsync(project, ImportText);
        var payload = new BsonDocument
        {
            ["project_id"] = project, ["label_revision"] = revision
        };
        BsonDocument? intent = null;
        SetHook(imports, step =>
        {
            if (step != "after-intent") return;
            intent = services.Store.Collection("project_import_journal")
                .Find(new BsonDocument("project_id", project)).First();
            throw new OperationCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            imports.RunImportAsync(payload, CancellationToken.None));
        SetHook(imports, null);

        Assert.NotNull(intent);
        Assert.True(intent!["source_id"].IsObjectId);
        Assert.True(intent["translation_id"].IsObjectId);
        var file = await services.Store.Collection("file")
            .Find(new BsonDocument("p", project)).FirstAsync();
        Assert.Equal(0, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));

        // A different writer legitimately owns the same rank. The preallocated
        // id must win; rank is never an adoption key.
        var foreign = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = file["_id"],
            ["r"] = 0,
            ["label_epoch"] = revision,
            ["c"] = ""
        };
        await services.Store.Collection("source").InsertOneAsync(foreign);
        await imports.RunImportAsync(payload, CancellationToken.None);

        var updated = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Error, (int)updated.Integer("is"));
        Assert.Equal(ProjectImportService.ErrorUnknown, (int)updated.Integer("ie"));
        Assert.Equal(1, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.NotEqual(foreign["_id"], intent["source_id"]);
    }

    [Fact]
    public async Task LegacyJournalWithoutStableIdsIsRejectedInsteadOfClaimingRank()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var (_, project) = await SetupAsync(owner);
        var projectDoc = await ProjectAsync(project);
        var revision = projectDoc.Integer("label_revision", 0);
        var (imports, _) = await ImportServicesAsync();
        await SetPendingTextAsync(project, ImportText);
        var file = await services.Store.Collection("file")
            .Find(new BsonDocument("p", project)).FirstOrDefaultAsync();
        if (file is null)
        {
            var placeholder = await imports.EnsurePlaceholderFileAsync(projectDoc,
                "page.png", CancellationToken.None);
            file = placeholder;
        }
        var foreign = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = file["_id"],
            ["r"] = 0,
            ["label_epoch"] = revision,
            ["c"] = ""
        };
        await services.Store.Collection("source").InsertOneAsync(foreign);
        await services.Store.Collection("project_import_journal").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = $"{project}:{revision}:0:0",
                ["project_id"] = project,
                ["revision"] = revision,
                ["file_index"] = 0,
                ["label_index"] = 0,
                ["file_name"] = "page.png",
                ["source_rank"] = 0,
                ["x"] = 0.25,
                ["y"] = 0.25,
                ["p"] = 1,
                ["translation_done"] = false
            });

        await imports.RunImportAsync(new BsonDocument
        {
            ["project_id"] = project, ["label_revision"] = revision
        }, CancellationToken.None);

        var updated = await ProjectAsync(project);
        Assert.Equal(ProjectImportService.Error, (int)updated.Integer("is"));
        Assert.Equal(ProjectImportService.ErrorUnknown, (int)updated.Integer("ie"));
        Assert.Equal(1, await services.Store.Collection("source")
            .CountDocumentsAsync(new BsonDocument("f", file["_id"])));
        Assert.Equal(0, await services.Store.Collection("translation")
            .CountDocumentsAsync(new BsonDocument("o", foreign["_id"])));
    }

    [Fact]
    public async Task JobHandlerKindMatchesQueuedContract()
    {
        var handler = new ProjectImportJobHandler(services.App.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(ProjectImportService.JobKind, handler.Kind);
        Assert.Equal("import_from_labelplus", handler.Kind);
    }

    private static readonly PropertyInfo StepHook = typeof(ProjectImportService).GetProperty(
        "TestStepHook", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static void SetHook(ProjectImportService imports, Action<string>? hook) =>
        StepHook.SetValue(imports, hook);

    private async Task<long> TranslationCountAsync(ObjectId fileId)
    {
        var sourceIds = (await services.Store.Collection("source")
            .Find(new BsonDocument("f", fileId)).Project(new BsonDocument("_id", 1)).ToListAsync())
            .Select(source => source["_id"]).ToArray();
        if (sourceIds.Length == 0) return 0;
        return await services.Store.Collection("translation")
            .CountDocumentsAsync(new BsonDocument("o", new BsonDocument("$in", new BsonArray(sourceIds))));
    }

    private static string FixturePath()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "legacy-reference",
                    "fixtures", "goldens.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("tools/legacy-reference/fixtures/goldens.json not found");
    }

    private async Task RunQueuedAsync(ProjectImportService imports, ObjectId project)
    {
        var payload = await QueuedPayloadAsync(project);
        await imports.RunImportAsync(payload, CancellationToken.None);
    }

    private async Task<BsonDocument> QueuedPayloadAsync(ObjectId project)
    {
        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("kind", ProjectImportService.JobKind))
            .Sort(new BsonDocument("created_at", -1)).FirstAsync();
        Assert.Equal(project, job["payload"]["project_id"].AsObjectId);
        return job["payload"].AsBsonDocument;
    }

    private async Task SetPendingTextAsync(ObjectId project, string text)
    {
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument
            {
                ["it"] = text, ["is"] = 0, ["ip"] = 0, ["ie"] = 0
            }));
    }

    private Task<BsonDocument> FileAsync(ObjectId project) => services.Store.Collection("file")
        .Find(new BsonDocument("p", project)).FirstAsync();

    private async Task<(ProjectImportService Imports, LabelService Labels)> ImportServicesAsync()
    {
        var labelLock = new ProjectLabelLock(services.Store, Clock);
        var requestContext = new LabelRequestContext();
        var labels = new LabelService(services.Store, Clock,
            new IdentityPermissions(services.Store, Clock),
            new UserView(services.Configuration, services.App.Services),
            services.App.Services, labelLock, requestContext);
        await labels.InitializeAsync(CancellationToken.None);
        var imports = new ProjectImportService(services.Store,
            services.App.Services.GetRequiredService<JobQueue>(),
            services.App.Services.GetRequiredService<LegacyTypes>(),
            labelLock, requestContext, labels, Clock);
        return (imports, labels);
    }

    private async Task<(BsonDocument Team, ObjectId Project)> SetupAsync()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        return await SetupAsync(owner);
    }

    private async Task<(BsonDocument Team, ObjectId Project)> SetupAsync(HttpClient client)
    {
        var teamBody = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name = Name("team"), intro = "", allow_apply_type = 1, application_check_type = 2,
            default_role = (await RoleAsync("team", "member")).ToString()
        }));
        var teamId = ObjectId.Parse(teamBody.GetProperty("team").GetProperty("id").GetString());
        var projectBody = await BodyAsync(await client.PostAsJsonAsync($"/v1/teams/{teamId}/projects", new
        {
            name = Name("project"), intro = "", allow_apply_type = 1, application_check_type = 2,
            source_language = "ja", target_languages = new[] { "zh-CN" }
        }));
        var project = ObjectId.Parse(projectBody.GetProperty("project").GetProperty("id").GetString());
        var team = await services.Store.Collection("team").Find(new BsonDocument("_id", teamId)).FirstAsync();
        return (team, project);
    }

    private Task<BsonDocument> ProjectAsync(ObjectId id) => services.Store.Collection("project")
        .Find(new BsonDocument("_id", id)).FirstAsync();

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];

    private async Task<ObjectId> RoleAsync(string scope, string code) =>
        (await services.Store.Collection(scope + "_role").Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId;

    private async Task<BsonDocument> UserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["n"] = name, ["ns"] = SearchText.Normalize(name), ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["e"] = name + "@example.test", ["p"] = Password,
            ["admin"] = true, ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}


