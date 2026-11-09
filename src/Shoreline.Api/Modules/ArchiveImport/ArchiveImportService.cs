using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>
/// Queue + worker service. The worker always re-checks the per-project claim and the project label revision before
/// every short label-locked file upload; it never holds ProjectLabelLock across a download or validation.
/// </summary>
public sealed class ArchiveImportService
{
    public const string JobKind = "archive_import";

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialPattern =
        new(@"(?i)(bearer\s+\S+|(?:api[_-]?key|token|secret)\s*[:=]\s*\S+)", RegexOptions.Compiled);

    private readonly IMongoCollection<BsonDocument> _projects;
    private readonly IMongoCollection<BsonDocument> _teams;
    private readonly ArchiveImportTaskStore _tasks;
    private readonly ArchiveImportClaimStore _claims;
    private readonly IArchiveJobQueue _jobs;
    private readonly IArchiveImportTransport _transport;
    private readonly IArchiveProjectUploader _uploader;
    private readonly ArchiveSecrets _secrets;
    private readonly ArchiveImportOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArchiveImportService>? _logger;

    public ArchiveImportService(MongoStore store, ArchiveImportTaskStore tasks, ArchiveImportClaimStore claims,
        IArchiveJobQueue jobs, IArchiveImportTransport transport, IArchiveProjectUploader uploader,
        ArchiveSecrets secrets, ArchiveImportOptions options, TimeProvider clock,
        ILogger<ArchiveImportService>? logger = null)
    {
        _projects = store.Collection("project");
        _teams = store.Collection("team");
        _tasks = tasks;
        _claims = claims;
        _jobs = jobs;
        _transport = transport;
        _uploader = uploader;
        _secrets = secrets;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public Task<BsonDocument?> LatestAsync(ObjectId projectId, CancellationToken ct) =>
        _tasks.LatestAsync(projectId, ct);

    public Task DismissAsync(ObjectId projectId, CancellationToken ct) => _tasks.DismissAsync(projectId, ct);

    /// <summary>
    /// Creates the task, persists it, then enqueues the job. An unknown queue result can never lose the task: the
    /// claim is released and the task is either failed (still queued) or left to the worker.
    /// </summary>
    public async Task<BsonDocument> CreateAsync(BsonDocument project, string? userId, string gid, string token,
        string galleryUrl, CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var taskId = ObjectId.GenerateNewId();
        await EnsureCreateClaimAsync(projectId, taskId, ct);

        var now = Now;
        var task = new BsonDocument
        {
            ["_id"] = taskId,
            ["p"] = projectId,
            ["u"] = userId is not null && ObjectId.TryParse(userId, out var user) ? new BsonObjectId(user) : BsonNull.Value,
            ["g"] = gid,
            ["t"] = token,
            ["gu"] = galleryUrl,
            ["s"] = ArchiveImportStatus.Queued,
            ["st"] = ArchiveImportText.QueuedStage,
            ["n"] = 0,
            ["c"] = 0,
            ["e"] = "",
            ["zu"] = "",
            ["d"] = false,
            ["ct"] = now,
            ["ut"] = now,
            ["lr"] = project.Integer("label_revision", 0),
            ["lo"] = BsonNull.Value,
            ["lu"] = BsonNull.Value,
            ["pi"] = 0,
            ["ic"] = 0,
        };

        try
        {
            await _tasks.InsertAsync(task, ct);
        }
        catch
        {
            await _claims.ReleaseUnownedAsync(projectId, taskId, CancellationToken.None);
            throw;
        }

        try
        {
            await _jobs.EnqueueAsync(JobKind, new BsonDocument
            {
                ["project"] = projectId.ToString(),
                ["taskid"] = taskId.ToString(),
                ["label_revision"] = project.Integer("label_revision", 0),
            }, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger?.LogWarning("archive import enqueue failed: {Type}", error.GetType().Name);
            await _tasks.TryFailNewAsync(taskId, ArchiveImportText.FailedStage, "任务入队失败", CancellationToken.None);
            await _claims.ReleaseUnownedAsync(projectId, taskId, CancellationToken.None);
            throw new ApiError("ArchiveImportQueueError", 500, "归档导入任务加入队列失败，请稍后重试", 500);
        }

        return task;
    }

    public async Task RunAsync(BsonDocument payload, CancellationToken ct)
    {
        var projectId = ArchiveImportIds.Id(payload, "project") ?? ArchiveImportIds.Id(payload, "project_id");
        if (projectId is null) return;

        var taskId = ArchiveImportIds.Id(payload, "taskid") ?? ArchiveImportIds.Id(payload, "task_id");
        BsonDocument? task;
        if (taskId is not null)
        {
            task = await _tasks.FindByIdAsync(taskId.Value, projectId.Value, ct);
        }
        else
        {
            var gid = payload.Text("gid");
            if (gid.Length == 0) gid = payload.Text("g");
            var token = payload.Text("token");
            if (token.Length == 0) token = payload.Text("t");
            if (gid.Length == 0 && token.Length == 0) return;
            task = await _tasks.FindLegacyAsync(projectId.Value, gid, token, ct);
        }
        if (task is null) return;
        if (!ArchiveImportStatus.IsRunning((int)task.Integer("s"))) return;

        long? expectedRevision = null;
        if (payload.TryGetValue("label_revision", out var revision) && revision.IsNumeric)
            expectedRevision = revision.ToInt64();
        else if (task.TryGetValue("lr", out var storedRevision) && storedRevision.IsNumeric)
            expectedRevision = storedRevision.ToInt64();

        var owner = await AcquireWorkerClaimAsync(projectId.Value, task["_id"].AsObjectId, ct);
        if (owner is null) return;

        try
        {
            await ExecuteAsync(owner, expectedRevision, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApiError error) when (error.Error == "LabelBusyError")
        {
            // A concurrent label operation is running; let the job lease retry rather than failing the import.
            throw;
        }
        catch (ArchiveLostException)
        {
            throw;
        }
        catch (ArchiveRetryException)
        {
            throw;
        }
        catch (ArchiveImportFailure failure)
        {
            await FailAsync(owner, failure.Stage, failure.Message);
        }
        catch (Exception error)
        {
            _logger?.LogWarning("archive import failed: {Type}", error.GetType().Name);
            await FailAsync(owner, ArchiveImportText.FailedStage, Sanitize(error.Message));
        }
        finally
        {
            await ReleaseAsync(owner);
        }
    }

    private async Task ExecuteAsync(ArchiveClaimOwner owner, long? expectedRevision, CancellationToken ct)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewClaimLoopAsync(owner, execution);
        try
        {
            await ExecuteCoreAsync(owner, expectedRevision, execution.Token);
        }
        finally
        {
            execution.Cancel();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RenewClaimLoopAsync(ArchiveClaimOwner owner, CancellationTokenSource execution)
    {
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), execution.Token);
                var lease = Now.Add(_options.ClaimLease);
                if (!await _claims.RenewAsync(owner.ProjectId, owner.TaskId, owner.Token, lease, execution.Token))
                {
                    owner.LeaseUntil = DateTime.MinValue;
                    execution.Cancel();
                    return;
                }
                owner.LeaseUntil = lease;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _logger?.LogWarning("archive claim renewal skipped: {Type}", error.GetType().Name);
            execution.Cancel();
        }
    }

    private async Task ExecuteCoreAsync(ArchiveClaimOwner owner, long? expectedRevision, CancellationToken ct)
    {
        var task = await _tasks.FindByIdAsync(owner.TaskId, owner.ProjectId, ct);
        if (task is null || !ArchiveImportStatus.IsRunning((int)task.Integer("s"))) return;

        if ((int)task.Integer("s") == ArchiveImportStatus.Queued)
        {
            if (!await _tasks.TryTransitionToResolvingAsync(owner.TaskId, owner, ct))
                throw new ArchiveLostException("task claim lost");
        }
        else if (!await _tasks.TouchOwnerAsync(owner.TaskId, owner, ct))
        {
            throw new ArchiveLostException("task claim lost");
        }

        task = await _tasks.FindByIdAsync(owner.TaskId, owner.ProjectId, ct)
               ?? throw new ArchiveLostException("task disappeared");
        var project = await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", owner.ProjectId))
            .FirstOrDefaultAsync(ct);
        if (project is null) throw new ArchiveImportFailure("项目不存在", "项目不存在");
        if (project.Integer("st") != 0)
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "项目已完结");
        // TODO(archive-import): 此处 revision 复验与 D1 上传内部锁之间仍有 epoch 变更窗口，
        // 尚未实现“把 expectedRevision 传进 D1 锁内做 CAS 对比”的严格 fencing + 并发竞争测试。
        if (expectedRevision is not null && project.Integer("label_revision", 0) != expectedRevision.Value)
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "项目标签版本已变更，导入已取消");

