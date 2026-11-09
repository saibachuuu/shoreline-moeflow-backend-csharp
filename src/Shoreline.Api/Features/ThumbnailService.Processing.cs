using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;
using Shoreline.Imaging;

namespace Shoreline.Api.Features;

public sealed partial class ThumbnailService
{
    private sealed class SourceMissingException : Exception
    {
    }

    private async Task RunCoreAsync(BsonDocument payload, int? attemptOverride, CancellationToken ct)
    {
        if (!TryReadPayload(payload, out var fileId, out var projectId, out var saveName, out var runId, out var revision))
        {
            return;
        }

        var tempDirectory = Path.Combine(_tempRoot, runId);
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!await MarkGeneratingAsync(fileId, projectId, saveName, runId, revision, ct))
            {
                return;
            }

            Directory.CreateDirectory(tempDirectory);
            var sourcePath = Path.Combine(tempDirectory, "source");
            await DownloadSourceAsync(saveName, sourcePath, ct);
            ct.ThrowIfCancellationRequested();

            var processed = _processor.Process(sourcePath, tempDirectory, ct);
            await UploadStagingAsync(processed, runId, ct);
            ct.ThrowIfCancellationRequested();

            if (!await PublishAsync(
                    fileId, projectId, saveName, runId, revision,
                    processed.CoverBytes, processed.ResampleBytes, ct))
            {
                return;
            }
        }
        catch (SourceMissingException)
        {
            await FailCurrentAsync(fileId, projectId, saveName, runId, SourceMissingError);
        }
        catch (ImageProcessingException error)
        {
            await FailCurrentAsync(fileId, projectId, saveName, runId, SafeProcessingError(error));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(
                "Thumbnail task failed for file {FileId} ({ErrorType})",
                fileId, error.GetType().Name);

            var terminal = await HandleTransientAsync(
                payload, fileId, projectId, saveName, runId, attemptOverride, ct);
            if (!terminal) throw;
        }
        finally
        {
            await CleanupAsync(runId, tempDirectory);
        }
    }

    private static bool TryReadPayload(
        BsonDocument payload,
        out ObjectId fileId,
        out ObjectId projectId,
        out string saveName,
        out string runId,
        out long revision)
    {
        fileId = default;
        projectId = default;
        saveName = string.Empty;
        runId = string.Empty;
        revision = 0;

        if (!payload.TryGetValue("file_id", out var file) || !file.IsString
            || !ObjectId.TryParse(file.AsString, out fileId)) return false;
        if (!payload.TryGetValue("project_id", out var project) || !project.IsString
            || !ObjectId.TryParse(project.AsString, out projectId)) return false;
        if (!payload.TryGetValue("save_name", out var save) || !save.IsString) return false;
        if (!payload.TryGetValue("run_id", out var run) || !run.IsString || run.AsString.Length == 0) return false;

        saveName = save.AsString;
        runId = run.AsString;
        if (payload.TryGetValue("label_revision", out var labelRevision) && labelRevision.IsNumeric)
        {
            revision = labelRevision.ToInt64();
        }
        return true;
    }

    /// <summary>
    /// Short critical section: fence the save_name, claim the run, and set
    /// GENERATING. Downloads and image decoding happen after this lease is
    /// released.
    /// </summary>
    private async Task<bool> MarkGeneratingAsync(
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        long revision,
        CancellationToken ct)
    {
        await using var session = await _labels.EnterAsync(
            new LabelRequestContext(), projectId, recovery: false, expectedRevision: null, ct);

        var project = session.Project;
        if (project.Integer("label_revision", 0) != revision) return false;
        if (session.Recovery) return false;
        if (HasStoragePool(project))
        {
            await FailUnderLockAsync(fileId, projectId, saveName, string.Empty, StoragePoolError, ct);
            return false;
        }

        var file = await Files.Find(new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId
        }).FirstOrDefaultAsync(ct);
        if (file is null || file.Text("sa") != saveName) return false;

        try
        {
            session.AssertEpoch(file);
        }
        catch (ApiError)
        {
            return false;
        }

        if (file.Integer("th") == ThumbnailSucceeded) return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        var currentRun = file.Text("thumbnail_run");
        var currentRunAt = Date(file, "thumbnail_run_at");
        var anotherRunIsActive =
            file.Integer("th") == ThumbnailGenerating
            && currentRun.Length > 0
            && !string.Equals(currentRun, runId, StringComparison.Ordinal)
            && currentRunAt is not null
            && currentRunAt.Value > now - _runLease;
        if (anotherRunIsActive) return false;

        var filter = new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["sa"] = saveName
        };
        var update = Builders<BsonDocument>.Update
            .Set("th", ThumbnailGenerating)
            .Set("the", string.Empty)
            .Set("tht", now)
            .Set("thumbnail_run", runId)
            .Set("thumbnail_run_at", now);
        var result = await Files.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    private async Task DownloadSourceAsync(string saveName, string destination, CancellationToken ct)
    {
        if (saveName.Length == 0) throw new SourceMissingException();

        var key = $"files/{saveName}";
        var info = await _storage.HeadAsync(key, ct);
        if (info is null) throw new SourceMissingException();
        if (info.Size > _maxSourceBytes)
        {
            throw new ImageProcessingException(
                "source image exceeds the configured byte limit",
                ImageProcessingFailure.SourceTooLarge);
        }

        try
        {
            using var download = await _storage.GetAsync(key, ct);
            await using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous);
            await download.Content.CopyToAsync(output, 81920, ct);
        }
        catch (FileNotFoundException)
        {
            throw new SourceMissingException();
        }

        if (new FileInfo(destination).Length == 0) throw new SourceMissingException();
    }

    private async Task UploadStagingAsync(
        ImageProcessingResult processed,
        string runId,
        CancellationToken ct)
    {
        await PutFileAsync(StagingCoverKey(runId), processed.CoverPath, ct);
        await PutFileAsync(StagingResampleKey(runId), processed.ResamplePath, ct);
    }

    private async Task PutFileAsync(string key, string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous);
        await _storage.PutAsync(key, stream, "image/webp", ct);
    }

    /// <summary>
    /// Final critical section. All object copies and both byte counters are
    /// committed while the project lease is held, and SUCCEEDED is a CAS on the
    /// exact run id. A later re-upload or another worker can therefore never be
    /// overwritten by a stale run.
    /// </summary>
    private async Task<bool> PublishAsync(
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        long revision,
        long coverBytes,
        long resampleBytes,
        CancellationToken ct)
    {
        await using var session = await _labels.EnterAsync(
            new LabelRequestContext(), projectId, recovery: false, expectedRevision: null, ct);

        var project = session.Project;
        if (project.Integer("label_revision", 0) != revision) return false;
        if (session.Recovery) return false;
        if (HasStoragePool(project)) return false;

        var file = await Files.Find(new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId
        }).FirstOrDefaultAsync(ct);
        if (file is null || file.Text("sa") != saveName) return false;

        try
        {
            session.AssertEpoch(file);
        }
        catch (ApiError)
        {
            return false;
        }

        if (file.Integer("th") != ThumbnailGenerating
            || !string.Equals(file.Text("thumbnail_run"), runId, StringComparison.Ordinal))
        {
            return false;
        }

        await _storage.CopyAsync(StagingCoverKey(runId), FinalCoverKey(saveName), ct);
        await _storage.CopyAsync(StagingResampleKey(runId), FinalResampleKey(saveName), ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var runFilter = new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["sa"] = saveName,
            ["th"] = ThumbnailGenerating,
            ["thumbnail_run"] = runId
        };
        var fileUpdate = Builders<BsonDocument>.Update
            .Set("cover_bytes", new BsonInt64(coverBytes))
            .Set("resample_bytes", new BsonInt64(resampleBytes))
            .Set("the", string.Empty)
            .Set("tht", now);
        var fileChanged = await Files.UpdateOneAsync(runFilter, fileUpdate, cancellationToken: ct);
        if (fileChanged.MatchedCount == 0) return false;

        await RecomputeProjectDerivedAsync(projectId, project, ct);
        var finished = await Files.UpdateOneAsync(
            runFilter,
            Builders<BsonDocument>.Update
                .Set("th", ThumbnailSucceeded)
                .Set("the", string.Empty)
                .Set("tht", _clock.GetUtcNow().UtcDateTime)
                .Unset("thumbnail_run")
                .Unset("thumbnail_run_at"),
            cancellationToken: ct);
        return finished.MatchedCount == 1;
    }

    /// <summary>
    /// Rebuild the project's derived byte totals from the file rows instead of
    /// adding each generated size again. This makes a replay after a crash
    /// between the file and project writes idempotent.
    /// </summary>
    private async Task RecomputeProjectDerivedAsync(
        ObjectId projectId,
        BsonDocument project,
        CancellationToken ct)
    {
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(new[]
        {
            new BsonDocument("$match", new BsonDocument("p", projectId)),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = BsonNull.Value,
                ["cover"] = new BsonDocument("$sum", "$cover_bytes"),
                ["resample"] = new BsonDocument("$sum", "$resample_bytes")
            })
        });

        var rows = await Files.Aggregate(pipeline, cancellationToken: ct).ToListAsync(ct);
        var totals = rows.FirstOrDefault();
        var cover = totals is null ? 0 : totals.Integer("cover");
        var resample = totals is null ? 0 : totals.Integer("resample");

        var oldCover = project.Integer("cover_bytes");
        var oldResample = project.Integer("resample_bytes");
        var deltaKb = RoundKb(cover, resample) - RoundKb(oldCover, oldResample);

        var update = Builders<BsonDocument>.Update
            .Set("cover_bytes", new BsonInt64(cover))
            .Set("resample_bytes", new BsonInt64(resample));
        if (deltaKb != 0)
        {
            update = update.Inc("fs", new BsonInt64(deltaKb));
        }

        var changed = await Projects.UpdateOneAsync(
            new BsonDocument("_id", projectId), update, cancellationToken: ct);
        if (changed.MatchedCount == 0)
        {
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
        }
    }

    private async Task<bool> HandleTransientAsync(
        BsonDocument payload,
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        int? attemptOverride,
        CancellationToken ct)
    {
        var attempt = attemptOverride ?? await CurrentAttemptAsync(payload, ct);
        if (attempt < _maxAttempts) return false;

        await FailCurrentAsync(fileId, projectId, saveName, runId, TransientFailureError);
        return true;
    }

    private async Task<int> CurrentAttemptAsync(BsonDocument payload, CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            ["kind"] = JobKind,
            ["state"] = "running",
            ["payload.file_id"] = payload.Text("file_id"),
            ["payload.save_name"] = payload.Text("save_name")
        };
        var job = await _store.Collection("csharp_job")
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("attempts"))
            .FirstOrDefaultAsync(ct);
        return job is null ? 1 : Math.Max(1, (int)job.Integer("attempts", 1));
    }

    private async Task FailCurrentAsync(
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        string message)
    {
        await FailAsync(
            fileId, projectId, saveName, runId, message,
            requireRun: runId.Length > 0, CancellationToken.None);
    }

    private async Task FailUnderLockAsync(
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        string message,
        CancellationToken ct)
    {
        await FailAsync(fileId, projectId, saveName, runId, message, requireRun: false, ct);
    }

    private async Task FailAsync(
        ObjectId fileId,
        ObjectId projectId,
        string saveName,
        string runId,
        string message,
        bool requireRun,
        CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["sa"] = saveName
        };
        if (requireRun)
        {
            filter["th"] = ThumbnailGenerating;
            filter["thumbnail_run"] = runId;
        }
        else
        {
            // The storage-pool branch runs before this task becomes GENERATING,
            // so it must also be able to fail a QUEUING row without clobbering
            // a previously successful result.
            filter["th"] = new BsonDocument("$ne", ThumbnailSucceeded);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await Files.UpdateOneAsync(
                filter,
                Builders<BsonDocument>.Update
                    .Set("th", ThumbnailFailed)
                    .Set("the", Truncate(message))
                    .Set("tht", _clock.GetUtcNow().UtcDateTime)
                    .Unset("thumbnail_run")
                    .Unset("thumbnail_run_at"),
                cancellationToken: timeout.Token);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(
                "Could not record terminal thumbnail failure for file {FileId} ({ErrorType})",
                fileId, error.GetType().Name);
        }
    }

    private async Task CleanupAsync(string runId, string tempDirectory)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _storage.DeleteAsync(StagingCoverKey(runId), timeout.Token);
            await _storage.DeleteAsync(StagingResampleKey(runId), timeout.Token);
        }
        catch
        {
            // Staging objects are namespaced by run id and never served. A
            // missed cleanup is harmless and must not mask the task result.
        }

        try
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SafeProcessingError(ImageProcessingException error) => error.Failure switch
    {
        ImageProcessingFailure.SourceTooLarge => "source image exceeds the configured byte limit",
        ImageProcessingFailure.FormatNotSupported => "source image format is not supported",
        ImageProcessingFailure.DimensionsExceeded => "source image exceeds the configured dimension limit",
        ImageProcessingFailure.DecodeFailed => "source image could not be decoded",
        _ => "thumbnail generation failed"
    };
}
