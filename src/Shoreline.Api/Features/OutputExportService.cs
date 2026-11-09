using System.IO.Compression;
using System.Net;
using System.Text;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Formats;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shoreline.Tests")]

namespace Shoreline.Api.Features;

/// <summary>
/// The long-running output worker. It freezes LabelPlus/project.json/source
/// pointers under the project label lock, releases the lock for downloads and
/// zip creation, then reacquires it to verify the snapshot and publish.
/// </summary>
public sealed class OutputExportService(
    MongoStore store,
    ProjectLabelLock labels,
    LabelRequestContext labelContext,
    ObjectStorage storage,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<OutputExportService> logger)
{
    private const int BatchSize = 200;
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: runs while the publish lock is held, before the
    /// frozen pointer check. Production leaves this null.</summary>
    internal Func<CancellationToken, Task>? BeforePublishVerifyAsync { get; set; }

    /// <summary>Test seam: injected failure after the artifact row is written.</summary>
    internal Func<CancellationToken, Task>? AfterArtifactAsync { get; set; }

    /// <summary>Test seam: injected failure after the project ledger is applied.</summary>
    internal Func<CancellationToken, Task>? AfterLedgerAsync { get; set; }

    /// <summary>Test seam: injected failure immediately before a lease renewal.</summary>
    internal Func<CancellationToken, Task>? BeforeRenewAsync { get; set; }

    /// <summary>Test seam: renewal cadence. Production uses 30 seconds.</summary>
    internal TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(30);

    private readonly ProjectCleanupOptions cleanup = ProjectCleanupOptions.FromConfiguration(configuration);
    private IMongoCollection<BsonDocument> Outputs => store.Collection("output");
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Languages => store.Collection("language");
    private IMongoCollection<BsonDocument> Teams => store.Collection("team");
    private IMongoCollection<BsonDocument> Members => store.Collection("project_member");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task RunAsync(BsonDocument payload, CancellationToken ct)
    {
        if (!TryObjectId(payload.GetValue("output_id", BsonNull.Value), out var outputId)) return;
        var payloadProject = payload.GetValue("project_id", BsonNull.Value);
        var expectedRevision = payload.GetValue("label_revision", BsonNull.Value) is { IsNumeric: true } numeric
            ? numeric.ToInt64()
            : (long?)null;

        var initial = await Outputs.Find(new BsonDocument("_id", outputId)).FirstOrDefaultAsync(ct);
        if (initial is null || initial.Integer("s") == OutputStatus.Succeeded) return;
        if (!TryObjectId(initial.GetValue("p", BsonNull.Value), out var projectId)) return;
        if (payloadProject.IsObjectId && payloadProject.AsObjectId != projectId) return;

        string owner;
        try
        {
            await using var claimSession = await labels.EnterAsync(
                labelContext, projectId, recovery: false, expectedRevision, ct);
            var current = await Outputs.Find(new BsonDocument("_id", outputId)).FirstOrDefaultAsync(ct);
            if (current is null || current.Integer("s") == OutputStatus.Succeeded) return;

            owner = Guid.NewGuid().ToString("N");
            if (!await OutputLease.TryClaimAsync(Outputs, outputId, owner, Now, ct)) return;
        }
        catch (ApiError error) when (error.Status == 409)
        {
            long currentRevision;
            using (var bounded = new CancellationTokenSource(CleanupBudget))
            {
                try
                {
                    currentRevision = await labels.ReadRevisionAsync(store, projectId, bounded.Token)
                        .WaitAsync(CleanupBudget, CancellationToken.None);
                }
                catch (Exception readError)
                {
                    logger.LogWarning("Could not read project revision ({ErrorType})", readError.GetType().Name);
                    throw error;
                }
            }

            if (expectedRevision is not null && currentRevision != expectedRevision.Value)
            {
                using var markBudget = new CancellationTokenSource(CleanupBudget);
                try
                {
                    await FailUnclaimedAsync(outputId, "stale_revision", markBudget.Token)
                        .WaitAsync(CleanupBudget, CancellationToken.None);
                }
                catch (Exception markError)
                {
                    logger.LogWarning("Could not mark stale output ({ErrorType})", markError.GetType().Name);
                }
            }
            else
            {
                throw;
            }
            return;
        }

        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewLoopAsync(outputId, owner, leaseCts, leaseCts.Token);
        string? runDirectory = null;
        string? stagingKey = null;
        try
        {
            runDirectory = CreateRunDirectory(outputId, owner);
            var frozen = await FreezeAsync(outputId, owner, projectId, expectedRevision, runDirectory, leaseCts.Token);

            string artifactPath;
            string extension;
            string contentType;
            if (frozen.Type == OutputTypes.OnlyText)
            {
                artifactPath = Path.Combine(runDirectory, "translations.txt");
                extension = ".txt";
                contentType = "text/plain; charset=utf-8";
            }
            else
            {
                if (!await OutputLease.SetStatusAsync(Outputs, outputId, owner,
                        OutputStatus.Downloading, Now, leaseCts.Token))
                    throw LostOwnership();
                var errors = await DownloadImagesAsync(frozen.Manifest, runDirectory, leaseCts.Token);

                if (!await OutputLease.SetStatusAsync(Outputs, outputId, owner,
                        OutputStatus.Ziping, Now, leaseCts.Token))
                    throw LostOwnership();

                if (errors.Count > 0)
                    await WriteErrorsAsync(runDirectory, projectId, frozen, errors, leaseCts.Token);

                artifactPath = Path.Combine(runDirectory, "output.zip");
                await CreateZipAsync(runDirectory, artifactPath, leaseCts.Token);
                extension = ".zip";
                contentType = "application/zip";
            }

            var fileName = frozen.DownloadName + extension;
            stagingKey = cleanup.OutputPrefix + outputId + "/.work/" + owner + extension;
            var bytes = await UploadAsync(stagingKey, artifactPath, contentType, leaseCts.Token);

            await PublishAsync(outputId, owner, projectId, frozen, stagingKey, fileName, bytes, leaseCts.Token);
        }
        catch (OperationCanceledException)
        {
            using var cleanup = new CancellationTokenSource(CleanupBudget);
            try
            {
                await OutputLease.ReleaseAsync(Outputs, outputId, owner, Now, cleanup.Token)
                    .WaitAsync(CleanupBudget, CancellationToken.None);
            }
            catch (Exception releaseError)
            {
                logger.LogWarning("Could not release output lease ({ErrorType})", releaseError.GetType().Name);
            }
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("Output job failed ({ErrorType})", exception.GetType().Name);
            using var cleanup = new CancellationTokenSource(CleanupBudget);
            try
            {
                await OutputLease.MarkErrorAsync(Outputs, outputId, owner, Now, cleanup.Token)
                    .WaitAsync(CleanupBudget, CancellationToken.None);
            }
            catch (Exception markError)
            {
                logger.LogWarning("Could not mark output error ({ErrorType})", markError.GetType().Name);
            }
            throw;
        }
        finally
        {
            leaseCts.Cancel();
            // Renewal can fault (Mongo outage) or hang; cancellation and
            // cleanup must still run, and must not mask the original error.
            try
            {
                await renewal.WaitAsync(CleanupBudget, CancellationToken.None);
            }
            catch (Exception renewError)
            {
                logger.LogWarning("Output renewal stopped ({ErrorType})", renewError.GetType().Name);
            }

            using var cleanup = new CancellationTokenSource(CleanupBudget);
            if (stagingKey is not null)
            {
                try
                {
                    await storage.DeleteAsync(stagingKey, cleanup.Token)
                        .WaitAsync(CleanupBudget, CancellationToken.None);
                }
                catch (Exception) { }
            }
            if (runDirectory is not null)
            {
                try { Directory.Delete(runDirectory, recursive: true); }
                catch (Exception) { }
            }
        }
    }

    private async Task RenewLoopAsync(
        ObjectId outputId, string owner, CancellationTokenSource lease, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(RenewInterval, ct);
                if (BeforeRenewAsync is not null)
                    await BeforeRenewAsync(ct);
                if (!await OutputLease.RenewAsync(Outputs, outputId, owner, Now, ct))
                {
                    // The executor no longer owns this output; stop all further IO.
                    await lease.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A transient (or permanent) renewal failure is a lost lease:
                // cancel the job's IO rather than continuing without a fence.
                logger.LogWarning("Output lease renewal failed ({ErrorType})", exception.GetType().Name);
                try { await lease.CancelAsync(); }
                catch (Exception) { }
                return;
            }
        }
    }

    private async Task<FreezeResult> FreezeAsync(
        ObjectId outputId,
        string owner,
        ObjectId projectId,
        long? expectedRevision,
        string runDirectory,
        CancellationToken ct)
    {
        await using var session = await labels.EnterAsync(
            labelContext, projectId, recovery: false, expectedRevision, ct);
        var project = session.Project;
        if (project.Flag("cip")) throw new InvalidOperationException("Project clear is in progress.");
        OutputObjectCleanup.EnsureDefaultStorageSource(project);

        var output = await Outputs.Find(new BsonDocument("_id", outputId)).FirstOrDefaultAsync(ct)
                     ?? throw new InvalidOperationException("Output disappeared.");
        if (!await IsCurrentAsync(output, owner, ct)) throw LostOwnership();
        var target = await LoadTargetAsync(projectId, output, ct);
        if (!await OutputLease.SetStatusAsync(Outputs, outputId, owner,
                OutputStatus.TranslationOutputting, Now, ct))
            throw LostOwnership();

        var frozen = await BuildSnapshotAsync(project, target, output, runDirectory, ct);
        return frozen with { Revision = session.Revision };
    }

    private async Task PublishAsync(
        ObjectId outputId,
        string owner,
        ObjectId projectId,
        FreezeResult frozen,
        string stagingKey,
        string fileName,
        long bytes,
        CancellationToken ct)
    {
        await using var session = await labels.EnterAsync(
            labelContext, projectId, recovery: false, frozen.Revision, ct);
        var project = session.Project;
        if (project.Flag("cip")) throw new InvalidOperationException("Project clear is in progress.");
        OutputObjectCleanup.EnsureDefaultStorageSource(project);

        var output = await Outputs.Find(new BsonDocument("_id", outputId)).FirstOrDefaultAsync(ct)
                     ?? throw new InvalidOperationException("Output disappeared.");
        if (!await IsCurrentAsync(output, owner, ct)) throw LostOwnership();
        if (!TryObjectId(output.GetValue("p", BsonNull.Value), out var outputProject)
            || outputProject != projectId)
            throw LostOwnership();
        _ = await LoadTargetAsync(projectId, output, ct);
        if (BeforePublishVerifyAsync is not null)
            await BeforePublishVerifyAsync(ct);
        await VerifyFrozenPointersAsync(projectId, frozen.Manifest, ct);

        var finalKey = cleanup.OutputPrefix + outputId + "/" + fileName;
        await storage.CopyAsync(stagingKey, finalKey, ct);

        var guard = OutputLease.PublishFilter(outputId, owner, Now);
        if (!await OutputAccounting.RecordArtifactAsync(
                store, outputId, bytes, fileName, Now, guard, ct))
            throw LostOwnership();
        if (AfterArtifactAsync is not null)
            await AfterArtifactAsync(ct);

        // The project ledger is applied before success is visible. If this
        // process dies here the output remains retryable; a replay recomputes
        // the same total and never increments twice.
        if (!await OutputAccounting.ApplyProjectAccountingAsync(store, projectId, ct))
            throw new InvalidOperationException("Project disappeared during export.");
        if (AfterLedgerAsync is not null)
            await AfterLedgerAsync(ct);

        if (!await OutputAccounting.MarkSucceededAsync(
                store, outputId, fileName, Now,
                OutputLease.PublishFilter(outputId, owner, Now), ct))
            throw LostOwnership();
    }

    private async Task<BsonDocument> LoadTargetAsync(
        ObjectId projectId, BsonDocument output, CancellationToken ct)
    {
        if (!TryObjectId(output.GetValue("t", BsonNull.Value), out var targetId))
            throw new InvalidOperationException("Output target is invalid.");
        var target = await Targets.Find(new BsonDocument
        {
            ["_id"] = targetId,
            ["t"] = projectId
        }).FirstOrDefaultAsync(ct);
        return target ?? throw new InvalidOperationException("Target disappeared.");
    }

    private async Task<bool> IsCurrentAsync(BsonDocument output, string owner, CancellationToken ct)
    {
        var current = await Outputs.Find(new BsonDocument
        {
            ["_id"] = output["_id"],
            ["job_owner"] = owner,
            ["job_run_token"] = owner,
            ["job_lease_until"] = new BsonDocument("$gt", Now),
            ["s"] = new BsonDocument("$ne", OutputStatus.Succeeded)
        }).FirstOrDefaultAsync(ct);
        return current is not null;
    }

    private async Task<FreezeResult> BuildSnapshotAsync(
        BsonDocument project,
        BsonDocument target,
        BsonDocument output,
        string runDirectory,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var targetId = target["_id"].AsObjectId;
        var include = ReadIds(output.GetValue("file_ids_include", BsonNull.Value));
        var exclude = ReadIds(output.GetValue("file_ids_exclude", BsonNull.Value));
        var filter = OutputFileSelection.Filter(projectId, include, exclude);
        var anyManual = await Files.Find(OutputFileSelection.ManualProbe(filter))
            .Limit(1)
            .FirstOrDefaultAsync(ct) is not null;
        var files = await Files.Find(filter).Sort(OutputFileSelection.Sort(false)).ToListAsync(ct);
        if (anyManual)
        {
            // Python's Mongo sort puts null/missing mo first. The required
            // order is dn, t, mo (nulls last), sn, _id, so sort explicitly
            // rather than globally partitioning the manual files.
            files = files
                .OrderBy(file => file.Text("dn"), StringComparer.Ordinal)
                .ThenBy(file => file.Integer("t"))
                .ThenBy(file => file.GetValue("mo", BsonNull.Value).IsBsonNull
                    ? long.MaxValue
                    : file.GetValue("mo").ToInt64())
                .ThenBy(file => file.Text("sn"), StringComparer.Ordinal)
                .ThenBy(file => file["_id"].AsObjectId)
                .ToList();
        }
        var exportNames = LabelPlusWriter.ExportFileNames(files);
        var ancestors = await AncestorNamesAsync(files, ct);

        var team = project.GetValue("t", BsonNull.Value).IsObjectId
            ? await Teams.Find(new BsonDocument("_id", project.GetValue("t").AsObjectId)).FirstOrDefaultAsync(ct)
            : null;
        var page = LabelPlusWriter.ResolveStaffPage(
            NumericOrNull(project.GetValue("slp", BsonNull.Value)),
            NumericOrNull(team?.GetValue("slp", BsonNull.Value) ?? BsonNull.Value));
        // ProjectMember.objects(project=..., status="active") has no explicit
        // order; keep Mongo's natural order for the staff block.
        var members = await Members.Find(new BsonDocument
        {
            ["project"] = projectId,
            ["status"] = "active"
        }).ToListAsync(ct);
        var staffBlock = LabelPlusWriter.StaffBlock(members);
        var staffIndex = staffBlock is null
            ? -1
            : LabelPlusWriter.StaffTargetIndex(files.Count, page);
        var staffFirst = staffBlock is not null && page > 0;

        var translationsPath = Path.Combine(runDirectory, "translations.txt");
        await using (var writer = new StreamWriter(translationsPath, append: false, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(LabelPlusWriter.Header.AsMemory(), ct);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var fileId = file["_id"].AsObjectId;
                var displayName = exportNames.TryGetValue(fileId, out var exportName)
                    ? exportName
                    : AncestorPath(file, ancestors) + file.Text("n");
                await writer.WriteAsync(LabelPlusWriter.FileHeader(displayName).AsMemory(), ct);
                if (index == staffIndex && staffFirst)
                    await writer.WriteAsync(staffBlock!.AsMemory(), ct);

                var sourceNumber = 1;
                // Python File.sources() orders by rank only. Do not add an
                // _id tie-break here: equal-rank rows must keep the same
                // natural order as the legacy exporter.
                var sourceOptions = new FindOptions<BsonDocument, BsonDocument>
                {
                    Sort = new BsonDocument("r", 1),
                    BatchSize = BatchSize
                };
                using var cursor = await Sources.FindAsync(new BsonDocument("f", fileId), sourceOptions, ct);
                while (await cursor.MoveNextAsync(ct))
                {
                    var batch = cursor.Current.ToList();
                    var best = await BestTranslationsAsync(
                        batch.Select(source => source["_id"].AsObjectId), targetId, ct);
                    foreach (var source in batch)
                    {
                        var x = source.GetValue("x", new BsonInt32(0));
                        var y = source.GetValue("y", new BsonInt32(0));
                        var positionValue = source.GetValue("p", BsonNull.Value);
                        var position = positionValue.IsNumeric ? positionValue.ToInt32() : 1;
                        await writer.WriteAsync(
                            LabelPlusWriter.SourceEntry(sourceNumber++, x, y, position).AsMemory(), ct);
                        best.TryGetValue(source["_id"].AsObjectId, out var translation);
                        await writer.WriteAsync(
                            LabelPlusWriter.TranslationLine(LabelPlusWriter.TranslationContent(translation))
                                .AsMemory(), ct);
                    }
                }
                if (index == staffIndex && !staffFirst)
                    await writer.WriteAsync(staffBlock!.AsMemory(), ct);
            }
        }

        var downloadName = await DownloadNameAsync(project, target, ct);
        var languageCode = await LanguageCodeAsync(target, ct);
        var projectJson = await BuildProjectJsonAsync(project, output["_id"].AsObjectId, languageCode, ct);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "project.json"), projectJson,
            new UTF8Encoding(false), ct);

        var manifestFiles = new BsonArray(files.Select(file =>
        {
            var id = file["_id"].AsObjectId;
            return (BsonValue)new BsonDocument
            {
                ["id"] = id.ToString(),
                ["name"] = file.Text("n"),
                ["export_name"] = exportNames.TryGetValue(id, out var name) ? name : file.Text("n"),
                ["save_name"] = file.Text("sa")
            };
        }));
        var manifest = new BsonDocument
        {
            ["format_version"] = 1,
            ["output_id"] = output["_id"].AsObjectId.ToString(),
            ["project_id"] = projectId.ToString(),
            ["target_id"] = targetId.ToString(),
            ["type"] = output.Integer("ty"),
            ["download_name"] = downloadName,
            ["language_code"] = languageCode,
            ["files"] = manifestFiles
        };
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "manifest.json"),
            manifest.ToJson(), new UTF8Encoding(false), ct);

        return new FreezeResult
        {
            Manifest = manifest,
            DownloadName = downloadName,
            Type = (int)output.Integer("ty"),
            Revision = 0
        };
    }

    private async Task<Dictionary<ObjectId, string>> AncestorNamesAsync(
        IReadOnlyList<BsonDocument> files,
        CancellationToken ct)
    {
        var ids = files
            .SelectMany(file => ReadIds(file.GetValue("a", BsonNull.Value)))
            .Distinct()
            .ToList();
        var names = new Dictionary<ObjectId, string>();
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await Files.Find(new BsonDocument
            {
                ["_id"] = new BsonDocument("$in", new BsonArray(chunk))
            }).ToListAsync(ct);
            foreach (var row in rows) names[row["_id"].AsObjectId] = row.Text("n");
        }
        return names;
    }

    private static string AncestorPath(BsonDocument file, Dictionary<ObjectId, string> ancestors)
    {
        var ids = ReadIds(file.GetValue("a", BsonNull.Value));
        if (ids.Count == 0) return "";
        return string.Join("/", ids.Select(id => ancestors.TryGetValue(id, out var name) ? name : "")) + "/";
    }

    private async Task<Dictionary<ObjectId, BsonDocument>> BestTranslationsAsync(
        IEnumerable<ObjectId> sourceIds,
        ObjectId targetId,
        CancellationToken ct)
    {
        var ids = sourceIds.ToList();
        if (ids.Count == 0) return [];
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["o"] = new BsonDocument("$in", new BsonArray(ids)),
                ["t"] = targetId
            }),
            new BsonDocument("$sort", new BsonDocument
            {
                ["s"] = -1,
                ["p"] = -1,
                ["e"] = -1,
                ["ct"] = -1,
                ["_id"] = -1
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = "$o",
                ["doc"] = new BsonDocument("$first", "$$ROOT")
            }),
            new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$doc"))
        };
        var rows = await Translations.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
        var best = new Dictionary<ObjectId, BsonDocument>();
        foreach (var row in rows)
        {
            var sourceId = row.GetValue("o", BsonNull.Value).AsObjectId;
            best[sourceId] = row;
        }
        return best;
    }

    private async Task<string> DownloadNameAsync(
        BsonDocument project, BsonDocument target, CancellationToken ct)
    {
        var set = project.GetValue("ps", BsonNull.Value).IsObjectId
            ? await store.Collection("project_set")
                .Find(new BsonDocument("_id", project.GetValue("ps").AsObjectId))
                .FirstOrDefaultAsync(ct)
            : null;
        var setDefault = set is null || set.Flag("d");
        var setPrefix = setDefault ? "" : LabelPlusWriter.SafeDownloadName(set!.Text("n")) + " - ";
        var languageName = await LanguageNameAsync(target, ct);
        return setPrefix + LabelPlusWriter.SafeDownloadName(project.Text("n")) + " - " + languageName;
    }

    private async Task<string> LanguageNameAsync(BsonDocument target, CancellationToken ct)
    {
        var language = await LoadLanguageAsync(target, ct);
        var chinese = language?.Text("c") ?? "";
        if (chinese.Length > 0) return chinese;
        var english = language?.Text("e") ?? "";
        return english.Length > 0 ? english : language?.Text("co") ?? "";
    }

    private async Task<string> LanguageCodeAsync(BsonDocument target, CancellationToken ct) =>
        (await LoadLanguageAsync(target, ct))?.Text("co") ?? "";

    private async Task<BsonDocument?> LoadLanguageAsync(BsonDocument target, CancellationToken ct)
    {
        var languageId = target.GetValue("l", BsonNull.Value);
        if (!languageId.IsObjectId) return null;
        return await Languages.Find(new BsonDocument("_id", languageId.AsObjectId)).FirstOrDefaultAsync(ct);
    }

    private async Task<string> BuildProjectJsonAsync(
        BsonDocument project,
        ObjectId outputId,
        string outputLanguage,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var roleCode = "translator";
        var roleId = project.GetValue("dr", BsonNull.Value);
        if (roleId.IsObjectId)
        {
            var role = await store.Collection("project_role")
                .Find(new BsonDocument("_id", roleId.AsObjectId)).FirstOrDefaultAsync(ct);
            var code = role?.Text("m_o") ?? "";
            if (code.Length > 0) roleCode = code;
        }

        // Python project.targets() has no explicit order; preserve natural
        // collection order for the target_languages array.
        var targets = await Targets.Find(new BsonDocument("t", projectId)).ToListAsync(ct);
        var languageIds = targets.Select(target => target.GetValue("l", BsonNull.Value))
            .Where(value => value.IsObjectId)
            .Select(value => value.AsObjectId)
            .Distinct()
            .ToList();
        var sourceLanguageId = project.GetValue("ol", BsonNull.Value);
        if (sourceLanguageId.IsObjectId) languageIds.Add(sourceLanguageId.AsObjectId);
        var languageRows = await Languages.Find(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(languageIds.Distinct()))
        }).ToListAsync(ct);
        var codes = languageRows.ToDictionary(row => row["_id"].AsObjectId, row => row.Text("co"));
        var targetCodes = targets.Select(target =>
        {
            var id = target.GetValue("l", BsonNull.Value);
            return id.IsObjectId && codes.TryGetValue(id.AsObjectId, out var code) ? code : "";
        }).ToList();
        var sourceCode = sourceLanguageId.IsObjectId && codes.TryGetValue(sourceLanguageId.AsObjectId, out var source)
            ? source
            : "";

        var fields = ProjectOutputJson
            .Fields(project, roleCode, sourceCode, targetCodes)
            .ToList();
        fields.Add(new("output_id", outputId.ToString()));
        fields.Add(new("output_language", outputLanguage));
        return PythonJson.Serialize(fields);
    }

    private async Task<List<string>> DownloadImagesAsync(
        BsonDocument manifest,
        string runDirectory,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var imagesRoot = Path.Combine(runDirectory, "images");
        Directory.CreateDirectory(imagesRoot);
        var rootFull = Path.GetFullPath(imagesRoot) + Path.DirectorySeparatorChar;
        foreach (var file in manifest.GetValue("files", new BsonArray()).AsBsonArray)
        {
            ct.ThrowIfCancellationRequested();
            var document = file.AsBsonDocument;
            var fileId = document.Text("id");
            var name = document.Text("name");
            var exportName = LabelPlusWriter.SafeEntryPath(document.Text("export_name"));
            var relative = Path.Combine("images", exportName.Replace('/', Path.DirectorySeparatorChar));
            var local = Path.GetFullPath(Path.Combine(runDirectory, relative));
            if (!local.StartsWith(rootFull, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe zip entry path.");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);

            try
            {
                using var download = await storage.GetAsync(
                    cleanup.FilePrefix + document.Text("save_name"), ct);
                await using var target = new FileStream(local, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);
                await download.Content.CopyToAsync(target, ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(local);
                throw;
            }
            catch (FileNotFoundException)
            {
                TryDelete(local);
                errors.Add($"File {name}<{fileId}> is not found in server.\r\n");
            }
            catch (AmazonS3Exception exception) when (IsNotFound(exception))
            {
                TryDelete(local);
                errors.Add($"File {name}<{fileId}> is not found in server.\r\n");
            }
            catch (Exception)
            {
                TryDelete(local);
                errors.Add($"File {name}<{fileId}> download error.\r\n");
            }
        }
        return errors;
    }

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || exception.ErrorCode is "404" or "NoSuchKey" or "NotFound";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private async Task WriteErrorsAsync(
        string runDirectory,
        ObjectId projectId,
        FreezeResult frozen,
        List<string> errors,
        CancellationToken ct)
    {
        var targetId = frozen.Manifest.Text("target_id");
        var outputId = frozen.Manifest.Text("output_id");
        var projectName = frozen.Manifest.Text("project_name");
        var project = await store.Collection("project")
            .Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct);
        projectName = project?.Text("n") ?? projectName;
        var text =
            $"Project Name: {projectName}\r\n" +
            $"Project ID:   {projectId}\r\n" +
            $"Target ID:    {targetId}\r\n" +
            $"Output ID:    {outputId}\r\n" +
            "------------------------\r\n" +
            string.Concat(errors);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "errors.txt"), text,
            new UTF8Encoding(false), ct);
    }

    private static async Task CreateZipAsync(
        string runDirectory, string zipPath, CancellationToken ct)
    {
        var entries = new List<string> { "translations.txt", "project.json" };
        if (File.Exists(Path.Combine(runDirectory, "errors.txt"))) entries.Add("errors.txt");
        var imagesRoot = Path.Combine(runDirectory, "images");
        if (Directory.Exists(imagesRoot))
        {
            foreach (var file in Directory.EnumerateFiles(imagesRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(runDirectory, file).Replace('\\', '/');
                entries.Add(relative);
            }
        }
        await OutputArchive.CreateAsync(runDirectory, zipPath, entries, ct);
    }

    private async Task<long> UploadAsync(string key, string path, string contentType, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true);
        await storage.PutAsync(key, stream, contentType, ct);
        var info = await storage.HeadAsync(key, ct);
        return info?.Size ?? new FileInfo(path).Length;
    }

    private async Task VerifyFrozenPointersAsync(
        ObjectId projectId, BsonDocument manifest, CancellationToken ct)
    {
        var files = manifest.GetValue("files", new BsonArray()).AsBsonArray;
        foreach (var chunk in files.Chunk(BatchSize))
        {
            var expected = chunk.Select(item => item.AsBsonDocument).ToList();
            var ids = expected.Select(item => ObjectId.Parse(item.Text("id"))).ToList();
            var rows = await Files.Find(new BsonDocument
            {
                ["_id"] = new BsonDocument("$in", new BsonArray(ids))
            }).ToListAsync(ct);
            var byId = rows.ToDictionary(row => row["_id"].AsObjectId);
            foreach (var item in expected)
            {
                if (!byId.TryGetValue(ObjectId.Parse(item.Text("id")), out var row))
                    throw new ApiError("OutputStale", 1,
                        "导出期间源文件已变化，请重试导出", 409);
                var rowProject = row.GetValue("p", BsonNull.Value);
                if (!rowProject.IsObjectId || rowProject.AsObjectId != projectId
                    || !string.Equals(row.Text("sa"), item.Text("save_name"), StringComparison.Ordinal))
                    throw new ApiError("OutputStale", 1,
                        "导出期间源文件已变化，请重试导出", 409);
            }
        }
    }

    private string CreateRunDirectory(ObjectId outputId, string owner)
    {
        var root = configuration["Outputs:TempPath"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "shoreline-outputs");
        var directory = Path.Combine(root, outputId + "-" + owner);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task FailUnclaimedAsync(ObjectId outputId, string reason, CancellationToken ct)
    {
        await Outputs.UpdateOneAsync(
            new BsonDocument
            {
                ["_id"] = outputId,
                ["s"] = new BsonDocument("$ne", OutputStatus.Succeeded),
                ["$or"] = new BsonArray
                {
                    new BsonDocument("job_lease_until", BsonNull.Value),
                    new BsonDocument("job_lease_until", new BsonDocument("$lt", Now))
                }
            },
            new BsonDocument("$set", new BsonDocument
            {
                ["s"] = OutputStatus.Error,
                ["job_error"] = reason
            }),
            cancellationToken: ct);
    }

    private static InvalidOperationException LostOwnership() =>
        new("Output execution lease was lost.");

    private static bool TryObjectId(BsonValue value, out ObjectId id)
    {
        if (value.IsObjectId)
        {
            id = value.AsObjectId;
            return true;
        }
        return ObjectId.TryParse(value.ToString(), out id);
    }

    private static List<ObjectId> ReadIds(BsonValue value) =>
        value is BsonArray array
            ? array.Select(item => item.IsObjectId ? item.AsObjectId : ObjectId.Parse(item.ToString())).ToList()
            : [];

    private static int? NumericOrNull(BsonValue value) => value.IsNumeric ? value.ToInt32() : null;

    private static double Number(BsonValue value) => value.IsNumeric ? value.ToDouble() : 0.0;

    private sealed record FreezeResult
    {
        public required BsonDocument Manifest { get; init; }
        public required string DownloadName { get; init; }
        public required int Type { get; init; }
        public required long Revision { get; init; }
    }
}