        var (apiUrl, keys) = await ResolveProviderAsync(project, ct);
        var gid = ArchiveImportIds.StringValue(task.GetValue("g", BsonNull.Value));
        var token = ArchiveImportIds.StringValue(task.GetValue("t", BsonNull.Value));

        string? zipUrl = null;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _tasks.TouchOwnerAsync(owner.TaskId, owner, ct))
                throw new ArchiveLostException("task claim lost");
            try
            {
                zipUrl = await _transport.ResolveArchiveUrlAsync(apiUrl, gid, token, key, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger?.LogWarning("archive resolve key skipped: {Type}", error.GetType().Name);
            }
            if (!string.IsNullOrEmpty(zipUrl)) break;
        }
        if (string.IsNullOrEmpty(zipUrl))
            throw new ArchiveImportFailure("解析归档链接失败",
                "无法从档案 API 获取归档链接（所有 key 均已尝试）");

        var tempDir = CreateTempDirectory();
        try
        {
            await StageAsync(owner, ArchiveImportStatus.Downloading, ArchiveImportText.DownloadingStage, ct);
            var zipPath = Path.Combine(tempDir, "archive.zip");
            try
            {
                await _transport.DownloadAsync(zipUrl, zipPath, _options.MaxZipBytes, ct);
            }
            catch (ArchiveDownloadLimitException error)
            {
                throw new ArchiveImportFailure(ArchiveImportText.DownloadingStage, error.Message);
            }
            catch (ArchiveImportFailure)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw new ArchiveImportFailure(ArchiveImportText.DownloadingStage, "归档下载异常");
            }
            catch (IOException error)
            {
                throw new ArchiveImportFailure(ArchiveImportText.DownloadingStage,
                    "本地临时目录不可用：" + Sanitize(error.Message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ArchiveImportFailure(ArchiveImportText.DownloadingStage, Sanitize(error.Message));
            }

            await StageAsync(owner, ArchiveImportStatus.Validating, ArchiveImportText.ValidatingStage, ct);
            using var archive = OpenArchive(zipPath);
            IReadOnlyList<ArchiveZipEntry> entries;
            try
            {
                entries = await archive.ValidateAndListAsync(_options, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ArchiveImportFailure)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
            }
            catch (IOException)
            {
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
            }
            if (entries.Count == 0)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档内没有可导入的图片");

            var resumeIndex = (int)Math.Clamp(task.Integer("pi"), 0, entries.Count);
            var imported = (int)Math.Clamp(task.Integer("ic"), 0, resumeIndex);
            await StageAsync(owner, ArchiveImportStatus.Importing, ArchiveImportText.ImportingStage, ct,
                entries.Count, resumeIndex);

            for (var index = resumeIndex; index < entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await _tasks.TouchOwnerAsync(owner.TaskId, owner, ct))
                    throw new ArchiveLostException("task claim lost");

                // Re-check the project epoch immediately before each short upload. FileService then takes the shared
                // ProjectLabelLock for the upload itself; this worker never holds that lock across download/zip work.
                project = await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", owner.ProjectId))
                    .FirstOrDefaultAsync(ct);
                if (project is null) throw new ArchiveImportFailure("项目不存在", "项目不存在");
                if (project.Integer("st") != 0)
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "项目已完结");
                if (expectedRevision is not null && project.Integer("label_revision", 0) != expectedRevision.Value)
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "项目标签版本已变更，导入已取消");

                var entry = entries[index];
                var entryPath = Path.Combine(tempDir, "entry-" + index.ToString("D6", CultureInfo.InvariantCulture));
                if (!ArchiveFilenamePolicy.IsValid(entry.Name))
                {
                    await AdvanceAsync(owner, index + 1, imported, ct);
                    continue;
                }

                try
                {
                    await archive.ExtractEntryAsync(entry, entryPath, _options.MaxEntryBytes, ct);
                    await using var stream = new FileStream(entryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await _uploader.UploadAsync(project, entry.Name, stream, stream.Length, ct);
                    imported++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ArchiveLostException)
                {
                    throw;
                }
                catch (ApiError error) when (error.Error == "LabelBusyError")
                {
                    throw;
                }
                catch (ApiError error) when (IsFatalFileError(error))
                {
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage, Sanitize(error.Message));
                }
                catch (ApiError)
                {
                    // Invalid filename/oversize/other per-file failure: skip and keep importing.
                }
                catch (ArchiveImportFailure)
                {
                    // A single corrupt/oversized entry is a per-file skip.
                }
                catch (ArchiveUploaderUnavailableException)
                {
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "文件服务不可用");
                }
                catch (Exception error)
                {
                    _logger?.LogWarning("archive entry skipped: {Type}", error.GetType().Name);
                }
                finally
                {
                    TryDelete(entryPath);
                }

                await AdvanceAsync(owner, index + 1, imported, ct);
            }

            if (!await _tasks.TryFinishAsync(owner.TaskId, owner, ArchiveImportStatus.Succeeded,
                    ArchiveImportText.SucceededStage(imported), "", imported, entries.Count, ct))
                throw new ArchiveLostException("task claim lost");
        }
        finally
        {
            await CleanupAsync(tempDir);
        }
    }

    private static ArchiveZipArchive OpenArchive(string path)
    {
        try
        {
            return new ArchiveZipArchive(path);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException
                                          or ArgumentException)
        {
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
        }
    }

    private async Task<(string ApiUrl, List<string> Keys)> ResolveProviderAsync(BsonDocument project,
        CancellationToken ct)
    {
        BsonDocument? team = null;
        var teamId = ArchiveImportIds.Id(project, "t");
        if (teamId is not null)
            team = await _teams.Find(Builders<BsonDocument>.Filter.Eq("_id", teamId.Value)).FirstOrDefaultAsync(ct);

        var teamUrl = team?.Text("aau") ?? "";
        var raw = teamUrl.Length != 0 ? teamUrl : _options.ProviderApiUrl;
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArchiveImportFailure("未配置档案 API", "系统未配置档案 API 地址");

        string apiUrl;
        try
        {
            apiUrl = ExternalApiUrl.Normalize(raw, _options.AllowedHosts, requireAllowlist: teamUrl.Length != 0);
        }
        catch (ArgumentException error)
        {
            throw new ArchiveImportFailure("档案 API 地址无效", Sanitize(error.Message));
        }

        var keys = ReadKeys(team);
        if (keys.Count == 0)
            throw new ArchiveImportFailure("未配置档案 API key", "团队未配置档案 API key，请联系管理员");
        return (apiUrl, keys);
    }

    private List<string> ReadKeys(BsonDocument? team)
    {
        var keys = new List<string>();
        if (team is null || !team.TryGetValue("aak", out var value) || !value.IsBsonArray) return keys;
        foreach (var item in value.AsBsonArray)
        {
            if (!item.IsBsonDocument) continue;
            var entry = item.AsBsonDocument;
            if (entry.TryGetValue("enabled", out var enabled) && enabled.IsBoolean && !enabled.AsBoolean) continue;
            var encrypted = entry.Text("key");
            if (string.IsNullOrWhiteSpace(encrypted)) continue;
            try
            {
                var plain = _secrets.Decrypt(encrypted).Trim();
                if (plain.Length != 0) keys.Add(plain);
            }
            catch (CryptographicException)
            {
                // Invalid entries are skipped; their tail or plaintext is never logged.
            }
        }
        return keys;
    }

    private async Task StageAsync(ArchiveClaimOwner owner, int status, string stage, CancellationToken ct,
        int? totalPages = null, int? completedPages = null)
    {
        if (!await _tasks.TrySetStageAsync(owner.TaskId, owner, status, stage, ct, totalPages, completedPages))
            throw new ArchiveLostException("task claim lost");
    }

    private async Task AdvanceAsync(ArchiveClaimOwner owner, int completedPages, int imported, CancellationToken ct)
    {
        if (!await _tasks.TryAdvanceAsync(owner.TaskId, owner, completedPages, imported, ct))
            throw new ArchiveLostException("task claim lost");
    }

    private async Task FailAsync(ArchiveClaimOwner owner, string stage, string message)
    {
        var safe = Sanitize(message);
        if (safe.Length == 0) safe = "归档导入失败";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _tasks.TryFinishAsync(owner.TaskId, owner, ArchiveImportStatus.Failed, stage, safe, null, null,
                timeout.Token);
        }
        catch (Exception error)
        {
            _logger?.LogWarning("archive import failure write skipped: {Type}", error.GetType().Name);
        }
    }

    private async Task ReleaseAsync(ArchiveClaimOwner owner)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _claims.ReleaseAsync(owner.ProjectId, owner.TaskId, owner.Token, timeout.Token);
        }
        catch (Exception error)
        {
            _logger?.LogWarning("archive claim release skipped: {Type}", error.GetType().Name);
        }
    }

    private async Task EnsureCreateClaimAsync(ObjectId projectId, ObjectId taskId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var claim = await _claims.GetAsync(projectId, ct);
            if (claim is null)
            {
                // A Python-created running row has no C# claim. Reject it unless it is old enough to retire.
                var legacy = await _tasks.FindActiveAsync(projectId, ct);
                if (legacy is not null)
                {
                    var legacyId = legacy["_id"].AsObjectId;
                    if (!await _tasks.TryMarkStaleFailedAsync(legacyId, Now - _options.StaleTaskAge, ct))
                        throw new ApiError("ValidateError", 2, "该项目已有归档导入任务进行中");
                    await _claims.DeleteAsync(projectId, legacyId, ct);
                }
                if (await _claims.TryInsertAsync(projectId, taskId, ct)) return;
                continue;
            }

            var bound = ArchiveImportIds.Id(claim, "task_id");
            if (bound == taskId) return;

            var active = await _tasks.FindActiveAsync(projectId, ct);
            if (active is not null)
            {
                var activeId = active["_id"].AsObjectId;
                if (bound != activeId) await _claims.RebindAsync(projectId, bound, activeId, ct);
                if (await TryRecoverStaleAsync(projectId, active, claim, ct)) continue;
                throw new ApiError("ValidateError", 2, "该项目已有归档导入任务进行中");
            }

            // A concurrent POST may have reserved the claim but not inserted its task yet.
            if (ArchiveImportIds.Time(claim, "reserved_until") > Now)
                throw new ApiError("ValidateError", 2, "该项目已有归档导入任务进行中");

            if (await _claims.RebindAsync(projectId, bound, taskId, ct)) return;
        }
        throw new ApiError("ValidateError", 2, "该项目已有归档导入任务进行中");
    }

    private async Task<bool> TryRecoverStaleAsync(ObjectId projectId, BsonDocument active, BsonDocument claim,
        CancellationToken ct)
    {
        var lease = ArchiveImportIds.Time(claim, "lease_until");
        if (lease != default && lease > Now) return false;
        var taskId = active["_id"].AsObjectId;
        if (!await _tasks.TryMarkStaleFailedAsync(taskId, Now - _options.StaleTaskAge, ct)) return false;
        await _claims.DeleteAsync(projectId, taskId, ct);
        return true;
    }

    private async Task<ArchiveClaimOwner?> AcquireWorkerClaimAsync(ObjectId projectId, ObjectId taskId,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await _claims.GetAsync(projectId, ct);
            if (claim is null)
            {
                if (!await _claims.TryInsertAsync(projectId, taskId, ct)) continue;
                claim = await _claims.GetAsync(projectId, ct);
                if (claim is null) continue;
            }

            var bound = ArchiveImportIds.Id(claim, "task_id");
            if (bound != taskId)
            {
                if (bound is not null)
                {
                    var other = await _tasks.FindByIdAsync(bound.Value, projectId, ct);
                    if (other is not null && ArchiveImportStatus.IsRunning((int)other.Integer("s"))) return null;
                }
                if (!await _claims.RebindAsync(projectId, bound, taskId, ct)) continue;
            }

            var now = Now;
            var token = Guid.NewGuid().ToString("N");
            var lease = now.Add(_options.ClaimLease);
            if (!await _claims.TryTakeAsync(projectId, taskId, token, lease, now, ct)) continue;
            var owner = new ArchiveClaimOwner(projectId, taskId, token, lease);
            if (await _tasks.TryBindOwnerAsync(taskId, owner, ct)) return owner;
            // The task finished or disappeared between the initial read and the claim. Do not leave a claim
            // fencing a completed row; a later task for this project may bind it freely.
            await _claims.ReleaseAsync(projectId, taskId, token, ct);
            return null;
        }
        throw new ArchiveRetryException("archive claim is busy");
    }

    private string CreateTempDirectory()
    {
        var root = _options.TempRoot;
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "moeflow-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task CleanupAsync(string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }, timeout.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or OperationCanceledException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsFatalFileError(ApiError error) => error.Error is
        "StoragePoolUnsupported" or "ProjectFinishedError" or "ProjectNotExistError" or "StorageSourceUnavailable";

    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var text = CredentialPattern.Replace(UrlPattern.Replace(message, "[url]"), "[redacted]");
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 1000 ? text[..1000] : text;
    }
}

public interface IArchiveJobQueue
{
    Task EnqueueAsync(string kind, BsonDocument payload, CancellationToken ct);
}

/// <summary>Thin adapter over the core JobQueue so batch/HTTP tests can substitute an in-memory queue.</summary>
public sealed class ArchiveJobQueue(JobQueue jobs) : IArchiveJobQueue
{
    public Task EnqueueAsync(string kind, BsonDocument payload, CancellationToken ct) =>
        jobs.EnqueueAsync(kind, payload, ct);
}
