using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// E1 label main chain: image sources, translations, tips, caches and the
/// shared project label lock. This is a direct port of the read/write behavior
/// in app/apis/source.py, translation.py and tip.py.
///
/// All public mutations enter the project lease once. Internal helpers never
/// enter the non-reentrant database lock again.
/// </summary>
public sealed partial class LabelService(
    MongoStore store,
    TimeProvider clock,
    IdentityPermissions permissions,
    UserView userView,
    IServiceProvider services,
    ProjectLabelLock? labelLock = null,
    LabelRequestContext? requestContext = null)
{
    public const int ImageType = 2;
    public const int PositionIn = 1;
    public const int PositionOut = 2;

    private readonly ProjectLabelLock labels = labelLock ?? new ProjectLabelLock(store, clock);
    private readonly LabelRequestContext requestScope = requestContext ?? new LabelRequestContext();

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private async Task<LabelSession> EnterAsync(ObjectId projectId, CancellationToken ct) =>
        await labels.EnterAsync(requestScope, projectId, recovery: false, expectedRevision: null, ct);

    // ------------------------------------------------------------------
    // GET /v1/files/{fileId}/sources
    // ------------------------------------------------------------------
    public async Task<LabelListResult> ListSourcesAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId fileId,
        ObjectId targetId,
        bool paging,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var file = await RequireFileAsync(fileId, ct);
        await permissions.RequireProjectAsync(actor, session.Project, "project:ACCESS", ct);

        var target = await FindTargetAsync(projectId, targetId, ct)
                     ?? throw ApiError.Validate("need `target`");
        RequireActivated(file);
        return await BuildTranslatorListAsync(context, actor, file, target, paging, ct);
    }

    // ------------------------------------------------------------------
    // PATCH /v1/files/{fileId}/sources
    // ------------------------------------------------------------------
    public async Task BatchSelectAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId fileId,
        IReadOnlyList<BatchSelection> selections,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var file = await RequireFileAsync(fileId, ct);
        await permissions.RequireProjectAsync(actor, session.Project, "project:CHECK_TRA", ct);
        session.AssertReferenceEpoch(file);

        foreach (var selection in selections)
        {
            var source = await Sources.Find(new BsonDocument
            {
                ["_id"] = selection.SourceId,
                ["f"] = fileId
            }).FirstOrDefaultAsync(ct);
            if (source is null) continue;

            var translation = await Translations.Find(new BsonDocument
            {
                ["_id"] = selection.TranslationId,
                ["o"] = selection.SourceId
            }).FirstOrDefaultAsync(ct);
            if (translation is null) continue;

            session.AssertEpoch(source);
            await ApplySelectionAsync(translation, source, file, actor, selected: true, session, ct);
        }

        await TouchFileEditTimeAsync(file, session, Now(), ct);
    }

    // ------------------------------------------------------------------
    // POST /v1/files/{fileId}/sources
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>> CreateSourceAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId fileId,
        string content,
        double x,
        double y,
        int positionType,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var file = await RequireFileAsync(fileId, ct);
        RequireImage(file, "只有图片文件能添加原文");
        await permissions.RequireProjectAsync(actor, session.Project, "project:ADD_LABEL", ct);

        // Match source.py: file type and permission are checked before the
        // request schema is validated.
        ValidatePositionType(positionType);
        ValidateRange(x, "x");
        ValidateRange(y, "y");
        RequireActivated(file);
        session.AssertEpoch(file);

        var rank = await NextSourceRankAsync(fileId, ct);
        var now = Now();
        var source = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = fileId,
            ["r"] = rank,
            ["c"] = content,
            ["x"] = x,
            ["y"] = y,
            ["v"] = new BsonArray(),
            ["p"] = positionType,
            ["lf"] = true,
            ["b"] = false,
            ["cm"] = false,
            ["pt"] = new BsonArray(),
            ["ct"] = now,
            ["e"] = now,
            ["label_epoch"] = session.Revision
        };
        await Sources.InsertOneAsync(source, cancellationToken: ct);
        await AdjustSourceCountAsync(file, session, +1, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        return SourceView(source);
    }

    // ------------------------------------------------------------------
    // PUT /v1/sources/{sourceId}
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>> EditSourceAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId sourceId,
        SourceEditRequest edit,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfSourceAsync(sourceId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var source = await RequireSourceAsync(sourceId, ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);
        RequireImage(file, "只有图片文件能修改原文");
        await permissions.RequireProjectAsync(actor, session.Project, "project:MOVE_LABEL", ct);

        // Match source.py: type/permission precede schema validation.
        if (!edit.HasAny) throw ApiError.Validate("没有有效参数");
        if (edit.X is not null) ValidateRange(edit.X.Value, "x");
        if (edit.Y is not null) ValidateRange(edit.Y.Value, "y");
        if (edit.PositionType is not null) ValidatePositionType(edit.PositionType.Value);
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);

        var now = Now();
        var set = new BsonDocument
        {
            ["e"] = now,
            ["label_epoch"] = session.Revision
        };
        if (edit.Content is not null) set["c"] = edit.Content;
        if (edit.X is not null) set["x"] = edit.X.Value;
        if (edit.Y is not null) set["y"] = edit.Y.Value;
        if (edit.Vertices is not null) set["v"] = new BsonArray(edit.Vertices);
        if (edit.PositionType is not null) set["p"] = edit.PositionType.Value;

        await Sources.UpdateOneAsync(new BsonDocument("_id", sourceId),
            new BsonDocument("$set", set), cancellationToken: ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        var updated = await RequireSourceAsync(sourceId, ct);
        return SourceView(updated);
    }

    // ------------------------------------------------------------------
    // DELETE /v1/sources/{sourceId}
    // ------------------------------------------------------------------
    public async Task DeleteSourceAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId sourceId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfSourceAsync(sourceId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var source = await RequireSourceAsync(sourceId, ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);
        RequireImage(file, "只有图片文件能删除原文");
        await permissions.RequireProjectAsync(actor, session.Project, "project:DELETE_LABEL", ct);
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);

        var now = Now();
        await TouchSourceEditTimeAsync(source, session, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        await ClearSourceAsync(source, file, session, ct);
    }

    // ------------------------------------------------------------------
    // PUT /v1/sources/{sourceId}/rank
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>> MoveSourceRankAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId sourceId,
        string nextSourceId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfSourceAsync(sourceId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var source = await RequireSourceAsync(sourceId, ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);
        RequireImage(file, "只有图片文件能修改原文顺序");
        await permissions.RequireProjectAsync(actor, session.Project, "project:ADD_LABEL", ct);

        ObjectId? nextId = null;
        if (nextSourceId != "end")
        {
            if (!ObjectId.TryParse(nextSourceId, out var parsed))
                throw ApiError.Field("next_source_id", "Invalid ObjectId.");
            nextId = parsed;
        }
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);

        await MoveAheadAsync(source, file, nextId, session, ct);
        return new Dictionary<string, object?> { ["message"] = LegacySuccessMessages.Get(context, "修改成功") };
    }

    // ------------------------------------------------------------------
    // POST /v1/sources/{sourceId}/translations
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>?> CreateTranslationAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId sourceId,
        string content,
        ObjectId targetId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfSourceAsync(sourceId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var source = await RequireSourceAsync(sourceId, ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);
        await permissions.RequireProjectAsync(actor, session.Project, "project:ADD_TRA", ct);

        var target = await FindTargetAsync(projectId, targetId, ct)
                     ?? throw TargetNotExist();
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);
        session.AssertReferenceEpoch(target);

        var translation = await CreateTranslationCoreAsync(source, file, target, actor, content, session, ct);
        if (translation is null) return null;

        await HeartbeatQuietlyAsync(session.Project, actor, ct);
        return await TranslationViewAsync(context, translation, target, ct);
    }

    // ------------------------------------------------------------------
    // PUT /v1/translations/{translationId}
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>?> EditTranslationAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId translationId,
        TranslationEditRequest edit,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfTranslationAsync(translationId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var translation = await RequireTranslationAsync(translationId, ct);
        var source = await RequireSourceAsync(TranslationSourceId(translation), ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);

        if (!edit.HasAny) throw ApiError.Validate("没有有效参数");

        var target = await RequireTargetAsync(projectId, TranslationTargetId(translation), ct);

        // The Python view can write `selected` and only afterwards discover that
        // content belongs to another user. Pre-validate every requested field so
        // a failed request never leaves a half-applied selection.
        if (edit.Selected is not null)
            await permissions.RequireProjectAsync(actor, session.Project, "project:CHECK_TRA", ct);
        if (edit.Content is not null && !IsOwnedBy(translation, actor))
            throw ApiError.Forbidden();
        if (edit.ProofreadContent is not null)
            await permissions.RequireProjectAsync(actor, session.Project, "project:PROOFREAD_TRA", ct);

        if (edit.Selected is not null)
        {
            await ApplySelectionAsync(translation, source, file, actor, edit.Selected.Value, session, ct);
            translation = await RequireTranslationAsync(translationId, ct);
        }

        if (edit.Content is not null)
        {
            var effectiveProofread = edit.ProofreadContent ?? translation.Text("p");
            if (edit.Content == "" && effectiveProofread == "")
            {
                await ClearTranslationCoreAsync(translation, source, file, session, ct);
                return null;
            }
            translation["c"] = edit.Content;
        }

        if (edit.ProofreadContent is not null)
        {
            var effectiveContent = edit.Content ?? translation.Text("c");
            if (edit.ProofreadContent == "" && effectiveContent == "")
            {
                await ClearTranslationCoreAsync(translation, source, file, session, ct);
                return null;
            }
            translation["p"] = edit.ProofreadContent;
            if (edit.ProofreadContent == "")
                translation.Remove("pr");
            else
                translation["pr"] = IdValue(actor);
        }

        if (edit.Content is not null || edit.ProofreadContent is not null)
        {
            session.AssertEpoch(translation);
            session.AssertReferenceEpoch(source);
            session.AssertReferenceEpoch(file);
            session.AssertReferenceEpoch(target);

            var now = Now();
            var set = new BsonDocument
            {
                ["c"] = translation.Text("c"),
                ["p"] = translation.Text("p"),
                ["e"] = now,
                ["label_epoch"] = session.Revision
            };
            var unset = new BsonDocument();
            if (translation.TryGetValue("pr", out var proofreader) &&
                proofreader != BsonNull.Value && !proofreader.IsBsonNull)
                set["pr"] = proofreader;
            else
                unset["pr"] = "";

            var update = new BsonDocument("$set", set);
            if (unset.ElementCount > 0) update["$unset"] = unset;
            await Translations.UpdateOneAsync(new BsonDocument("_id", translationId),
                update, cancellationToken: ct);
            await TouchFileEditTimeAsync(file, session, now, ct);
        }

        var current = await RequireTranslationAsync(translationId, ct);
        await HeartbeatQuietlyAsync(session.Project, actor, ct);
        return await TranslationViewAsync(context, current, target, ct);
    }

    // ------------------------------------------------------------------
    // DELETE /v1/translations/{translationId}
    // ------------------------------------------------------------------
    public async Task DeleteTranslationAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId translationId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfTranslationAsync(translationId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var translation = await RequireTranslationAsync(translationId, ct);
        var source = await RequireSourceAsync(TranslationSourceId(translation), ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);

        if (!IsOwnedBy(translation, actor))
            await permissions.RequireProjectAsync(actor, session.Project, "project:DELETE_TRA", ct);
        session.AssertEpoch(translation);
        session.AssertReferenceEpoch(source);
        session.AssertReferenceEpoch(file);

        await ClearTranslationCoreAsync(translation, source, file, session, ct);
    }

    // ------------------------------------------------------------------
    // POST /v1/sources/{sourceId}/tips
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>> CreateTipAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId sourceId,
        string content,
        ObjectId targetId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfSourceAsync(sourceId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var source = await RequireSourceAsync(sourceId, ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);
        await permissions.RequireProjectAsync(actor, session.Project, "project:PROOFREAD_TRA", ct);

        var target = await FindTargetAsync(projectId, targetId, ct)
                     ?? throw TargetNotExist();
        session.AssertEpoch(source);
        session.AssertReferenceEpoch(file);
        session.AssertReferenceEpoch(target);

        var tip = await CreateTipCoreAsync(source, target, actor, content, session, ct);
        var now = Now();
        await TouchSourceEditTimeAsync(source, session, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        await HeartbeatQuietlyAsync(session.Project, actor, ct);
        return await TipViewAsync(context, tip, ct);
    }

    // ------------------------------------------------------------------
    // PUT /v1/tips/{tipId}
    // ------------------------------------------------------------------
    public async Task<Dictionary<string, object?>> EditTipAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId tipId,
        string content,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfTipAsync(tipId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var tip = await RequireTipAsync(tipId, ct);
        var source = await RequireSourceAsync(TipSourceId(tip), ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);

        if (!IsOwnedBy(tip, actor)) throw ApiError.Forbidden();
        await permissions.RequireProjectAsync(actor, session.Project, "project:PROOFREAD_TRA", ct);
        if (content.Trim().Length == 0) throw TipEmpty();
        session.AssertEpoch(tip);
        session.AssertReferenceEpoch(source);
        session.AssertReferenceEpoch(file);

        var now = Now();
        await Tips.UpdateOneAsync(new BsonDocument("_id", tipId),
            new BsonDocument("$set", new BsonDocument
            {
                ["c"] = content,
                ["et"] = now,
                ["label_epoch"] = session.Revision
            }), cancellationToken: ct);
        await TouchSourceEditTimeAsync(source, session, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
        await HeartbeatQuietlyAsync(session.Project, actor, ct);
        return await TipViewAsync(context, await RequireTipAsync(tipId, ct), ct);
    }

    // ------------------------------------------------------------------
    // DELETE /v1/tips/{tipId}
    // ------------------------------------------------------------------
    public async Task DeleteTipAsync(
        HttpContext context,
        BsonDocument actor,
        ObjectId tipId,
        CancellationToken ct)
    {
        var projectId = await ProjectIdOfTipAsync(tipId, ct);
        await using var session = await EnterAsync(projectId, ct);
        var tip = await RequireTipAsync(tipId, ct);
        var source = await RequireSourceAsync(TipSourceId(tip), ct);
        var file = await RequireFileAsync(SourceFileId(source), ct);

        if (!IsOwnedBy(tip, actor)) throw ApiError.Forbidden();
        await permissions.RequireProjectAsync(actor, session.Project, "project:PROOFREAD_TRA", ct);
        session.AssertEpoch(tip);
        session.AssertReferenceEpoch(source);
        session.AssertReferenceEpoch(file);

        await Tips.DeleteOneAsync(new BsonDocument("_id", tipId), ct);
        var now = Now();
        await TouchSourceEditTimeAsync(source, session, now, ct);
        await TouchFileEditTimeAsync(file, session, now, ct);
    }

    /// <summary>
    /// Creates the compatibility indexes. Translation/tip unique indexes match
    /// the legacy indexes; existing equivalent indexes are accepted.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsureIndexAsync("source", new BsonDocument("f", 1), "label_source_file", ct: ct);
        await EnsureIndexAsync("source", new BsonDocument("b", 1), "label_source_blank", ct: ct);
        await EnsureIndexAsync("source", new BsonDocument("r", 1), "label_source_rank", ct: ct);

        await EnsureIndexAsync("translation", new BsonDocument("o", 1), "label_translation_source", ct: ct);
        await EnsureIndexAsync("translation", new BsonDocument
        {
            ["s"] = -1, ["p"] = -1, ["e"] = -1, ["ct"] = -1, ["_id"] = -1
        }, "label_translation_order", ct: ct);
        await EnsureIndexAsync("translation", new BsonDocument
        {
            ["o"] = 1, ["t"] = 1, ["u"] = 1
        }, "label_translation_unique", unique: true, ct: ct);

        await EnsureIndexAsync("tip", new BsonDocument("o", 1), "label_tip_source", ct: ct);
        await EnsureIndexAsync("tip", new BsonDocument("et", -1), "label_tip_edit_time", ct: ct);
        await EnsureIndexAsync("tip", new BsonDocument
        {
            ["o"] = 1, ["t"] = 1, ["u"] = 1
        }, "label_tip_unique", unique: true, ct: ct);

        await EnsureIndexAsync("file_target_cache", new BsonDocument
        {
            ["f"] = 1, ["t"] = 1
        }, "label_file_target_cache", ct: ct);
    }

    /// <summary>
    /// Python's MoePagination defaults to 30 and caps at 100.
    /// </summary>
    public static Pagination ParseTranslatorPagination(HttpRequest request)
    {
        var page = int.TryParse(request.Query["page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? Math.Max(1, p)
            : 1;
        var limit = int.TryParse(request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? l
            : 30;
        if (limit < 1) limit = 30;
        if (limit > 100) limit = 100;
        if ((long)(page - 1) * limit > int.MaxValue) throw ApiError.Invalid("Page is too large");
        return new Pagination(page, limit);
    }
}
