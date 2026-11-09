using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed class UserProfileService(MongoStore store, IConfiguration configuration, TimeProvider clock, IdentityNameGate names)
{
    public async Task<(BsonDocument User, ObjectId? AuditId)> UpdateAsync(BsonDocument user, BsonDocument actor,
        JsonElement data, bool aliasesOnly, string requestId, CancellationToken ct)
    {
        var requestedName = aliasesOnly ? user.Text("n") : data.RequiredText("name", 1024);
        using var named = await names.EnterAsync("user", requestedName, ct);
        var next = user.DeepClone().AsBsonDocument;
        var aliasesProvided = data.TryGetProperty("aliases", out var aliasData);
        if (!aliasesOnly)
        {
            var name = requestedName;
            if (name != user.Text("n"))
            {
                if (name.Length is < 2 or > 18 || !Regex.IsMatch(name, @"\A[\u2E80-\u2FDF\u3040-\u318F\u31A0-\u31BF\u31F0-\u31FF\u3400-\u4DB5\u4E00-\u9FFF\uA960-\uA97F\uAC00-\uD7FFa-zA-Z0-9_]+\z"))
                    throw ApiError.Field("name", "用户名长度或格式不正确");
                if (await store.Collection("user").Find(new BsonDocument("n", new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i")))
                        .AnyAsync(ct)) throw ApiError.Field("name", "此昵称已被使用");
            }
            next["n"] = name;
            next["s"] = data.RequiredText("signature", 140);
            var locale = data.RequiredText("locale", 16);
            if (locale is not ("auto" or "zh_CN" or "zh_TW" or "en")) throw ApiError.Field("locale", "语言无效");
            next["l"] = locale;
        }
        if (aliasesOnly || aliasesProvided) next["as"] = new BsonArray(NormalizeAliases(aliasData, next.Text("n")));
        else next["as"] = new BsonArray(NormalizeAliases(JsonSerializer.SerializeToElement(
            next.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString)), next.Text("n")));
        if (data.TryGetProperty("default_display_name", out var display))
        {
            if (display.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw AliasError("default display name must be a string");
            next["ddn"] = display.ValueKind == JsonValueKind.String ? display.GetString()!.Trim() : "";
        }
        var defaultName = next.Text("ddn").Trim();
        if (defaultName.EnumerateRunes().Count() > 140) throw AliasError("default display name is too long");
        if (defaultName != next.Text("n") && !next["as"].AsBsonArray.Any(v => v.AsString == defaultName)) defaultName = "";
        next["ddn"] = defaultName;
        next["ns"] = SearchText.Normalize(next.Text("n"));
        next["asrch"] = new BsonArray(next["as"].AsBsonArray.Select(v => SearchText.Normalize(v.AsString)));
        var updates = new[] { "n", "s", "l", "as", "ddn", "ns", "asrch" }
            .Where(next.Contains).Select(key => Builders<BsonDocument>.Update.Set(key, next[key])).ToList();
        updates.Add(Builders<BsonDocument>.Update.Inc("csharp_profile_revision", 1));
        var revision = user.GetValue("csharp_profile_revision", 0);
        var filter = new BsonDocument("_id", user["_id"]);
        if (revision.ToInt64() == 0)
            filter["$or"] = new BsonArray { new BsonDocument("csharp_profile_revision", revision),
                new BsonDocument("csharp_profile_revision", new BsonDocument("$exists", false)) };
        else filter["csharp_profile_revision"] = revision;
        var updated = await store.Collection("user").FindOneAndUpdateAsync<BsonDocument>(filter,
            Builders<BsonDocument>.Update.Combine(updates), new() { ReturnDocument = ReturnDocument.After }, ct);
        if (updated is null) throw new ApiError("IdentityVersionConflictError", 5115, "版本已变化，请刷新后重试", 409) { IdentityCode = "VERSION_CONFLICT" };
        ObjectId? audit = null;
        if (aliasesOnly || aliasesProvided)
        {
            audit = ObjectId.GenerateNewId();
            await store.Collection("identity_audit_event").InsertOneAsync(new BsonDocument
            {
                ["_id"] = audit.Value,
                ["actor"] = actor["_id"],
                ["scope"] = "user",
                ["action"] = "user_aliases_replace",
                ["target_user_id"] = user["_id"].ToString(),
                ["request_id"] = requestId,
                ["source"] = "runtime",
                ["create_time"] = clock.GetUtcNow().UtcDateTime,
                ["before"] = new BsonDocument { ["scope"] = "site", ["aliases"] = user.GetValue("as", new BsonArray()), ["default_display_name"] = user.Text("ddn") },
                ["after"] = new BsonDocument { ["scope"] = "site", ["aliases"] = updated["as"], ["default_display_name"] = updated.Text("ddn") }
            }, cancellationToken: ct);
        }
        return (updated, audit);
    }

    private string[] NormalizeAliases(JsonElement data, string name)
        => IdentityAliases.Normalize(data, name, configuration.GetValue("Users:MaxAliases", 10));

    private static ApiError AliasError(string detail) => IdentityAliases.Error(detail);
}
