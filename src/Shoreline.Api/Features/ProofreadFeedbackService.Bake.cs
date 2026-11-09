using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed partial class ProofreadFeedbackService
{
    /// <summary>
    /// Durable bake job. The job row owns retry/lease semantics; this method
    /// only mutates a note while it is still a draft and always re-checks the
    /// label revision before publishing the final payload.
    /// </summary>
    public async Task BakeAsync(BsonDocument jobPayload, CancellationToken ct)
    {
        if (jobPayload.GetValue("notification_id", BsonNull.Value) is not BsonObjectId noteId) return;
        var note = await NoteRows.Find(Builders<BsonDocument>.Filter.Eq("_id", noteId.Value)).FirstOrDefaultAsync(ct);
        if (note is null || note.Text("event_type") != "proofread_feedback") return;
        if (note.GetValue("revoked_at", BsonNull.Value) is not BsonNull) return;
        if (note.Text("state") != "draft") return;
        if (note.GetValue("payload", BsonNull.Value) is not BsonDocument payload ||
            payload.GetValue("pending", BsonNull.Value) is not BsonDocument pending) return;

        var projectId = payload.GetValue("project_id", BsonNull.Value) is BsonObjectId pid
            ? pid.Value
            : jobPayload.GetValue("project_id", BsonNull.Value) is BsonObjectId jid ? jid.Value : ObjectId.Empty;
        if (projectId == ObjectId.Empty) return;
        var project = await Projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId)).FirstOrDefaultAsync(ct);
        if (project is null)
        {
            var fallbackName = pending.Text("pn");
            await CancelDraftAsync(noteId.Value, fallbackName, "project_missing", ct);
            return;
        }

        var revision = project.Integer("label_revision");
        var expected = payload.TryGetValue("label_revision", out var expectedValue) && expectedValue.IsNumeric
            ? expectedValue.ToInt64() : revision;
        if (revision != expected || (jobPayload.TryGetValue("label_revision", out var jobRevision) &&
                                     jobRevision.IsNumeric && jobRevision.ToInt64() != revision))
        {
            await CancelDraftAsync(noteId.Value, project.Text("n"), "label_revision_stale", ct);
            return;
        }

        try
        {
            var pages = PagesFromBson(pending);
            var markerEnabled = MarkerEnabled(pending);
            var opacityValue = pending.GetValue("marker_opacity", pending.GetValue("pmo", new BsonInt32(92)));
            var opacity = opacityValue.IsNumeric ? Math.Clamp(opacityValue.ToInt32(), 0, 100) : 92;
            var total = pages.Count;
            var done = 0;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                page.MarkedImageUrl = await RenderPageAsync(project, page, markerEnabled, opacity, ct);
                done++;
                await NoteRows.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", noteId.Value), Builders<BsonDocument>.Filter.Eq("state", "draft"),
                        Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)),
                    Builders<BsonDocument>.Update.Set("title",
                        $"「{project.Text("n")}」校对稿 · 正在生成标注图（{done}/{total}）"),
                    cancellationToken: ct);
            }

            var siteName = pending.Text("sn", "萌翻");
            var projectName = pending.Text("pn", project.Text("n"));
            var language = pending.Text("tl");
            var senderName = pending.Text("sender_name");
            var senderEmail = pending.Text("sender_email");
            var sendTime = pending.Text("send_time");
            var totalChanged = (int)pending.Integer("total_changed");
            var totalTips = (int)pending.Integer("total_tips");
            var projectUrl = pending.Text("project_url");
            var replyAddress = pending.Text("reply_address");
            var (_, html, text) = ProofreadMailRenderer.Render(siteName, projectName, language,
                senderName, senderEmail, sendTime, totalChanged, totalTips, pages, projectUrl, replyAddress);

            var finalPayload = new BsonDocument
            {
                ["html"] = html,
                ["text"] = text,
                ["reply_to"] = replyAddress,
                ["cc_myself"] = payload.Flag("cc_myself"),
                ["operation"] = payload.GetValue("operation", new BsonDocument()),
            };
            if (PythonJsonSize(finalPayload) > MaxPayloadBytes)
            {
                await CancelDraftAsync(noteId.Value, projectName, "payload_too_large", ct,
                    $"「{projectName}」校对稿过大，已取消寄送");
                return;
            }

            var flipped = await NoteRows.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", noteId.Value), Builders<BsonDocument>.Filter.Eq("state", "draft"),
                    Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)),
                Builders<BsonDocument>.Update
                    .Set("payload", finalPayload)
                    .Set("title", $"「{projectName}」校对稿已寄送")
                    .Set("state", "preparing"),
                cancellationToken: ct);
            if (flipped.ModifiedCount == 0) return;

            await notifications.DispatchAsync(noteId.Value, ct);
            var deliveries = await DeliveryRows.Find(Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("notification_id", noteId.Value),
                    Builders<BsonDocument>.Filter.In("state", ["pending", "retry_wait"])))
                .Limit(50).ToListAsync(ct);
            foreach (var delivery in deliveries)
            {
                ct.ThrowIfCancellationRequested();
                await notifications.DeliverAsync(delivery["_id"].AsObjectId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning("Proofread draft bake failed ({ErrorType})", error.GetType().Name);
            await NoteRows.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", noteId.Value), Builders<BsonDocument>.Filter.Eq("state", "draft")),
                Builders<BsonDocument>.Update.Set("title",
                    $"「{project.Text("n")}」校对稿生成失败（请重新寄送）"),
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task CancelDraftAsync(ObjectId noteId, string projectName, string reason,
        CancellationToken ct, string? title = null)
    {
        await NoteRows.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", noteId), Builders<BsonDocument>.Filter.Eq("state", "draft")),
            Builders<BsonDocument>.Update
                .Set("state", "cancelled")
                .Set("error", reason)
                .Set("title", title ?? $"「{projectName}」校对稿已取消寄送"),
            cancellationToken: ct);
    }

    private BsonDocument PendingPayload(string siteName, BsonDocument project, string language,
        string senderName, string senderEmail, string sendTime, int totalChanged, int totalTips,
        IReadOnlyList<ProofreadPage> pages, string projectUrl, string replyAddress,
        bool markerEnabled, int opacity)
    {
        return new BsonDocument
        {
            ["sn"] = siteName,
            ["pn"] = project.Text("n"),
            ["tl"] = language,
            ["sender_name"] = senderName,
            ["sender_email"] = senderEmail,
            ["send_time"] = sendTime,
            ["total_changed"] = totalChanged,
            ["total_tips"] = totalTips,
            ["changed_pages"] = new BsonArray(pages.Select(PageToBson)),
            ["project_url"] = projectUrl,
            ["reply_address"] = replyAddress,
            ["marker_enabled"] = markerEnabled,
            ["marker_opacity"] = opacity,
        };
    }

    private static BsonDocument PageToBson(ProofreadPage page) => new()
    {
        ["page_number"] = page.PageNumber,
        ["file_id"] = page.FileId,
        ["file_name"] = page.FileName,
        ["image_url"] = page.ImageUrl,
        ["source_key"] = page.SourceKey,
        ["marked_image_url"] = page.MarkedImageUrl,
        ["total_sources"] = page.TotalSources,
        ["changed_count"] = page.ChangedCount,
        ["tip_count"] = page.TipCount,
        ["changed_label_nums"] = new BsonArray(page.ChangedLabelNums),
        ["labels"] = new BsonArray(page.Labels.Select(label => new BsonDocument
        {
            ["source_id"] = label.SourceId,
            ["label_num"] = label.LabelNum,
            ["source_text"] = label.SourceText,
            ["orig_translation"] = label.OrigTranslation,
            ["proofread_translation"] = label.ProofreadTranslation,
            ["is_changed"] = label.IsChanged,
            ["diff_html"] = label.DiffHtml,
            ["tips"] = new BsonArray(label.Tips.Select(tip => new BsonDocument
            {
                ["author"] = tip.Author is null ? BsonNull.Value : tip.Author,
                ["content"] = tip.Content,
                ["edit_time"] = tip.EditTime,
            })),
            ["x"] = label.X,
            ["y"] = label.Y,
            ["position_type"] = label.PositionType,
        })),
    };

    private static List<ProofreadPage> PagesFromBson(BsonDocument pending)
    {
        var pages = new List<ProofreadPage>();
        if (!pending.TryGetValue("changed_pages", out var pagesValue) || !pagesValue.IsBsonArray) return pages;
        foreach (var pageValue in pagesValue.AsBsonArray)
        {
            if (!pageValue.IsBsonDocument) continue;
            var document = pageValue.AsBsonDocument;
            var page = new ProofreadPage
            {
                PageNumber = (int)document.Integer("page_number", 1),
                FileId = document.Text("file_id"),
                FileName = document.Text("file_name"),
                ImageUrl = document.Text("image_url"),
                SourceKey = document.Text("source_key"),
                MarkedImageUrl = document.Text("marked_image_url"),
                TotalSources = (int)document.Integer("total_sources"),
                ChangedCount = (int)document.Integer("changed_count"),
                TipCount = (int)document.Integer("tip_count"),
                ChangedLabelNums = document.TryGetValue("changed_label_nums", out var nums) && nums.IsBsonArray
                    ? nums.AsBsonArray.Where(v => v.IsNumeric).Select(v => v.ToInt32()).ToList()
                    : [],
            };
            if (document.TryGetValue("labels", out var labelsValue) && labelsValue.IsBsonArray)
            {
                foreach (var labelValue in labelsValue.AsBsonArray)
                {
                    if (!labelValue.IsBsonDocument) continue;
                    var label = labelValue.AsBsonDocument;
                    var item = new ProofreadLabel
                    {
                        SourceId = label.Text("source_id"),
                        LabelNum = (int)label.Integer("label_num"),
                        SourceText = label.Text("source_text"),
                        OrigTranslation = label.Text("orig_translation"),
                        ProofreadTranslation = label.Text("proofread_translation"),
                        IsChanged = label.Flag("is_changed"),
                        DiffHtml = label.Text("diff_html"),
                        X = DoubleValue(label,"x"),
                        Y = DoubleValue(label,"y"),
                        PositionType = (int)label.Integer("position_type", 1),
                    };
                    if (label.TryGetValue("tips", out var tipsValue) && tipsValue.IsBsonArray)
                    {
                        foreach (var tipValue in tipsValue.AsBsonArray)
                        {
                            if (!tipValue.IsBsonDocument) continue;
                            var tip = tipValue.AsBsonDocument;
                            item.Tips.Add(new ProofreadTip
                            {
                                Author = tip.TryGetValue("author", out var author) && author.IsString
                                    ? author.AsString : null,
                                Content = tip.Text("content"),
                                EditTime = tip.Text("edit_time"),
                            });
                        }
                    }
                    page.Labels.Add(item);
                }
            }
            pages.Add(page);
        }
        return pages;
    }

    private async Task ResolveOriginalUrlsAsync(IReadOnlyList<ProofreadPage> pages, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.SourceKey) || !page.SourceKey.StartsWith("files/", StringComparison.Ordinal))
                continue;
            try
            {
                page.ImageUrl = await storage.SignGetAsync(page.SourceKey, UrlValidity);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                page.ImageUrl = "";
            }
        }
    }

    private async Task<string> RenderPageAsync(BsonDocument project, ProofreadPage page,
        bool markerEnabled, int opacity, CancellationToken ct)
    {
        if (!markerEnabled || page.Labels.Count == 0) return page.ImageUrl;
        var saveName = page.SourceKey.StartsWith("files/", StringComparison.Ordinal)
            ? page.SourceKey["files/".Length..]
            : "";
        if (saveName.Length == 0 || saveName.Contains('/') || saveName.Contains('\\')) return page.ImageUrl;
        try
        {
            var baseKey = await BaseImageKeyAsync(project, saveName, ct);
            using var download = await storage.GetAsync(baseKey, ct);
            if (download.Length > 64L * 1024 * 1024) return page.ImageUrl;
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, ct);
            var markers = page.Labels.Select(label => new ProofreadMarkerLabel(
                label.X, label.Y, label.PositionType, label.LabelNum)).ToList();
            var fingerprint = renderer.Fingerprint(markers, opacity);
            var key = $"files/proofread-{fingerprint}-{Stem(saveName)}.webp";
            if (await storage.HeadAsync(key, ct) is null)
            {
                var rendered = renderer.Render(buffer.ToArray(), markers, opacity);
                if (rendered is null) return await FallbackUrlAsync(page, ct);
                using var output = new MemoryStream(rendered, writable: false);
                await storage.PutAsync(key, output, "image/webp", ct);
            }
            return await storage.SignGetAsync(key, UrlValidity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning("Proofread marker render failed ({ErrorType})", error.GetType().Name);
            return await FallbackUrlAsync(page, ct);
        }
    }

    private async Task<string> FallbackUrlAsync(ProofreadPage page, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(page.ImageUrl)) return page.ImageUrl;
        try
        {
            return string.IsNullOrWhiteSpace(page.SourceKey)
                ? ""
                : await storage.SignGetAsync(page.SourceKey, UrlValidity);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return "";
        }
    }

    private async Task<string> BaseImageKeyAsync(BsonDocument project, string saveName, CancellationToken ct)
    {
        var original = FileKeyPrefix + saveName;
        if (!project.TryGetValue("csid", out var csid) || csid.IsBsonNull)
        {
            var resample = FileKeyPrefix + "resample-" + Stem(saveName) + ".webp";
            if (await storage.HeadAsync(resample, ct) is not null) return resample;
        }
        return original;
    }

    private static string OriginalKey(BsonDocument file)
    {
        var saveName = file.Text("sa");
        return saveName.Length == 0 ? "" : FileKeyPrefix + saveName;
    }

    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    private static string ProofreadMessageId(ObjectId projectId, ObjectId targetId, string recipient)
    {
        var local = new string(recipient.Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray());
        return $"<proofread.{projectId}.{targetId}.{local}@moeflow.invalid>";
    }

    /// <summary>
    /// Python json.dumps(default=str, ensure_ascii=True) byte size estimate for
    /// the 8 MiB cap. Keys keep BSON insertion order like the legacy dict.
    /// </summary>
    internal static long PythonJsonSize(BsonValue value)
    {
        var builder = new StringBuilder();
        AppendPythonJson(builder, value);
        return Encoding.UTF8.GetByteCount(builder.ToString());
    }

    private static void AppendPythonJson(StringBuilder builder, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Null:
                builder.Append("null");
                break;
            case BsonType.Boolean:
                builder.Append(value.AsBoolean ? "true" : "false");
                break;
            case BsonType.Int32:
            case BsonType.Int64:
                builder.Append(value.ToInt64().ToString(CultureInfo.InvariantCulture));
                break;
            case BsonType.Double:
                builder.Append(value.ToDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            case BsonType.String:
                AppendPythonString(builder, value.AsString);
                break;
            case BsonType.ObjectId:
                AppendPythonString(builder, value.AsObjectId.ToString());
                break;
            case BsonType.DateTime:
                AppendPythonString(builder, value.AsBsonDateTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case BsonType.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.AsBsonArray)
                {
                    if (!firstItem) builder.Append(", ");
                    firstItem = false;
                    AppendPythonJson(builder, item);
                }
                builder.Append(']');
                break;
            case BsonType.Document:
                builder.Append('{');
                var firstField = true;
                foreach (var element in value.AsBsonDocument)
                {
                    if (!firstField) builder.Append(", ");
                    firstField = false;
                    AppendPythonString(builder, element.Name);
                    builder.Append(": ");
                    AppendPythonJson(builder, element.Value);
                }
                builder.Append('}');
                break;
            default:
                AppendPythonString(builder, value.ToString() ?? "");
                break;
        }
    }

    private static void AppendPythonString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (rune.Value < 0x20 || rune.Value > 0x7e)
                        builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append((char)rune.Value);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>
    /// Character diff equivalent to difflib.SequenceMatcher for the mail body.
    /// Both sides are HTML encoded; only local markup is emitted unescaped.
    /// </summary>
    public static string GenerateDiffHtml(string original, string proof)
    {
        const string deletedStyle = "background-color: #ffdce0; color: #af1b28; text-decoration: line-through; padding: 1px 3px; border-radius: 2px; margin: 0 1px;";
        const string insertedStyle = "background-color: #dcffe4; color: #116329; text-decoration: none; padding: 1px 3px; border-radius: 2px; font-weight: bold; margin: 0 1px;";
        string E(string value) => WebUtility.HtmlEncode(value);

        var prefix = 0;
        while (prefix < original.Length && prefix < proof.Length && original[prefix] == proof[prefix]) prefix++;
        var suffix = 0;
        while (suffix < original.Length - prefix && suffix < proof.Length - prefix &&
               original[original.Length - 1 - suffix] == proof[proof.Length - 1 - suffix]) suffix++;

        var html = new StringBuilder(original.Length + proof.Length + 64);
        html.Append(E(original[..prefix]));
        var left = original[prefix..(original.Length - suffix)];
        var right = proof[prefix..(proof.Length - suffix)];
        if (left.Length > 0 || right.Length > 0)
        {
            if (left.Length * right.Length is > 0 and <= 250_000)
            {
                AppendDiff(html, left, right, deletedStyle, insertedStyle);
            }
            else
            {
                if (left.Length > 0) html.Append("<del style=\"").Append(deletedStyle).Append("\">").Append(E(left)).Append("</del>");
                if (right.Length > 0) html.Append("<ins style=\"").Append(insertedStyle).Append("\">").Append(E(right)).Append("</ins>");
            }
        }
        html.Append(E(original[(original.Length - suffix)..]));
        return html.ToString();
    }

    private static void AppendDiff(StringBuilder html, string left, string right,
        string deletedStyle, string insertedStyle)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        var table = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
                table[i, j] = left[i] == right[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        }
        var x = 0;
        var y = 0;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length && left[x] == right[y])
            {
                html.Append(E(left[x].ToString()));
                x++;
                y++;
            }
            else if (y < right.Length && (x >= left.Length || table[x, y + 1] >= table[x + 1, y]))
            {
                html.Append("<ins style=\"").Append(insertedStyle).Append("\">").Append(E(right[y].ToString())).Append("</ins>");
                y++;
            }
            else
            {
                html.Append("<del style=\"").Append(deletedStyle).Append("\">").Append(E(left[x].ToString())).Append("</del>");
                x++;
            }
        }
    }
}
