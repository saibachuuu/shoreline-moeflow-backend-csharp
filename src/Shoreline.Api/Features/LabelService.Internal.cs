using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed partial class LabelService
{
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Tips => store.Collection("tip");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> FileTargetCaches => store.Collection("file_target_cache");
    private IMongoCollection<BsonDocument> Languages => store.Collection("language");

    // ------------------------------------------------------------------
    // Loaders and project resolution
    // ------------------------------------------------------------------

    private async Task<BsonDocument> RequireFileAsync(ObjectId fileId, CancellationToken ct) =>
        await Files.Find(new BsonDocument("_id", fileId)).FirstOrDefaultAsync(ct)
        ?? throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");

    private async Task<BsonDocument> RequireSourceAsync(ObjectId sourceId, CancellationToken ct) =>
        await Sources.Find(new BsonDocument("_id", sourceId)).FirstOrDefaultAsync(ct)
        ?? throw SourceNotExist();

    private async Task<BsonDocument> RequireTranslationAsync(ObjectId translationId, CancellationToken ct) =>
        await Translations.Find(new BsonDocument("_id", translationId)).FirstOrDefaultAsync(ct)
        ?? throw TranslationNotExist();

    private async Task<BsonDocument> RequireTipAsync(ObjectId tipId, CancellationToken ct) =>
        await Tips.Find(new BsonDocument("_id", tipId)).FirstOrDefaultAsync(ct)
        ?? throw TipNotExist();

    private async Task<BsonDocument?> FindTargetAsync(ObjectId projectId, ObjectId targetId, CancellationToken ct) =>
        await Targets.Find(new BsonDocument { ["_id"] = targetId, ["t"] = projectId }).FirstOrDefaultAsync(ct);

    private async Task<BsonDocument> RequireTargetAsync(ObjectId projectId, ObjectId targetId, CancellationToken ct) =>
        await FindTargetAsync(projectId, targetId, ct) ?? throw TargetNotExist();

    private async Task<ObjectId> ProjectIdOfFileAsync(ObjectId fileId, CancellationToken ct)
    {
        var file = await RequireFileAsync(fileId, ct);
        return ObjectIdOf(file.GetValue("p", BsonNull.Value)) ?? throw ProjectNotExist();
    }

    private async Task<ObjectId> ProjectIdOfSourceAsync(ObjectId sourceId, CancellationToken ct)
    {
        var source = await RequireSourceAsync(sourceId, ct);
        return await ProjectIdOfFileAsync(SourceFileId(source), ct);
    }

    private async Task<ObjectId> ProjectIdOfTranslationAsync(ObjectId translationId, CancellationToken ct)
    {
        var translation = await RequireTranslationAsync(translationId, ct);
        return await ProjectIdOfSourceAsync(TranslationSourceId(translation), ct);
    }

    private async Task<ObjectId> ProjectIdOfTipAsync(ObjectId tipId, CancellationToken ct)
    {
        var tip = await RequireTipAsync(tipId, ct);
        return await ProjectIdOfSourceAsync(TipSourceId(tip), ct);
    }

    private async Task<int> NextSourceRankAsync(ObjectId fileId, CancellationToken ct)
    {
        var last = await Sources.Find(new BsonDocument("f", fileId))
            .Sort(new BsonDocument { ["r"] = -1, ["_id"] = -1 })
            .FirstOrDefaultAsync(ct);
        return last is null ? 0 : IntOf(last.GetValue("r", 0)) + 1;
    }

    private static ObjectId SourceFileId(BsonDocument source) =>
        ObjectIdOf(source.GetValue("f", BsonNull.Value)) ?? throw SourceNotExist();

    private static ObjectId TranslationSourceId(BsonDocument translation) =>
        ObjectIdOf(translation.GetValue("o", BsonNull.Value)) ?? throw TranslationNotExist();

    private static ObjectId TranslationTargetId(BsonDocument translation) =>
        ObjectIdOf(translation.GetValue("t", BsonNull.Value)) ?? throw TargetNotExist();

    private static ObjectId TipSourceId(BsonDocument tip) =>
        ObjectIdOf(tip.GetValue("o", BsonNull.Value)) ?? throw TipNotExist();

    private static bool IsOwnedBy(BsonDocument document, BsonDocument actor)
    {
        var owner = ObjectIdOf(document.GetValue("u", BsonNull.Value));
        return owner is not null && owner == IdValue(actor);
    }

    private static void RequireImage(BsonDocument file, string message)
    {
        if (IntOf(file.GetValue("t", 0)) != ImageType)
            throw new ApiError("FileTypeNotSupportError", 108, message);
    }

    private static void RequireActivated(BsonDocument file)
    {
        if (!file.Flag("ac"))
            throw new ApiError("FileNotActivatedError", 4016, "不能操作非激活修订版");
    }

    private static void ValidateRange(double value, string field)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw ApiError.Field(field, "Must be greater than or equal to 0 and less than or equal to 1.");
    }

    private static void ValidatePositionType(int value)
    {
        if (value is not (PositionIn or PositionOut))
            throw ApiError.Field("position_type", "Must be one of: 1, 2.");
    }

    // ------------------------------------------------------------------
    // Translator list
    // ------------------------------------------------------------------

    private async Task<LabelListResult> BuildTranslatorListAsync(
        HttpContext context,
        BsonDocument actor,
        BsonDocument file,
        BsonDocument target,
        bool paging,
        CancellationToken ct)
    {
        var page = paging ? ParseTranslatorPagination(context.Request) : new Pagination(1, 30);
        var filter = new BsonDocument("f", IdValue(file));
        var total = await Sources.CountDocumentsAsync(filter, cancellationToken: ct);

        var query = Sources.Find(filter).Sort(new BsonDocument { ["r"] = 1, ["_id"] = 1 });
        if (paging) query = query.Skip(page.Skip).Limit(page.Limit);
        var pageRows = await query.ToListAsync(ct);

        // Python slices by rank first and only then filters blank rows, so a
        // page can contain fewer than `limit` items.
        var rows = pageRows.Where(row => !row.Flag("b")).ToList();
        var sourceIds = rows.Select(IdValue).ToList();
        var targetId = IdValue(target);
        var actorId = IdValue(actor);
        var targetView = await TargetViewAsync(context, target, ct);

        var myRows = new List<BsonDocument>();
        var otherRows = new List<BsonDocument>();
        var tipRows = new List<BsonDocument>();
        var otherLanguage = new HashSet<ObjectId>();
        if (sourceIds.Count > 0)
        {
            var inSources = new BsonDocument("$in", new BsonArray(sourceIds));
            myRows = await Translations.Find(new BsonDocument
            {
                ["o"] = inSources,
                ["u"] = actorId,
                ["t"] = targetId
            }).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);

            otherRows = await Translations.Find(new BsonDocument
            {
                ["o"] = inSources,
                ["u"] = new BsonDocument("$ne", actorId),
                ["t"] = targetId
            }).Sort(new BsonDocument
            {
                ["s"] = -1, ["p"] = -1, ["e"] = -1, ["ct"] = -1, ["_id"] = -1
            }).ToListAsync(ct);

            var otherLanguageRows = await Translations.Find(new BsonDocument
            {
                ["o"] = inSources,
                ["t"] = new BsonDocument("$ne", targetId)
            }).Project(new BsonDocument("o", 1)).ToListAsync(ct);
            foreach (var row in otherLanguageRows)
            {
                var id = ObjectIdOf(row.GetValue("o", BsonNull.Value));
                if (id is not null) otherLanguage.Add(id.Value);
            }

            tipRows = await Tips.Find(new BsonDocument
            {
                ["o"] = inSources,
                ["t"] = targetId
            }).Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 }).ToListAsync(ct);
        }

        var userIds = new HashSet<ObjectId>();
        foreach (var row in myRows.Concat(otherRows))
        {
            AddUserId(userIds, row, "u");
            AddUserId(userIds, row, "pr");
            AddUserId(userIds, row, "sr");
        }
        foreach (var row in tipRows) AddUserId(userIds, row, "u");
        var users = await UsersAsync(userIds, context, ct);

        var myBySource = new Dictionary<ObjectId, BsonDocument>();
        foreach (var row in myRows) myBySource[TranslationSourceId(row)] = row;
        var othersBySource = otherRows.GroupBy(TranslationSourceId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var tipsBySource = tipRows.GroupBy(TipSourceId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var items = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var sourceId = IdValue(row);
            var item = SourceView(row);
            item["translations"] = othersBySource.TryGetValue(sourceId, out var others)
                ? others.Select(value => BuildTranslationView(value, users, targetView)).ToList()
                : new List<Dictionary<string, object?>>();
            item["tips"] = tipsBySource.TryGetValue(sourceId, out var tips)
                ? tips.Select(value => BuildTipView(value, users)).ToList()
                : new List<Dictionary<string, object?>>();
            item["has_other_language_translation"] = otherLanguage.Contains(sourceId);
            if (myBySource.TryGetValue(sourceId, out var mine))
                item["my_translation"] = BuildTranslationView(mine, users, targetView);
            items.Add(item);
        }

        return new LabelListResult(items, total, page.Page, page.Limit, paging);
    }

    private async Task<Dictionary<string, object?>> TranslationViewAsync(
        HttpContext context,
        BsonDocument translation,
        BsonDocument target,
        CancellationToken ct)
    {
        var ids = new HashSet<ObjectId>();
        AddUserId(ids, translation, "u");
        AddUserId(ids, translation, "pr");
        AddUserId(ids, translation, "sr");
        var users = await UsersAsync(ids, context, ct);
        var targetView = await TargetViewAsync(context, target, ct);
        return BuildTranslationView(translation, users, targetView);
    }

    private async Task<Dictionary<string, object?>> TipViewAsync(
        HttpContext context,
        BsonDocument tip,
        CancellationToken ct)
    {
        var ids = new HashSet<ObjectId>();
        AddUserId(ids, tip, "u");
        return BuildTipView(tip, await UsersAsync(ids, context, ct));
    }

    private static Dictionary<string, object?> SourceView(BsonDocument source) => new()
    {
        ["id"] = IdValue(source).ToString(),
        ["content"] = source.Text("c"),
        ["x"] = DoubleOf(source.GetValue("x", 0)),
        ["y"] = DoubleOf(source.GetValue("y", 0)),
        ["position_type"] = IntOf(source.GetValue("p", PositionIn)),
        ["machine"] = source.GetValue("cm", true).ToBoolean()
    };

    private static Dictionary<string, object?> BuildTranslationView(
        BsonDocument translation,
        IReadOnlyDictionary<string, Dictionary<string, object?>?> users,
        Dictionary<string, object?> targetView) => new()
    {
        ["source_id"] = TranslationSourceId(translation).ToString(),
        ["id"] = IdValue(translation).ToString(),
        ["mt"] = translation.Flag("mt"),
        ["content"] = translation.Text("c"),
        ["user"] = UserViewOrNull(users, translation, "u"),
        ["proofread_content"] = translation.Text("p"),
        ["proofreader"] = UserViewOrNull(users, translation, "pr"),
        ["selected"] = translation.Flag("s"),
        ["selector"] = UserViewOrNull(users, translation, "sr"),
        ["create_time"] = translation.Timestamp("ct"),
        ["edit_time"] = translation.Timestamp("e"),
        ["target"] = targetView
    };

    private static Dictionary<string, object?> BuildTipView(
        BsonDocument tip,
        IReadOnlyDictionary<string, Dictionary<string, object?>?> users) => new()
    {
        ["source_id"] = TipSourceId(tip).ToString(),
        ["id"] = IdValue(tip).ToString(),
        ["content"] = tip.Text("c"),
        ["user"] = UserViewOrNull(users, tip, "u"),
        ["create_time"] = tip.Timestamp("ct"),
        ["edit_time"] = tip.Timestamp("et")
    };

    private async Task<Dictionary<string, object?>> TargetViewAsync(
        HttpContext context,
        BsonDocument target,
        CancellationToken ct)
    {
        var languageId = ObjectIdOf(target.GetValue("l", BsonNull.Value));
        var language = languageId is null
            ? null
            : await Languages.Find(new BsonDocument("_id", languageId.Value)).FirstOrDefaultAsync(ct);
        return new Dictionary<string, object?>
        {
            ["id"] = IdValue(target).ToString(),
            ["language"] = LanguageView(language, languageId, context),
            ["translated_source_count"] = target.Integer("tsc", 0),
            ["checked_source_count"] = target.Integer("csc", 0),
            ["create_time"] = target.Timestamp("ct"),
            ["edit_time"] = target.Timestamp("e"),
            ["intro"] = target.Text("i")
        };
    }

    private static Dictionary<string, object?> LanguageView(
        BsonDocument? language,
        ObjectId? fallbackId,
        HttpContext context)
    {
        if (language is null)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = fallbackId?.ToString() ?? "",
                ["en_name"] = "",
                ["lo_name"] = "",
                ["i18n_name"] = "",
                ["no_space"] = false,
                ["code"] = "",
                ["g_tra_code"] = "",
                ["g_ocr_code"] = "",
                ["sort"] = 0
            };
        }

        var chinese = false;
        try
        {
            chinese = LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
        }
        catch (FormatException)
        {
        }

        var localized = LanguageCatalog.DisplayName(language, chinese);
        return new Dictionary<string, object?>
        {
            ["id"] = IdValue(language).ToString(),
            ["en_name"] = language.Text("e"),
            ["lo_name"] = localized,
            ["i18n_name"] = localized,
            ["no_space"] = language.Flag("n"),
            ["code"] = language.Text("co"),
            ["g_tra_code"] = language.Text("gt"),
            ["g_ocr_code"] = language.Text("go"),
            ["sort"] = IntOf(language.GetValue("s", 0))
        };
    }

    private async Task<Dictionary<string, Dictionary<string, object?>?>> UsersAsync(
        IEnumerable<ObjectId> ids,
        HttpContext context,
        CancellationToken ct)
    {
        var unique = ids.Distinct().ToList();
        var result = new Dictionary<string, Dictionary<string, object?>?>();
        if (unique.Count == 0) return result;

        var rows = await store.Collection("user").Find(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(unique))
        }).ToListAsync(ct);
        foreach (var row in rows)
        {
            result[IdValue(row).ToString()] = await userView.ToApiAsync(row, context);
        }
        return result;
    }

    private static Dictionary<string, object?>? UserViewOrNull(
        IReadOnlyDictionary<string, Dictionary<string, object?>?> users,
        BsonDocument document,
        string field)
    {
        var id = ObjectIdOf(document.GetValue(field, BsonNull.Value));
        return id is not null && users.TryGetValue(id.Value.ToString(), out var view) ? view : null;
    }

    private static void AddUserId(ISet<ObjectId> ids, BsonDocument document, string field)
    {
        var id = ObjectIdOf(document.GetValue(field, BsonNull.Value));
        if (id is not null) ids.Add(id.Value);
    }

    // ------------------------------------------------------------------
    // Translation mutation helpers
    // ------------------------------------------------------------------

    private async Task<BsonDocument?> CreateTranslationCoreAsync(
        BsonDocument source,
        BsonDocument file,
        BsonDocument target,
        BsonDocument actor,
        string content,
        LabelSession session,
        CancellationToken ct)
    {
        var sourceId = IdValue(source);
        var targetId = IdValue(target);
        var actorId = IdValue(actor);
        var existing = await Translations.Find(new BsonDocument
        {
            ["o"] = sourceId,
            ["u"] = actorId,
            ["t"] = targetId,
            ["mt"] = false
        }).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            session.AssertEpoch(existing);
            if (content == "" && existing.Text("p") == "")
            {
                await ClearTranslationCoreAsync(existing, source, file, session, ct);
                return null;
            }

            var editTime = await NextEditTimeAsync(sourceId, targetId, existing, ct);
            await Translations.UpdateOneAsync(new BsonDocument("_id", IdValue(existing)),
                new BsonDocument("$set", new BsonDocument
                {
                    ["c"] = content,
                    ["e"] = editTime,
                    ["label_epoch"] = session.Revision
                }), cancellationToken: ct);
            await TouchFileEditTimeAsync(file, session, editTime, ct);
            return await RequireTranslationAsync(IdValue(existing), ct);
        }

        if (content == "") return null;

        var id = ObjectId.GenerateNewId();
        var now = Now();
        var edit = await NextEditTimeAsync(sourceId, targetId, null, ct);
        var translation = new BsonDocument
        {
            ["_id"] = id,
            ["o"] = sourceId,
            ["t"] = targetId,
            ["c"] = content,
            ["u"] = actorId,
            ["p"] = "",
            ["s"] = false,
            ["mt"] = false,
            ["ct"] = now,
            ["e"] = edit,
            ["label_epoch"] = session.Revision
        };
        try
        {
            await Translations.InsertOneAsync(translation, cancellationToken: ct);
        }
        catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
        {
            throw new ApiError("TranslationNotUniqueError", 8007, "翻译不唯一");
        }

        await TouchFileEditTimeAsync(file, session, edit, ct);
        var otherCount = await Translations.CountDocumentsAsync(new BsonDocument
        {
            ["o"] = sourceId,
            ["t"] = targetId,
            ["_id"] = new BsonDocument("$ne", id)
        }, cancellationToken: ct);
        if (otherCount == 0 && !source.Flag("b"))
            await AdjustCountAsync(file, session, "translated_source_count", +1, targetId, ct);
        return translation;
    }

    private async Task ApplySelectionAsync(
        BsonDocument translation,
        BsonDocument source,
        BsonDocument file,
        BsonDocument actor,
        bool selected,
        LabelSession session,
        CancellationToken ct)
    {
        translation = await RequireTranslationAsync(IdValue(translation), ct);
        session.AssertEpoch(translation);
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);
        if (translation.Flag("s") == selected) return;

        var sourceId = TranslationSourceId(translation);
        var targetId = TranslationTargetId(translation);
        var translationId = IdValue(translation);
        var now = Now();

        if (selected)
        {
            var otherSelected = await Translations.Find(new BsonDocument
            {
                ["o"] = sourceId,
                ["t"] = targetId,
                ["s"] = true,
                ["_id"] = new BsonDocument("$ne", translationId)
            }).FirstOrDefaultAsync(ct);

            if (otherSelected is not null)
            {
                await Translations.UpdateManyAsync(new BsonDocument
                {
                    ["o"] = sourceId,
                    ["t"] = targetId,
                    ["_id"] = new BsonDocument("$ne", translationId)
                }, new BsonDocument
                {
                    ["$set"] = new BsonDocument
                    {
                        ["s"] = false,
                        ["label_epoch"] = session.Revision
                    },
                    ["$unset"] = new BsonDocument("sr", "")
                }, cancellationToken: ct);
            }

            await Translations.UpdateOneAsync(new BsonDocument("_id", translationId),
                new BsonDocument
                {
                    ["$set"] = new BsonDocument
                    {
                        ["s"] = true,
                        ["sr"] = IdValue(actor),
                        ["e"] = now,
                        ["label_epoch"] = session.Revision
                    }
                }, cancellationToken: ct);

            if (otherSelected is null && !source.Flag("b"))
                await AdjustCountAsync(file, session, "checked_source_count", +1, targetId, ct);
        }
        else
        {
            await Translations.UpdateOneAsync(new BsonDocument("_id", translationId),
                new BsonDocument
                {
                    ["$set"] = new BsonDocument
                    {
                        ["s"] = false,
                        ["e"] = now,
                        ["label_epoch"] = session.Revision
                    },
                    ["$unset"] = new BsonDocument("sr", "")
                }, cancellationToken: ct);
            await Translations.UpdateManyAsync(new BsonDocument
            {
                ["o"] = sourceId,
                ["t"] = targetId,
                ["_id"] = new BsonDocument("$ne", translationId)
            }, new BsonDocument
            {
                ["$set"] = new BsonDocument
                {
                    ["s"] = false,
                    ["label_epoch"] = session.Revision
                },
                ["$unset"] = new BsonDocument("sr", "")
            }, cancellationToken: ct);
            await AdjustCountAsync(file, session, "checked_source_count", -1, targetId, ct);
        }

        await TouchFileEditTimeAsync(file, session, now, ct);
    }

    private async Task ClearTranslationCoreAsync(
        BsonDocument translation,
        BsonDocument source,
        BsonDocument file,
        LabelSession session,
        CancellationToken ct)
    {
        translation = await RequireTranslationAsync(IdValue(translation), ct);
        session.AssertEpoch(translation);
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);
        var translationId = IdValue(translation);
        var sourceId = TranslationSourceId(translation);
        var targetId = TranslationTargetId(translation);

        var others = await Translations.CountDocumentsAsync(new BsonDocument
        {
            ["o"] = sourceId,
            ["t"] = targetId,
            ["_id"] = new BsonDocument("$ne", translationId)
        }, cancellationToken: ct);

        if (translation.Flag("s"))
            await AdjustCountAsync(file, session, "checked_source_count", -1, targetId, ct);
        if (others == 0)
            await AdjustCountAsync(file, session, "translated_source_count", -1, targetId, ct);

        await Translations.DeleteOneAsync(new BsonDocument("_id", translationId), ct);
    }

    private async Task<BsonDocument> CreateTipCoreAsync(
        BsonDocument source,
        BsonDocument target,
        BsonDocument actor,
        string content,
        LabelSession session,
        CancellationToken ct)
    {
        if (content == "") throw TipEmpty();
        var sourceId = IdValue(source);
        var targetId = IdValue(target);
        var actorId = IdValue(actor);

        var existing = await Tips.Find(new BsonDocument
        {
            ["o"] = sourceId,
            ["t"] = targetId,
            ["u"] = actorId
        }).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            session.AssertEpoch(existing);
            var editTime = await NextTipEditTimeAsync(sourceId, targetId, existing, ct);
            await Tips.UpdateOneAsync(new BsonDocument("_id", IdValue(existing)),
                new BsonDocument("$set", new BsonDocument
                {
                    ["c"] = content,
                    ["et"] = editTime,
                    ["label_epoch"] = session.Revision
                }), cancellationToken: ct);
            return await RequireTipAsync(IdValue(existing), ct);
        }

        var edit = await NextTipEditTimeAsync(sourceId, targetId, null, ct);
        var tip = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = targetId,
            ["c"] = content,
            ["u"] = actorId,
            ["ct"] = edit,
            ["et"] = edit,
            ["label_epoch"] = session.Revision
        };
        try
        {
            await Tips.InsertOneAsync(tip, cancellationToken: ct);
            return tip;
        }
        catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
        {
            // Concurrent same proofreader: re-read and update the winner.
            var winner = await Tips.Find(new BsonDocument
            {
                ["o"] = sourceId,
                ["t"] = targetId,
                ["u"] = actorId
            }).FirstOrDefaultAsync(ct);
            if (winner is null) throw;
            var editTime = await NextTipEditTimeAsync(sourceId, targetId, winner, ct);
            await Tips.UpdateOneAsync(new BsonDocument("_id", IdValue(winner)),
                new BsonDocument("$set", new BsonDocument
                {
                    ["c"] = content,
                    ["et"] = editTime,
                    ["label_epoch"] = session.Revision
                }), cancellationToken: ct);
            return await RequireTipAsync(IdValue(winner), ct);
        }
    }

    private async Task<DateTime> NextEditTimeAsync(
        ObjectId sourceId,
        ObjectId targetId,
        BsonDocument? current,
        CancellationToken ct)
    {
        var last = await Translations.Find(new BsonDocument
        {
            ["o"] = sourceId,
            ["t"] = targetId
        }).Sort(new BsonDocument { ["e"] = -1, ["_id"] = -1 }).FirstOrDefaultAsync(ct);
        var previous = MaxTime(last is null ? null : DateOf(last.GetValue("e", BsonNull.Value)),
            current is null ? null : DateOf(current.GetValue("e", BsonNull.Value)));
        var now = Now();
        return previous is not null && now <= previous.Value ? previous.Value.AddMilliseconds(1) : now;
    }

    private async Task<DateTime> NextTipEditTimeAsync(
        ObjectId sourceId,
        ObjectId targetId,
        BsonDocument? current,
        CancellationToken ct)
    {
        var last = await Tips.Find(new BsonDocument
        {
            ["o"] = sourceId,
            ["t"] = targetId
        }).Sort(new BsonDocument { ["et"] = -1, ["_id"] = -1 }).FirstOrDefaultAsync(ct);
        var previous = MaxTime(last is null ? null : DateOf(last.GetValue("et", BsonNull.Value)),
            current is null ? null : DateOf(current.GetValue("et", BsonNull.Value)));
        var now = Now();
        return previous is not null && now <= previous.Value ? previous.Value.AddMilliseconds(1) : now;
    }

    private static DateTime? MaxTime(DateTime? left, DateTime? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static DateTime? DateOf(BsonValue value) => value switch
    {
        BsonDateTime date => date.ToUniversalTime(),
        BsonTimestamp timestamp => DateTimeOffset.FromUnixTimeSeconds(timestamp.Timestamp).UtcDateTime,
        _ => null
    };

    // ------------------------------------------------------------------
    // Rank movement
    // ------------------------------------------------------------------

    private async Task MoveAheadAsync(
        BsonDocument source,
        BsonDocument file,
        ObjectId? nextId,
        LabelSession session,
        CancellationToken ct)
    {
        var sourceId = IdValue(source);
        var fileId = IdValue(file);
        var sourceRank = IntOf(source.GetValue("r", 0));

        if (nextId is null)
        {
            var last = await Sources.Find(new BsonDocument("f", fileId))
                .Sort(new BsonDocument { ["r"] = -1, ["_id"] = -1 })
                .FirstOrDefaultAsync(ct);
            if (last is null || IdValue(last) == sourceId) return;
            if (file.Flag("sm")) throw SourceMoving();

            await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["sm"] = true,
                    ["label_epoch"] = session.Revision
                }), cancellationToken: ct);
            try
            {
                await Sources.UpdateManyAsync(new BsonDocument
                {
                    ["f"] = fileId,
                    ["r"] = new BsonDocument("$gt", sourceRank)
                }, new BsonDocument
                {
                    ["$inc"] = new BsonDocument("r", -1),
                    ["$set"] = new BsonDocument("label_epoch", session.Revision)
                }, cancellationToken: ct);
                await Sources.UpdateOneAsync(new BsonDocument("_id", sourceId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["r"] = IntOf(last.GetValue("r", 0)),
                        ["label_epoch"] = session.Revision
                    }), cancellationToken: ct);
            }
            finally
            {
                await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["sm"] = false,
                        ["label_epoch"] = session.Revision
                    }), cancellationToken: CancellationToken.None);
            }
            return;
        }

        var next = await Sources.Find(new BsonDocument("_id", nextId.Value)).FirstOrDefaultAsync(ct);
        if (next is null) throw SourceNotExist();
        if (SourceFileId(next) != fileId) throw SourceNotExist();
        if (nextId.Value == sourceId) return;

        var nextRank = IntOf(next.GetValue("r", 0));
        if (sourceRank + 1 == nextRank) return;
        if (file.Flag("sm")) throw SourceMoving();

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument("$set", new BsonDocument
            {
                ["sm"] = true,
                ["label_epoch"] = session.Revision
            }), cancellationToken: ct);
        try
        {
            if (sourceRank > nextRank)
            {
                await Sources.UpdateManyAsync(new BsonDocument
                {
                    ["f"] = fileId,
                    ["r"] = new BsonDocument { ["$gte"] = nextRank, ["$lt"] = sourceRank }
                }, new BsonDocument
                {
                    ["$inc"] = new BsonDocument("r", 1),
                    ["$set"] = new BsonDocument("label_epoch", session.Revision)
                }, cancellationToken: ct);
                await Sources.UpdateOneAsync(new BsonDocument("_id", sourceId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["r"] = nextRank,
                        ["label_epoch"] = session.Revision
                    }), cancellationToken: ct);
            }
            else
            {
                await Sources.UpdateManyAsync(new BsonDocument
                {
                    ["f"] = fileId,
                    ["r"] = new BsonDocument { ["$gt"] = sourceRank, ["$lt"] = nextRank }
                }, new BsonDocument
                {
                    ["$inc"] = new BsonDocument("r", -1),
                    ["$set"] = new BsonDocument("label_epoch", session.Revision)
                }, cancellationToken: ct);
                await Sources.UpdateOneAsync(new BsonDocument("_id", sourceId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["r"] = nextRank - 1,
                        ["label_epoch"] = session.Revision
                    }), cancellationToken: ct);
            }
        }
        finally
        {
            await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["sm"] = false,
                    ["label_epoch"] = session.Revision
                }), cancellationToken: CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------
    // Source deletion
    // ------------------------------------------------------------------

    private async Task ClearSourceAsync(
        BsonDocument source,
        BsonDocument file,
        LabelSession session,
        CancellationToken ct)
    {
        source = await RequireSourceAsync(IdValue(source), ct);
        session.AssertEpoch(source);
        var sourceId = IdValue(source);

        var translations = await Translations.Find(new BsonDocument("o", sourceId)).ToListAsync(ct);
        var tips = await Tips.Find(new BsonDocument("o", sourceId)).ToListAsync(ct);
        // Python's cascade calls Translation.clear/Tip.delete, each of which
        // validates the row epoch. Validate every child before touching counts so
        // a stale row cannot leave a half-applied cascade.
        foreach (var translation in translations) session.AssertEpoch(translation);
        foreach (var tip in tips) session.AssertEpoch(tip);

        foreach (var group in translations.GroupBy(TranslationTargetId))
        {
            if (group.Any(row => row.Flag("s")))
                await AdjustCountAsync(file, session, "checked_source_count", -1, group.Key, ct);
            if (group.Any())
                await AdjustCountAsync(file, session, "translated_source_count", -1, group.Key, ct);
            await Translations.DeleteManyAsync(new BsonDocument
            {
                ["o"] = sourceId,
                ["t"] = group.Key
            }, ct);
        }

        await Tips.DeleteManyAsync(new BsonDocument("o", sourceId), ct);
        await Sources.DeleteOneAsync(new BsonDocument("_id", sourceId), ct);
        await AdjustCountAsync(file, session, "source_count", -1, null, ct);
    }

    // ------------------------------------------------------------------
    // Cache maintenance
    // ------------------------------------------------------------------

    private async Task TouchSourceEditTimeAsync(
        BsonDocument source,
        LabelSession session,
        DateTime editTime,
        CancellationToken ct)
    {
        await Sources.UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument
            {
                ["e"] = editTime,
                ["label_epoch"] = session.Revision
            }), cancellationToken: ct);
    }

    private async Task TouchFileEditTimeAsync(
        BsonDocument file,
        LabelSession session,
        DateTime editTime,
        CancellationToken ct)
    {
        var ids = FileIds(file);
        await Files.UpdateManyAsync(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(ids))
        }, new BsonDocument("$set", new BsonDocument
        {
            ["et"] = editTime,
            ["label_epoch"] = session.Revision
        }), cancellationToken: ct);

        var projectId = ObjectIdOf(file.GetValue("p", BsonNull.Value));
        if (projectId is null) return;
        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId.Value),
            new BsonDocument("$set", new BsonDocument("m_e", editTime)), cancellationToken: ct);

        if (ObjectIdOf(session.Project.GetValue("ps", BsonNull.Value)) is { } setId)
        {
            await store.Collection("project_set").UpdateOneAsync(new BsonDocument("_id", setId),
                new BsonDocument("$set", new BsonDocument("et", editTime)), cancellationToken: ct);
        }
        if (ObjectIdOf(session.Project.GetValue("t", BsonNull.Value)) is { } teamId)
        {
            await store.Collection("team").UpdateOneAsync(new BsonDocument("_id", teamId),
                new BsonDocument("$set", new BsonDocument("m_e", editTime)), cancellationToken: ct);
        }
    }

    private async Task AdjustSourceCountAsync(
        BsonDocument file,
        LabelSession session,
        int step,
        CancellationToken ct) =>
        await AdjustCountAsync(file, session, "source_count", step, null, ct);

    private async Task AdjustCountAsync(
        BsonDocument file,
        LabelSession session,
        string cacheName,
        int step,
        ObjectId? targetId,
        CancellationToken ct)
    {
        if (step == 0) return;
        var ids = FileIds(file);
        var fileField = cacheName switch
        {
            "source_count" => "sc",
            "translated_source_count" => "tsc",
            "checked_source_count" => "csc",
            _ => throw new ArgumentOutOfRangeException(nameof(cacheName))
        };

        await Files.UpdateManyAsync(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(ids))
        }, new BsonDocument
        {
            ["$inc"] = new BsonDocument(fileField, step),
            ["$set"] = new BsonDocument("label_epoch", session.Revision)
        }, cancellationToken: ct);

        var projectId = ObjectIdOf(file.GetValue("p", BsonNull.Value));
        if (projectId is not null)
        {
            await Projects.UpdateOneAsync(new BsonDocument("_id", projectId.Value),
                new BsonDocument("$inc", new BsonDocument(fileField, step)), cancellationToken: ct);
        }

        if (targetId is null) return;

        var cacheField = cacheName == "translated_source_count" ? "ts" : "cs";
        await FileTargetCaches.UpdateManyAsync(new BsonDocument
        {
            ["f"] = new BsonDocument("$in", new BsonArray(ids)),
            ["t"] = targetId.Value
        }, new BsonDocument
        {
            ["$inc"] = new BsonDocument(cacheField, step),
            ["$set"] = new BsonDocument("label_epoch", session.Revision)
        }, cancellationToken: ct);

        await Targets.UpdateOneAsync(new BsonDocument("_id", targetId.Value),
            new BsonDocument
            {
                ["$inc"] = new BsonDocument(fileField, step),
                ["$set"] = new BsonDocument("label_epoch", session.Revision)
            }, cancellationToken: ct);
    }

    private static List<ObjectId> FileIds(BsonDocument file)
    {
        var ids = new List<ObjectId>();
        if (file.TryGetValue("a", out var ancestors) && ancestors is BsonArray array)
        {
            foreach (var value in array)
            {
                var id = ObjectIdOf(value);
                if (id is not null) ids.Add(id.Value);
            }
        }
        ids.Add(IdValue(file));
        return ids.Distinct().ToList();
    }

    private async Task HeartbeatQuietlyAsync(
        BsonDocument project,
        BsonDocument actor,
        CancellationToken ct)
    {
        try
        {
            var presence = services.GetService<PresenceService>();
            if (presence is null) return;
            await presence.HeartbeatAsync(project, actor, "editing_text", ct);
        }
        catch (Exception)
        {
            // Presence is best effort and must never fail a label write.
        }
    }

    // ------------------------------------------------------------------
    // Indexes
    // ------------------------------------------------------------------

    private async Task EnsureIndexAsync(
        string collectionName,
        BsonDocument keys,
        string name,
        bool unique = false,
        CancellationToken ct = default)
    {
        var builder = Builders<BsonDocument>.IndexKeys;
        var definitions = keys.Elements.Select(element =>
            element.Value.ToInt32() >= 0
                ? builder.Ascending(element.Name)
                : builder.Descending(element.Name)).ToArray();
        IndexKeysDefinition<BsonDocument> definition = definitions.Length == 1
            ? definitions[0]
            : builder.Combine(definitions);

        try
        {
            await store.Collection(collectionName).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(definition,
                    new CreateIndexOptions { Name = name, Unique = unique }),
                cancellationToken: ct);
        }
        catch (MongoCommandException error) when (error.Code is 85 or 86)
        {
            // The production database already carries the legacy unique index
            // under another name. Equivalent key specifications are compatible.
        }
    }

    // ------------------------------------------------------------------
    // Small BSON helpers and error factories
    // ------------------------------------------------------------------

    private static ObjectId IdValue(BsonDocument document) =>
        ObjectIdOf(document.GetValue("_id", BsonNull.Value)) ??
        throw ApiError.Invalid("Invalid object id");

    private static ObjectId? ObjectIdOf(BsonValue value)
    {
        if (value.IsObjectId) return value.AsObjectId;
        if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed)) return parsed;
        return null;
    }

    private static double DoubleOf(BsonValue value)
    {
        try { return value.IsNumeric ? value.ToDouble() : 0; }
        catch (Exception) { return 0; }
    }

    private static int IntOf(BsonValue value)
    {
        try { return value.IsNumeric ? value.ToInt32() : 0; }
        catch (Exception) { return 0; }
    }

    private static ApiError SourceNotExist() =>
        new("SourceNotExistError", 8005, "原文不存在，已被删除");

    private static ApiError TranslationNotExist() =>
        new("NotExistError", 104, "未定义的错误");

    private static ApiError TipNotExist() =>
        new("NotExistError", 104, "未定义的错误");

    private static ApiError TargetNotExist() =>
        new("TargetNotExistError", 4024, "项目目标语言不存在");

    private static ApiError ProjectNotExist() =>
        new("ProjectNotExistError", 4001, "项目不存在");

    private static ApiError SourceMoving() =>
        new("SourceMovingError", 8006, "正在移动顺序，请稍后尝试");

    private static ApiError TipEmpty() =>
        new("TipEmptyError", 4021, "备注不能为空");
}
