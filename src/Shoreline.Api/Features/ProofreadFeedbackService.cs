using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// G3 proofread-draft feedback. Ports app/apis/project.py send-proofread-draft,
/// app/tasks/proofread_draft.py and app/services/proofread_image.py.
///
/// The label read happens under the shared project label lock; imaging and
/// mail work happens after that lock is released.
/// </summary>
public sealed partial class ProofreadFeedbackService(
    MongoStore store,
    IdentityPermissions permissions,
    NotificationPublishService notifications,
    NotificationReadService read,
    ProjectLabelLock labels,
    LabelRequestContext requestContext,
    JobQueue jobs,
    ProjectStorageRouter storageRouter,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider clock,
    IProofreadImageRenderer renderer,
    INotificationMailTransport mail,
    ILogger<ProofreadFeedbackService> logger)
{
    public const string JobKind = "proofread_draft_bake";
    public const long MaxPayloadBytes = 8L * 1024 * 1024;
    private const int FileTypeImage = 2;
    private const int ThumbnailSucceeded = 3;
    private const int SourceBatch = 200;
    private const int RelationBatch = 1000;

    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Tips => store.Collection("tip");
    private IMongoCollection<BsonDocument> Users => store.Collection("user");
    private IMongoCollection<BsonDocument> Members => store.Collection("project_member");
    private IMongoCollection<BsonDocument> Teams => store.Collection("team");
    private IMongoCollection<BsonDocument> NoteRows => store.Collection("notification");
    private IMongoCollection<BsonDocument> ReceiptRows => store.Collection("notification_receipt");
    private IMongoCollection<BsonDocument> DeliveryRows => store.Collection("notification_delivery");

    public bool Enabled => read.Enabled;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------
    // POST /v1/projects/{projectId}/targets/{targetId}/send-proofread-draft
    // ------------------------------------------------------------------
    public async Task<ProofreadDraftResult> SendAsync(
        BsonDocument actor,
        string projectId,
        string targetId,
        JsonElement data,
        string? idempotencyKey,
        CancellationToken ct)
    {
        if (!ObjectId.TryParse(projectId, out var projectKey))
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
        if (!ObjectId.TryParse(targetId, out var targetKey))
            throw new ApiError("TargetNotExistError", 4024, "项目目标语言不存在");

        var project = await Projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectKey)).FirstOrDefaultAsync(ct)
                      ?? throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
        var target = await Targets.Find(Builders<BsonDocument>.Filter.Eq("_id", targetKey)).FirstOrDefaultAsync(ct)
                     ?? throw new ApiError("TargetNotExistError", 4024, "项目目标语言不存在");
        // Python Target.project uses db_field "t" (app/models/target.py), not "p".
        if (target.GetValue("t", BsonNull.Value) is not BsonObjectId targetProject ||
            targetProject.Value != projectKey)
            throw new ApiError("TargetNotExistError", 4024, "项目目标语言不存在");

        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        if (!snapshot.Has("project:PROOFREAD_TRA"))
            throw new ApiError("NoPermissionError", 103, "您没有校对权限，无法寄送校对稿");

        var (ccMyself, fileId) = ParseBody(data);
        ObjectId? targetFileId = null;
        if (!string.IsNullOrWhiteSpace(fileId))
        {
            if (!ObjectId.TryParse(fileId, out var parsedFile))
                throw new ApiError("FileNotExistError", 4006, "文件不存在");
            targetFileId = parsedFile;
        }

        var actorId = actor["_id"].AsObjectId;
        var operation = new BsonDocument
        {
            ["target_id"] = targetKey.ToString(),
            ["file_id"] = targetFileId?.ToString() ?? "",
            ["cc_myself"] = ccMyself,
        };

        if (Enabled && !string.IsNullOrEmpty(idempotencyKey))
        {
            var digest = Sha256Hex($"{actorId}:core:{idempotencyKey}");
            var old = await NoteRows.Find(Builders<BsonDocument>.Filter.Eq("key", digest)).FirstOrDefaultAsync(ct);
            if (old is not null)
            {
                if (old.Text("event_type") != "proofread_feedback" ||
                    old.GetValue("project_id", BsonNull.Value) is not BsonObjectId noteProject ||
                    noteProject.Value != projectKey ||
                    !SameOperation(old, operation))
                {
                    throw NotificationErrors.Conflict("相同操作键不能用于不同内容");
                }
                await EnqueueBakeAsync(old, projectKey, ct);
                return new ProofreadDraftResult(202, new Dictionary<string, object?>
                {
                    ["message"] = "校对反馈已受理，邮件投递状态请查看通知记录",
                    ["notification_id"] = old["_id"].AsObjectId.ToString(),
                    ["state"] = old.Text("state"),
                });
            }
        }

        List<ProofreadPage> pages;
        long revision;
        await using (var session = await labels.EnterAsync(requestContext, projectKey, false, null, ct))
        {
            revision = session.Project.Integer("label_revision");
            pages = await BuildPagesAsync(session.Project, targetKey, targetFileId, ct);
        }
        await ResolveOriginalUrlsAsync(project, pages, ct);
        if (pages.Count == 0)
            throw new ApiError("ProofreadDraftNoChangesError", 4030, "当前目标语言没有可寄送的校对修改");

        var currentEmail = actor.Text("e").Trim().ToLowerInvariant();
        var toEmails = await TranslatorEmailsAsync(projectKey, ct);
        var ccEmails = new List<string>();
        if (ccMyself && currentEmail.Length > 0 && !toEmails.Contains(currentEmail, StringComparer.Ordinal))
            ccEmails.Add(currentEmail);

        var team = project.GetValue("t", BsonNull.Value) is BsonObjectId teamId
            ? await Teams.Find(Builders<BsonDocument>.Filter.Eq("_id", teamId.Value)).FirstOrDefaultAsync(ct)
            : null;
        var markerEnabled = team is null || team.GetValue("pme", BsonBoolean.True).ToBoolean();
        var opacity = team is not null && team.TryGetValue("pmo", out var pmo) && pmo.IsNumeric
            ? Math.Clamp(pmo.ToInt32(), 0, 100)
            : 92;

        var siteName = FirstNonEmpty(configuration["Site:Name"], configuration["SITE_NAME"], "萌翻");
        var siteOrigin = FirstNonEmpty(configuration["Site:Origin"], configuration["SITE_ORIGIN"], "").TrimEnd('/');
        var projectUrl = siteOrigin.Length > 0 ? $"{siteOrigin}/project/{projectKey}" : "";
        var targetLanguageName = await TargetLanguageNameAsync(target, ct);
        var totalChanged = pages.Sum(p => p.ChangedCount);
        var totalTips = pages.Sum(p => p.TipCount);

        if (!Enabled)
        {
            if (toEmails.Count == 0)
            {
                if (ccMyself && currentEmail.Length > 0)
                {
                    toEmails.Add(currentEmail);
                    ccEmails.Clear();
                }
                else throw new ApiError("NoTranslatorMemberError", 4031, "项目没有可接收校对稿的翻译成员");
            }
            foreach (var page in pages)
                page.MarkedImageUrl = await RenderPageAsync(project, page, markerEnabled, opacity, ct);

            var (subject, html, text) = ProofreadMailRenderer.Render(siteName, project.Text("n"),
                targetLanguageName, actor.Text("n"), currentEmail, SendTime(), totalChanged, totalTips,
                pages, projectUrl, currentEmail);
            var recipients = toEmails.Concat(ccEmails).ToList();
            foreach (var recipient in recipients)
            {
                var message = new NotificationMailMessage(recipient, subject, text, html,
                    ProofreadMessageId(projectKey, targetKey, recipient), currentEmail.Length > 0 ? currentEmail : null);
                var checkedMail = ProtectedNotificationMail.Check(message,
                    NotificationMailSettings.FromConfiguration(configuration, environment));
                if (checkedMail.Status != "ok")
                    throw new ApiError("NotificationError", 4600, "校对稿邮件无法发送", 502);
                var result = await mail.SendAsync(message, ct);
                if (result.Status is not ("accepted" or "unknown"))
                    throw new ApiError("NotificationError", 4600, "校对稿邮件无法发送", 502);
            }
            return new ProofreadDraftResult(200, new Dictionary<string, object?>
            {
                ["message"] = "校对稿已成功发送至翻译邮箱",
                ["recipients"] = recipients,
                ["changed_pages_count"] = pages.Count,
                ["changed_labels_count"] = totalChanged,
            });
        }

        if (toEmails.Count == 0)
            throw new ApiError("NoTranslatorMemberError", 4031, "项目没有可接收校对稿的翻译成员");

        var pending = PendingPayload(siteName, project, targetLanguageName, actor.Text("n"), currentEmail,
            SendTime(), totalChanged, totalTips, pages, projectUrl, currentEmail, markerEnabled, opacity);
        var notificationData = new Dictionary<string, object?>
        {
            ["category"] = "project",
            ["scope_id"] = projectKey.ToString(),
            ["title"] = $"「{project.Text("n")}」校对稿 · 正在生成标注图（0/{pages.Count}）",
            ["body"] = $"{actor.Text("n")} 寄送了 {targetLanguageName} 校对反馈，共 {pages.Count} 页、{totalChanged} 处修改。\n[project]{projectKey}[/project]",
            ["audience"] = new Dictionary<string, object?>
            {
                ["mode"] = "condition",
                ["tags"] = new[] { "translator" },
            },
            ["email"] = true,
            ["draft"] = true,
        };
        var dataElements = notificationData.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value));

        var payload = new BsonDocument
        {
            ["pending"] = pending,
            ["reply_to"] = currentEmail,
            ["cc_myself"] = ccMyself,
            ["operation"] = operation,
            ["label_revision"] = revision,
        };

        BsonDocument note;
        try
        {
            note = await notifications.PublishAsync(actor, dataElements,
                string.IsNullOrEmpty(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
                "proofread_feedback", payload, "core", true, ct);
        }
        catch (ApiError error) when (error.Error == NotificationErrors.ErrorName &&
                                     error.Message == "没有符合条件的接收人")
        {
            throw new ApiError("NoTranslatorMemberError", 4031, "项目没有可接收校对稿的翻译成员");
        }

        await ReceiptRows.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("notification_id", note["_id"]), Builders<BsonDocument>.Filter.Eq("user_id", actorId)),
            Builders<BsonDocument>.Update
                .SetOnInsert("created_at", Now())
                .SetOnInsert("archived", false),
            new UpdateOptions { IsUpsert = true }, ct);

        await jobs.EnqueueAsync(JobKind, new BsonDocument
        {
            ["notification_id"] = note["_id"],
            ["project_id"] = projectKey,
            ["label_revision"] = revision,
        }, ct);

        return new ProofreadDraftResult(202, new Dictionary<string, object?>
        {
            ["message"] = "校对反馈已受理，正在后台生成标注图，完成后自动寄出",
            ["notification_id"] = note["_id"].AsObjectId.ToString(),
            ["state"] = note.Text("state"),
            ["changed_pages_count"] = pages.Count,
            ["changed_labels_count"] = totalChanged,
        });
    }

    private static (bool CcMyself, string? FileId) ParseBody(JsonElement data)
    {
        if (data.ValueKind is not JsonValueKind.Object)
            throw ApiError.Invalid("请求必须是 JSON 对象");
        var cc = true;
        string? fileId = null;
        foreach (var property in data.EnumerateObject())
        {
            switch (property.Name)
            {
                case "cc_myself":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw ApiError.Field("cc_myself", "cc_myself 必须是布尔值");
                    cc = property.Value.GetBoolean();
                    break;
                case "file_id":
                    if (property.Value.ValueKind is JsonValueKind.Null) fileId = null;
                    else if (property.Value.ValueKind is JsonValueKind.String) fileId = property.Value.GetString();
                    else throw ApiError.Field("file_id", "file_id 必须是字符串");
                    break;
                default:
                    throw ApiError.Invalid("请求包含不支持的字段");
            }
        }
        return (cc, fileId);
    }

    private async Task<string> TargetLanguageNameAsync(BsonDocument target, CancellationToken ct)
    {
        if (target.GetValue("l", BsonNull.Value) is not BsonObjectId languageId) return "";
        var language = await store.Collection("language")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", languageId.Value)).FirstOrDefaultAsync(ct);
        return language?.Text("c") ?? "";
    }

    private async Task<List<string>> TranslatorEmailsAsync(ObjectId projectId, CancellationToken ct)
    {
        var rows = await Members.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("project", projectId),
                Builders<BsonDocument>.Filter.Eq("status", "active"),
                Builders<BsonDocument>.Filter.AnyEq("tags", "translator"),
                Builders<BsonDocument>.Filter.Ne("user", BsonNull.Value)))
            .ToListAsync(ct);
        var ids = rows.Select(row => row["user"].AsObjectId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var users = await Users.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.In("_id", ids),
                Builders<BsonDocument>.Filter.Ne("b", true)))
            .ToListAsync(ct);
        return users.Select(user => user.Text("e").Trim().ToLowerInvariant())
            .Where(email => email.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task EnqueueBakeAsync(BsonDocument note, ObjectId projectId, CancellationToken ct)
    {
        var revision = note.GetValue("payload", BsonNull.Value) is BsonDocument payload &&
                       payload.TryGetValue("label_revision", out var value) && value.IsNumeric
            ? value.ToInt64()
            : 0L;
        await jobs.EnqueueAsync(JobKind, new BsonDocument
        {
            ["notification_id"] = note["_id"],
            ["project_id"] = projectId,
            ["label_revision"] = revision,
        }, ct);
    }

    // ------------------------------------------------------------------
    // Snapshot read (under the shared project label lock)
    // ------------------------------------------------------------------
    private async Task<List<ProofreadPage>> BuildPagesAsync(BsonDocument project, ObjectId targetId,
        ObjectId? fileId, CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var imageFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("p", projectId),
            Builders<BsonDocument>.Filter.Eq("t", FileTypeImage),
            Builders<BsonDocument>.Filter.Eq("ac", true));
        var allFiles = await Files.Find(imageFilter)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .Sort(Builders<BsonDocument>.Sort.Ascending("dir_sort_name").Ascending("sn").Ascending("_id"))
            .ToListAsync(ct);
        var pageNumbers = allFiles.Select((file, index) => (file["_id"].AsObjectId, index + 1))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);

        List<BsonDocument> selected;
        if (fileId is { } only)
        {
            selected = await Files.Find(Builders<BsonDocument>.Filter.And(imageFilter, Builders<BsonDocument>.Filter.Eq("_id", only))).ToListAsync(ct);
            if (selected.Count == 0)
                throw new ApiError("FileNotExistError", 4006, "文件不存在");
        }
        else
        {
            selected = await Files.Find(imageFilter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("dir_sort_name").Ascending("sn").Ascending("_id"))
                .ToListAsync(ct);
        }

        var pages = new List<ProofreadPage>();
        foreach (var file in selected)
        {
            var fileKey = file["_id"].AsObjectId;
            var labels = new List<ProofreadLabel>();
            var pageTips = 0;
            long rankCursor = long.MinValue;
            ObjectId idCursor = default;
            while (true)
            {
                var sourceFilter = rankCursor == long.MinValue
                    ? Builders<BsonDocument>.Filter.Eq("f", fileKey)
                    : Builders<BsonDocument>.Filter.Or(
                        Builders<BsonDocument>.Filter.Gt("r", rankCursor),
                        Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("r", rankCursor), Builders<BsonDocument>.Filter.Gt("_id", idCursor)));
                var sources = await Sources.Find(Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("f", fileKey), sourceFilter))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("r").Ascending("_id"))
                    .Limit(SourceBatch)
                    .ToListAsync(ct);
                if (sources.Count == 0) break;
                rankCursor = sources[^1].Integer("r");
                idCursor = sources[^1]["_id"].AsObjectId;

                var sourceIds = sources.Select(s => s["_id"].AsObjectId).ToList();
                var translations = await Translations.Find(Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.In("o", sourceIds), Builders<BsonDocument>.Filter.Eq("t", targetId)))
                    .Sort(Builders<BsonDocument>.Sort
                        .Descending("s").Descending("p").Descending("e").Descending("ct").Descending("_id"))
                    .Limit(RelationBatch)
                    .ToListAsync(ct);
                var translationBySource = new Dictionary<ObjectId, BsonDocument>();
                foreach (var translation in translations)
                {
                    if (translation.GetValue("o", BsonNull.Value) is BsonObjectId sourceKey &&
                        !translationBySource.ContainsKey(sourceKey.Value))
                        translationBySource[sourceKey.Value] = translation;
                }

                var tips = await Tips.Find(Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.In("o", sourceIds), Builders<BsonDocument>.Filter.Eq("t", targetId)))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("ct"))
                    .Limit(RelationBatch)
                    .ToListAsync(ct);
                var tipUserIds = tips
                    .Select(t => t.GetValue("u", BsonNull.Value))
                    .Where(v => v.IsObjectId).Select(v => v.AsObjectId).Distinct().ToList();
                var tipUsers = tipUserIds.Count == 0
                    ? new Dictionary<ObjectId, BsonDocument>()
                    : (await Users.Find(Builders<BsonDocument>.Filter.In("_id", tipUserIds)).ToListAsync(ct))
                        .ToDictionary(u => u["_id"].AsObjectId);
                var tipsBySource = tips.GroupBy(t => t.GetValue("o", BsonNull.Value).IsObjectId
                    ? t["o"].AsObjectId : ObjectId.Empty).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var source in sources)
                {
                    var sourceKey = source["_id"].AsObjectId;
                    translationBySource.TryGetValue(sourceKey, out var translation);
                    var orig = translation?.Text("c") ?? "";
                    var proof = translation?.Text("p") ?? "";
                    var isChanged = proof.Trim().Length > 0 &&
                                    !string.Equals(proof.Trim(), orig.Trim(), StringComparison.Ordinal);
                    var label = new ProofreadLabel
                    {
                        SourceId = sourceKey.ToString(),
                        LabelNum = (int)(source.Integer("r") + 1),
                        SourceText = source.Text("c"),
                        OrigTranslation = orig,
                        ProofreadTranslation = proof,
                        IsChanged = isChanged,
                        DiffHtml = isChanged ? GenerateDiffHtml(orig, proof) : WebUtility.HtmlEncode(proof.Length > 0 ? proof : orig),
                        X = DoubleValue(source,"x"),
                        Y = DoubleValue(source,"y"),
                        PositionType = (int)source.Integer("p", 1),
                    };
                    if (tipsBySource.TryGetValue(sourceKey, out var sourceTips))
                    {
                        foreach (var tip in sourceTips)
                        {
                            var userId = tip.GetValue("u", BsonNull.Value);
                            var author = userId.IsObjectId && tipUsers.TryGetValue(userId.AsObjectId, out var user)
                                ? user.Text("n") : null;
                            label.Tips.Add(new ProofreadTip
                            {
                                Author = author,
                                Content = tip.Text("c"),
                                EditTime = TipEditTime(tip),
                            });
                        }
                        pageTips += sourceTips.Count;
                    }
                    labels.Add(label);
                }
            }

            var changed = labels.Where(l => l.IsChanged).ToList();
            if (changed.Count == 0 && pageTips == 0) continue;
            pages.Add(new ProofreadPage
            {
                PageNumber = pageNumbers.GetValueOrDefault(fileKey, pages.Count + 1),
                FileId = fileKey.ToString(),
                FileName = file.Text("n"),
                ImageUrl = "",
                SourceKey = OriginalKey(file),
                TotalSources = labels.Count,
                ChangedCount = changed.Count,
                TipCount = pageTips,
                ChangedLabelNums = changed.Select(l => l.LabelNum).ToList(),
                Labels = labels,
            });
        }
        return pages;
    }

    private static bool SameOperation(BsonDocument note, BsonDocument operation)
    {
        if (note.GetValue("payload", BsonNull.Value) is not BsonDocument payload) return false;
        if (payload.GetValue("operation", BsonNull.Value) is not BsonDocument existing) return false;
        return existing.Text("target_id") == operation.Text("target_id") &&
               existing.Text("file_id") == operation.Text("file_id") &&
               existing.Flag("cc_myself") == operation.Flag("cc_myself");
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string SendTime() =>
        Now().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static readonly TimeSpan UrlValidity = ObjectStorage.DefaultSignedUrlValidity;
    private const string FileKeyPrefix = "files/";

    private static double DoubleValue(BsonDocument document, string key, double fallback = 0) =>
        document.TryGetValue(key, out var value) && value.IsNumeric ? value.ToDouble() : fallback;

    private static bool MarkerEnabled(BsonDocument pending) =>
        pending.TryGetValue("marker_enabled", out var value) && value.IsBoolean
            ? value.AsBoolean
            : !pending.TryGetValue("pme", out var legacy) || !legacy.IsBoolean || legacy.AsBoolean;

    private static string TipEditTime(BsonDocument tip) =>
        tip.TryGetValue("et", out var value) && value.IsBsonDateTime
            ? value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
}
