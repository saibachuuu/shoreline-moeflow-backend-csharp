using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed class LanguageCatalog(MongoStore store)
{
    private sealed record SeedLanguage(string Code, string EnName, string LoName, string GTraCode,
        string GOcrCode, bool NoSpace, string LocalizedEn);
    private static readonly SeedLanguage[] Defaults = LoadDefaults();
    // Python translates language names from the gettext catalog; the English
    // name is not always row.c (some legacy rows store a Chinese label), so key
    // the fallback catalog by the stable language code instead of the label.
    private static readonly FrozenDictionary<string, string> English = Defaults.ToFrozenDictionary(l => l.Code, l => l.LocalizedEn, StringComparer.Ordinal);

    private static SeedLanguage[] LoadDefaults()
    {
        using var stream = typeof(LanguageCatalog).Assembly.GetManifestResourceStream("Shoreline.SystemLanguages")
            ?? throw new InvalidOperationException("Missing system language resource");
        return JsonSerializer.Deserialize<SeedLanguage[]>(stream, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException("Invalid system language resource");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var languages = store.Collection("language");
        await languages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("co"),
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        var seeds = store.Collection("csharp_seed");
        var key = new BsonDocument("_id", "languages-v1");
        var marker = await seeds.Find(key).FirstOrDefaultAsync(ct);
        // An existing Python catalog may be customized: leave it entirely alone.
        // A marker is written before seeding, so an interrupted new installation
        // resumes instead of mistaking a partially seeded catalog for an old one.
        if (marker is null)
        {
            if (await languages.Find(FilterDefinition<BsonDocument>.Empty).AnyAsync(ct))
            {
                // A concurrent initializer may have inserted its first rows
                // since our marker read. Join that initialization if present.
                marker = await seeds.Find(key).FirstOrDefaultAsync(ct);
                if (marker is null) return;
            }
            else
            {
                await seeds.UpdateOneAsync(key, new BsonDocument("$setOnInsert", new BsonDocument("complete", false)), new UpdateOptions { IsUpsert = true }, ct);
                marker = await seeds.Find(key).FirstOrDefaultAsync(ct);
            }
        }
        if (marker!.Flag("complete")) return;
        for (var i = 0; i < Defaults.Length; i++)
        {
            var language = Defaults[i];
            var row = new BsonDocument { ["e"] = language.EnName, ["c"] = language.LoName, ["co"] = language.Code,
                ["gt"] = language.GTraCode, ["go"] = language.GOcrCode, ["n"] = language.NoSpace, ["s"] = i };
            try
            {
                await languages.UpdateOneAsync(new BsonDocument("co", language.Code), new BsonDocument("$setOnInsert", row), new UpdateOptions { IsUpsert = true }, ct);
            }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Another starting process inserted this same unique code.
                if (!await languages.Find(new BsonDocument("co", language.Code)).AnyAsync(ct)) throw;
            }
        }
        await seeds.UpdateOneAsync(key, new BsonDocument("$set", new BsonDocument("complete", true)), cancellationToken: ct);
    }

    public async Task<object[]> ListAsync(bool chinese, CancellationToken ct = default)
    {
        var rows = await store.Collection("language").Find(new BsonDocument("co", new BsonDocument("$ne", "")))
            .Sort(new BsonDocument { ["s"] = 1, ["_id"] = 1 }).ToListAsync(ct);
        return rows.Select(row =>
        {
            var localized = DisplayName(row, chinese);
            return (object)new { id = row["_id"].AsObjectId.ToString(), en_name = row.Text("e"), lo_name = localized,
                i18n_name = localized, no_space = row.Flag("n"), code = row.Text("co"), g_tra_code = row.Text("gt"),
                g_ocr_code = row.Text("go"), sort = row.GetValue("s", 0).ToInt32() };
        }).ToArray();
    }

    public static string DisplayName(BsonDocument row, bool chinese)
    {
        var name = row.Text("c");
        return chinese ? name : English.GetValueOrDefault(row.Text("co"), row.Text("e", name));
    }

    public static bool ChinesePreferred(IList<StringWithQualityHeaderValue>? accepted)
    {
        if (accepted is null || accepted.Count == 0) return false;
        // Werkzeug first tries exact available languages; only if none matches
        // does it retry primary language subtags (for example zh-CN -> zh).
        static double Score(IEnumerable<StringWithQualityHeaderValue> headers, string language, bool primary)
        {
            var exact = headers.Where(h => (primary ? h.Value.ToString().Split('-')[0] : h.Value.ToString()).Equals(language, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0) return exact.Max(h => h.Quality ?? 1);
            return headers.Where(h => h.Value == "*").Select(h => h.Quality ?? 1).DefaultIfEmpty(-1).Max();
        }
        var zh = Score(accepted, "zh", false);
        var en = Score(accepted, "en", false);
        if (Math.Max(zh, en) > 0) return zh >= en;
        zh = Score(accepted, "zh", true);
        en = Score(accepted, "en", true);
        return zh > 0 && zh >= en;
    }
}

public static class LanguageEndpoints
{
    public static void MapLanguages(this WebApplication app) => app.MapGet("/v1/languages", async (HttpContext context, LanguageCatalog catalog, CancellationToken ct) =>
    {
        bool chinese;
        try { chinese = LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage); }
        catch (FormatException) { chinese = false; }
        return Results.Json(await catalog.ListAsync(chinese, ct));
    });
}
