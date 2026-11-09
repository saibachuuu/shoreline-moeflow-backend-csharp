using System.Net.Mail;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed class SiteSettings(MongoStore store)
{
    private static readonly (string Api, string Bson)[] TextFields =
    [
        ("homepage_html", "h"), ("homepage_css", "hc"), ("custom_site_title", "st"),
        ("homepage_welcome", "hw"), ("homepage_image_url", "hi")
    ];

    public async Task<BsonDocument> GetAsync(CancellationToken ct) =>
        await store.Collection("site_setting").Find(new BsonDocument("n", "site")).FirstOrDefaultAsync(ct)
        ?? new BsonDocument { ["n"] = "site", ["ew"] = true, ["oacg"] = true,
            ["we"] = new BsonArray(), ["ajt"] = new BsonArray() };

    public static Dictionary<string, object?> View(BsonDocument doc, bool homepageOnly = false)
    {
        var view = TextFields.ToDictionary(f => f.Api, f => (object?)doc.Text(f.Bson));
        if (homepageOnly) return view.ToDictionary(f => f.Key switch
        { "homepage_html" => "html", "homepage_css" => "css", _ => f.Key }, f => f.Value);
        view["enable_whitelist"] = doc.GetValue("ew", true).AsBoolean;
        view["only_allow_admin_create_team"] = doc.GetValue("oacg", true).AsBoolean;
        view["whitelist_emails"] = doc.GetValue("we", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray();
        view["auto_join_team_ids"] = doc.GetValue("ajt", new BsonArray()).AsBsonArray.Select(v => v.ToString()).ToArray();
        return view;
    }

    public async Task<BsonDocument> UpdateAsync(JsonElement data, CancellationToken ct)
    {
        // Validate everything before a single atomic $set; preserve extension-owned fields.
        var changes = new BsonDocument { ["ew"] = data.RequiredBoolean("enable_whitelist"),
            ["oacg"] = data.RequiredBoolean("only_allow_admin_create_team") };
        changes["we"] = Array(data, "whitelist_emails", value =>
        {
            if (!MailAddress.TryCreate(value, out var email) || email.Address != value || !email.Host.Contains('.'))
                throw ApiError.Field("whitelist_emails", "邮箱格式不正确");
            return new BsonString(value);
        });
        changes["ajt"] = Array(data, "auto_join_team_ids", value => ObjectId.TryParse(value, out var id)
            ? new BsonObjectId(id) : throw ApiError.Field("auto_join_team_ids", "ObjectId格式不正确"));
        foreach (var (api, bson) in TextFields)
            changes[bson] = data.TryGetProperty(api, out _) ? data.RequiredText(api, int.MaxValue) : "";
        return await store.Collection("site_setting").FindOneAndUpdateAsync(new BsonDocument("n", "site"),
            new BsonDocument("$set", changes), new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
    }

    private static BsonArray Array(JsonElement data, string field, Func<string, BsonValue> parse)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array)
            throw ApiError.Field(field, "必须提供列表");
        return new BsonArray(value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? parse(item.GetString()!) : throw ApiError.Field(field, "列表项必须为字符串")));
    }
}

public static class SiteEndpoints
{
    public static void MapSite(this WebApplication app)
    {
        app.MapGet("/v1/homepage", async (SiteSettings settings, CancellationToken ct) =>
            SiteSettings.View(await settings.GetAsync(ct), homepageOnly: true));
        app.MapGet("/v1/admin/site-setting", async (HttpContext ctx, CurrentUser current, SiteSettings settings, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            return SiteSettings.View(await settings.GetAsync(ct));
        });
        app.MapPut("/v1/admin/site-setting", async (JsonElement data, HttpContext ctx, CurrentUser current, SiteSettings settings, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            return SiteSettings.View(await settings.UpdateAsync(data, ct));
        });
    }
}
