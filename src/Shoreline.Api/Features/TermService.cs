using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Direct-service port of app/apis/term.py and app/models/term.py. Routes are
// intentionally registered by TermRegistration and are not wired into Program.cs
// by this slice.
public sealed class TermService(MongoStore store, TimeProvider clock, IdentityPermissions permissions,
    TeamMemberService members, UserView users, IHttpContextAccessor? http = null)
{
    public const int LegacyDefaultPageLimit = 30;
    public const int MaxPageLimit = 100;

    // ---- lookups ---------------------------------------------------------

    public async Task<BsonDocument> FindBankAsync(string id, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(id, out var key)) throw BankMissing();
        return await store.Collection("term_bank").Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw BankMissing();
    }

    public async Task<BsonDocument> FindTermAsync(string id, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(id, out var key)) throw TermMissing();
        return await store.Collection("term").Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw TermMissing();
    }

    private bool Chinese()
    {
        try { return LanguageCatalog.ChinesePreferred(http?.HttpContext?.Request.GetTypedHeaders().AcceptLanguage); }
        catch (FormatException) { return false; }
    }

    private ApiError BankMissing() => new("TermBankNotExistError", 7001,
        Chinese() ? "术语库不存在" : "Termbase does not exist.");
    private ApiError TermMissing() => new("TermNotExistError", 7002,
        Chinese() ? "术语不存在" : "Term does not exist.");

    // ---- team term banks -------------------------------------------------

    public async Task<(IReadOnlyList<BsonDocument> Rows, long Count)> ListBanksAsync(BsonDocument actor,
        string teamId, string? word, int skip, int limit, CancellationToken ct = default)
    {
        var team = await members.GetTeamAsync(teamId, ct);
        if (!(await permissions.TeamSnapshotAsync(actor, team, ct)).Has("team:ACCESS"))
            throw new ApiError("NoPermissionError", 103, "没有权限");

        // Python team.term_banks is TermBank.objects(team=self): the team
        // scope is mandatory, otherwise any team:ACCESS member could list
        // every team's banks.
        var filter = new BsonDocument("t", team["_id"]);
        if (!string.IsNullOrEmpty(word))
            filter["n"] = new BsonRegularExpression(Regex.Escape(word), "i");
        return await PageAsync("term_bank", filter, skip, limit, ct);
    }

    public async Task<BsonDocument> CreateBankAsync(BsonDocument actor, string teamId, JsonElement body,
        CancellationToken ct = default)
    {
        var team = await members.GetTeamAsync(teamId, ct);
        if (!(await permissions.TeamSnapshotAsync(actor, team, ct)).Has("team:CREATE_TERM_BANK"))
            throw new ApiError("NoPermissionError", 103,
                "您没有「创建术语库」权限，不能在此团队创建术语库");

        var form = ReadBank(body);
        var source = await LanguageAsync(form.SourceLanguageId, ct);
        var target = await LanguageAsync(form.TargetLanguageId, ct);
        if (source["_id"].AsObjectId == target["_id"].AsObjectId) throw SameLanguage();

        var now = clock.GetUtcNow().UtcDateTime;
        var bank = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = team["_id"],
            ["n"] = form.Name,
            ["sl"] = source["_id"],
            ["tl"] = target["_id"],
            ["ti"] = form.Tip,
            ["u"] = actor["_id"],
            ["c"] = now,
            ["e"] = now
        };
        await store.Collection("term_bank").InsertOneAsync(bank, cancellationToken: ct);
        return bank;
    }

    public async Task<BsonDocument> EditBankAsync(BsonDocument actor, string bankId, JsonElement body,
        CancellationToken ct = default)
    {
        var bank = await FindBankAsync(bankId, ct);
        var team = await BankTeamAsync(bank, ct);
        var change = await TeamHasAsync(actor, team, "CHANGE_TERM_BANK", ct);
        if (!(change || OwnedBy(bank, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「设置他人术语库」权限，只能设置自己创建的术语库");

        // The legacy TermBankSchema requires every field even on PUT.
        var form = ReadBank(body);
        var source = await LanguageAsync(form.SourceLanguageId, ct);
        var target = await LanguageAsync(form.TargetLanguageId, ct);
        if (source["_id"].AsObjectId == target["_id"].AsObjectId) throw SameLanguage();

        var now = clock.GetUtcNow().UtcDateTime;
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["n"] = form.Name,
            ["sl"] = source["_id"],
            ["tl"] = target["_id"],
            ["ti"] = form.Tip,
            ["e"] = now
        });
        await store.Collection("term_bank").UpdateOneAsync(new BsonDocument("_id", bank["_id"]), update,
            cancellationToken: ct);
        bank["n"] = form.Name;
        bank["sl"] = source["_id"];
        bank["tl"] = target["_id"];
        bank["ti"] = form.Tip;
        bank["e"] = now;
        return bank;
    }

    public async Task DeleteBankAsync(BsonDocument actor, string bankId, CancellationToken ct = default)
    {
        var bank = await FindBankAsync(bankId, ct);
        var team = await BankTeamAsync(bank, ct);
        var remove = await TeamHasAsync(actor, team, "DELETE_TERM_BANK", ct);
        if (!(remove || OwnedBy(bank, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「删除他人术语库」权限，只能删除自己创建的术语库");

        // Python reverse_delete_rule=CASCADE deletes terms, PULL removes the bank
        // from Project.term_banks (tb), and Source.possible_terms (pt) is pulled as
        // part of each cascaded Term deletion. There is no transaction in legacy.
        // Process a bounded cursor batch: materializing every term id and sending
        // one large $in can exceed Mongo's 16 MiB command limit for big banks.
        const int termBatchSize = 200;
        using var terms = await store.Collection("term")
            .Find(new BsonDocument("tb", bank["_id"]), new FindOptions { BatchSize = termBatchSize })
            .Sort(new BsonDocument("_id", 1))
            .ToCursorAsync(ct);
        while (await terms.MoveNextAsync(ct))
        {
            var termIds = terms.Current.Select(term => term["_id"].AsObjectId).ToArray();
            if (termIds.Length == 0) continue;
            var ids = new BsonArray(termIds.Select(id => (BsonValue)id));
            await store.Collection("source").UpdateManyAsync(new BsonDocument("pt", new BsonDocument("$in", ids)),
                new BsonDocument("$pull", new BsonDocument("pt", new BsonDocument("$in", ids))), cancellationToken: ct);
            await store.Collection("term").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", ids)), ct);
        }
        await store.Collection("project").UpdateManyAsync(new BsonDocument("tb", bank["_id"]),
            new BsonDocument("$pull", new BsonDocument("tb", bank["_id"])), cancellationToken: ct);
        await store.Collection("term_bank").DeleteOneAsync(new BsonDocument("_id", bank["_id"]), ct);
    }

    // ---- terms in a bank ------------------------------------------------

    public async Task<(IReadOnlyList<BsonDocument> Rows, long Count)> ListTermsAsync(BsonDocument actor,
        string bankId, string? word, int skip, int limit, CancellationToken ct = default)
    {
        var bank = await FindBankAsync(bankId, ct);
        var team = await BankTeamAsync(bank, ct);
        var access = await TeamHasAsync(actor, team, "ACCESS_TERM_BANK", ct);
        if (!(access || OwnedBy(bank, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「查看他人术语库」权限，只能查看自己创建的术语库中的术语");

        var filter = new BsonDocument("tb", bank["_id"]);
        if (!string.IsNullOrEmpty(word))
        {
            var pattern = new BsonRegularExpression(Regex.Escape(word), "i");
            filter["$or"] = new BsonArray
            {
                new BsonDocument("o", pattern),
                new BsonDocument("t", pattern),
                new BsonDocument("ti", pattern)
            };
        }
        return await PageAsync("term", filter, skip, limit, ct);
    }

    public async Task<BsonDocument> CreateTermAsync(BsonDocument actor, string bankId, JsonElement body,
        CancellationToken ct = default)
    {
        var bank = await FindBankAsync(bankId, ct);
        var team = await BankTeamAsync(bank, ct);
        var create = await TeamHasAsync(actor, team, "CREATE_TERM", ct);
        if (!(create || OwnedBy(bank, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「在他人术语库中增加术语」权限，只能在自己创建的术语库中增加术语");

        var form = ReadTerm(body);
        var now = clock.GetUtcNow().UtcDateTime;
        var term = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["tb"] = bank["_id"],
            ["o"] = form.Source,
            ["t"] = form.Target,
            ["ti"] = form.Tip,
            ["u"] = actor["_id"],
            ["c"] = now,
            ["e"] = now
        };
        await store.Collection("term").InsertOneAsync(term, cancellationToken: ct);
        await MarkProjectsForTermRefreshAsync(bank["_id"].AsObjectId, ct);
        return term;
    }

    public async Task<BsonDocument> EditTermAsync(BsonDocument actor, string termId, JsonElement body,
        CancellationToken ct = default)
    {
        var term = await FindTermAsync(termId, ct);
        var bank = await FindBankAsync(term["tb"].AsObjectId.ToString(), ct);
        var team = await BankTeamAsync(bank, ct);
        var change = await TeamHasAsync(actor, team, "CHANGE_TERM", ct);
        if (!(change || OwnedBy(bank, "u", actor) || OwnedBy(term, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「修改他人术语」权限，只能修改自己创建的术语或自己术语库中的术语");

        var form = ReadTerm(body);
        var sourceChanged = term.Text("o") != form.Source;
        var now = clock.GetUtcNow().UtcDateTime;
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["o"] = form.Source,
            ["t"] = form.Target,
            ["ti"] = form.Tip,
            ["e"] = now
        });
        await store.Collection("term").UpdateOneAsync(new BsonDocument("_id", term["_id"]), update,
            cancellationToken: ct);
        term["o"] = form.Source;
        term["t"] = form.Target;
        term["ti"] = form.Tip;
        term["e"] = now;

        // Legacy only refreshes projects when the source text changed.
        if (sourceChanged) await MarkProjectsForTermRefreshAsync(bank["_id"].AsObjectId, ct);
        return term;
    }

    public async Task DeleteTermAsync(BsonDocument actor, string termId, CancellationToken ct = default)
    {
        var term = await FindTermAsync(termId, ct);
        var bank = await FindBankAsync(term["tb"].AsObjectId.ToString(), ct);
        var team = await BankTeamAsync(bank, ct);
        var remove = await TeamHasAsync(actor, team, "DELETE_TERM", ct);
        if (!(remove || OwnedBy(bank, "u", actor) || OwnedBy(term, "u", actor)))
            throw new ApiError("NoPermissionError", 103,
                "您没有「删除他人术语」权限，只能删除自己创建的术语或自己术语库中的术语");

        // Source.possible_terms uses reverse_delete_rule=PULL.
        await store.Collection("source").UpdateManyAsync(new BsonDocument("pt", term["_id"]),
            new BsonDocument("$pull", new BsonDocument("pt", term["_id"])), cancellationToken: ct);
        await store.Collection("term").DeleteOneAsync(new BsonDocument("_id", term["_id"]), ct);
    }

    // ---- API views -------------------------------------------------------

    public async Task<object> BankViewAsync(BsonDocument bank, HttpContext? context,
        CancellationToken ct = default) => (await BanksViewAsync([bank], context, ct))[0];

    public async Task<object> TermViewAsync(BsonDocument term, HttpContext? context,
        CancellationToken ct = default) => (await TermsViewAsync([term], context, ct))[0];

    public async Task<object[]> BanksViewAsync(IReadOnlyList<BsonDocument> banks, HttpContext? context,
        CancellationToken ct = default)
    {
        var userIds = banks.Select(bank => Ref(bank, "u")).OfType<ObjectId>().Distinct().ToArray();
        var languageIds = banks.SelectMany(bank => new[] { Ref(bank, "sl"), Ref(bank, "tl") })
            .OfType<ObjectId>().Distinct().ToArray();
        var userMap = await LoadAsync("user", userIds, ct);
        var languageMap = await LoadAsync("language", languageIds, ct);

        var rows = new object[banks.Count];
        for (var index = 0; index < banks.Count; index++)
        {
            var bank = banks[index];
            var user = Ref(bank, "u") is { } userId ? userMap.GetValueOrDefault(userId) : null;
            var source = Ref(bank, "sl") is { } sourceId ? languageMap.GetValueOrDefault(sourceId) : null;
            var target = Ref(bank, "tl") is { } targetId ? languageMap.GetValueOrDefault(targetId) : null;
            rows[index] = new Dictionary<string, object?>
            {
                ["id"] = bank["_id"].ToString(),
                ["name"] = bank.Text("n"),
                ["source_language"] = source is null ? null : LanguageView(source),
                ["target_language"] = target is null ? null : LanguageView(target),
                ["tip"] = bank.Text("ti"),
                ["user"] = await UserObjectAsync(user, context),
                ["create_time"] = bank.Timestamp("c"),
                ["edit_time"] = bank.Timestamp("e")
            };
        }
        return rows;
    }

    public async Task<object[]> TermsViewAsync(IReadOnlyList<BsonDocument> terms, HttpContext? context,
        CancellationToken ct = default)
    {
        var userIds = terms.Select(term => Ref(term, "u")).OfType<ObjectId>().Distinct().ToArray();
        var userMap = await LoadAsync("user", userIds, ct);

        var rows = new object[terms.Count];
        for (var index = 0; index < terms.Count; index++)
        {
            var term = terms[index];
            var user = Ref(term, "u") is { } userId ? userMap.GetValueOrDefault(userId) : null;
            rows[index] = new Dictionary<string, object?>
            {
                ["id"] = term["_id"].ToString(),
                ["source"] = term.Text("o"),
                ["target"] = term.Text("t"),
                ["tip"] = term.Text("ti"),
                ["user"] = await UserObjectAsync(user, context),
                ["create_time"] = term.Timestamp("c"),
                ["edit_time"] = term.Timestamp("e")
            };
        }
        return rows;
    }

    // ---- internals -------------------------------------------------------

    private async Task<(IReadOnlyList<BsonDocument> Rows, long Count)> PageAsync(string collection,
        BsonDocument filter, int skip, int limit, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        limit = Math.Clamp(limit, 1, MaxPageLimit);
        var rows = store.Collection(collection);
        var count = await rows.CountDocumentsAsync(filter, cancellationToken: ct);
        var data = await rows.Find(filter).Sort(new BsonDocument("e", -1)).Skip(skip).Limit(limit).ToListAsync(ct);
        return (data, count);
    }

    private async Task<BsonDocument?> BankTeamAsync(BsonDocument bank, CancellationToken ct)
    {
        // System term banks have no team reference at all. They must not be
        // coerced to ObjectId.Empty; only the owner branches can authorize them.
        if (!bank.TryGetValue("t", out var value) || !value.IsObjectId) return null;
        return await store.Collection("team").Find(new BsonDocument("_id", value.AsObjectId))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> TeamHasAsync(BsonDocument actor, BsonDocument? team, string permission,
        CancellationToken ct) => team is not null &&
        (await permissions.TeamSnapshotAsync(actor, team, ct)).Has("team:" + permission);

    private static bool OwnedBy(BsonDocument document, string field, BsonDocument actor) =>
        document.TryGetValue(field, out var owner) && owner.IsObjectId &&
        actor.TryGetValue("_id", out var id) && id.IsObjectId && owner.AsObjectId == id.AsObjectId;

    private async Task<BsonDocument> LanguageAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw InvalidObjectId();
        return await store.Collection("language").Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("LanguageNotExistError", 6001, "语言不存在");
    }

    private static ApiError SameLanguage() => new("TargetAndSourceLanguageSameError", 6002,
        "原语言和目标语言不能相同");

    private static ApiError InvalidObjectId() => new("InvalidObjectIdError", 101, "错误的ObjectId");

    private async Task MarkProjectsForTermRefreshAsync(ObjectId bankId, CancellationToken ct) =>
        await store.Collection("project").UpdateManyAsync(new BsonDocument("tb", bankId),
            new BsonDocument("$set", new BsonDocument("nft", true)), cancellationToken: ct);

    private async Task<Dictionary<ObjectId, BsonDocument>> LoadAsync(string collection, ObjectId[] ids,
        CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await store.Collection(collection)
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids.Select(id => (BsonValue)id)))))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row["_id"].AsObjectId);
    }

    private static ObjectId? Ref(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsObjectId ? value.AsObjectId : null;

    private async Task<object?> UserObjectAsync(BsonDocument? user, HttpContext? context) =>
        user is null ? null : await users.ToApiAsync(user, context ?? new DefaultHttpContext());

    private static Dictionary<string, object?> LanguageView(BsonDocument language) => new()
    {
        ["id"] = language["_id"].ToString(),
        ["en_name"] = language.Text("e"),
        ["lo_name"] = language.Text("c"),
        ["i18n_name"] = language.Text("c"),
        ["no_space"] = language.Flag("n"),
        ["code"] = language.Text("co"),
        ["g_tra_code"] = language.Text("gt"),
        ["g_ocr_code"] = language.Text("go"),
        ["sort"] = language.GetValue("s", 0).ToInt32()
    };

    private static BankForm ReadBank(JsonElement body)
    {
        RequireObject(body);
        return new BankForm(
            Text(body, "name", 1, 40),
            Text(body, "tip", 0, 140),
            ObjectIdText(body, "source_language_id"),
            ObjectIdText(body, "target_language_id"));
    }

    private static TermForm ReadTerm(JsonElement body)
    {
        RequireObject(body);
        return new TermForm(
            Text(body, "source", 1, 40),
            Text(body, "target", 1, 40),
            Text(body, "tip", 0, 140));
    }

    private static void RequireObject(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("请求数据格式错误");
    }

    private static string Text(JsonElement body, string field, int minimum, int maximum)
    {
        if (!body.TryGetProperty(field, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw ApiError.Field(field, "必填");
        if (value.ValueKind != JsonValueKind.String) throw ApiError.Field(field, "必须是字符串");
        var text = value.GetString() ?? "";
        // Marshmallow Length counts Python code points; count runes, not UTF-16 units.
        var length = text.EnumerateRunes().Count();
        if (length < minimum || length > maximum)
            throw ApiError.Field(field, string.Create(CultureInfo.InvariantCulture,
                $"长度为{minimum}到{maximum}个字符"));
        return text;
    }

    private static string ObjectIdText(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw ApiError.Field(field, "必填");
        if (value.ValueKind != JsonValueKind.String) throw ApiError.Field(field, "必须是字符串");
        var text = value.GetString() ?? "";
        if (!ObjectId.TryParse(text, out _)) throw InvalidObjectId();
        return text;
    }

    private sealed record BankForm(string Name, string Tip, string SourceLanguageId, string TargetLanguageId);
    private sealed record TermForm(string Source, string Target, string Tip);
}
