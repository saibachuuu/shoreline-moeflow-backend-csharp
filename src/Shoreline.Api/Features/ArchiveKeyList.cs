using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class ArchiveKeyList
{
    public static BsonArray Apply(BsonArray current, JsonElement submitted, ArchiveSecrets secrets)
    {
        if (submitted.ValueKind != JsonValueKind.Array) throw Error("archive_api_keys 必须是对象数组");
        var existing = current.Where(value => value.IsBsonDocument && value.AsBsonDocument.Contains("id"))
            .Select(value => value.AsBsonDocument).Where(value => !value["id"].IsBsonNull && value["id"].ToString()!.Length != 0)
            .GroupBy(value => value["id"].ToString()!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var result = new BsonArray();
        foreach (var raw in submitted.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object) throw Error("archive_api_keys 必须是对象数组");
            var id = raw.TryGetProperty("id", out var idValue) && IdentityErrors.Truthy(idValue) ? idValue.ToString().Trim() : "";
            var hasKey = raw.TryGetProperty("key", out var keyValue) && keyValue.ValueKind != JsonValueKind.Null;
            BsonDocument item;
            if (id.Length != 0)
            {
                if (!existing.TryGetValue(id, out var previous)) throw Error("archive_api_keys 包含不存在的 id");
                item = previous.DeepClone().AsBsonDocument;
            }
            else
            {
                if (!hasKey) throw Error("archive_api_keys 每项需要 id 或 key");
                item = new BsonDocument { ["id"] = Guid.NewGuid().ToString(), ["remark"] = "", ["enabled"] = true };
            }
            if (raw.TryGetProperty("remark", out var remark)) item["remark"] = Cut(IdentityErrors.Truthy(remark) ? remark.ToString() : "", 200);
            else if (!item.Contains("remark") || item["remark"].IsBsonNull) item["remark"] = "";
            if (raw.TryGetProperty("enabled", out var enabled)) item["enabled"] = enabled.ValueKind != JsonValueKind.False;
            else if (!item.Contains("enabled")) item["enabled"] = true;
            try
            {
                if (hasKey)
                {
                    var text = keyValue.ToString().Trim();
                    if (text.Length == 0) throw Error("archive API key 不能为空");
                    item["key"] = secrets.Encrypt(Cut(text, 512));
                }
                else if (item.Text("key").Length != 0) item["key"] = secrets.Encrypt(secrets.Decrypt(item.Text("key")));
            }
            catch (CryptographicException error) { throw Error(error.Message); }
            result.Add(item);
        }
        return result;
    }

    public static object[] View(BsonArray keys, ArchiveSecrets secrets) => keys.Select((value, index) => (value, index))
        .Where(pair => pair.value.IsBsonDocument).Select(pair =>
        {
            var item = pair.value.AsBsonDocument;
            return (object)new
            {
                id = item.Contains("id") && !item["id"].IsBsonNull ? item["id"].ToString() : pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                remark = item.Text("remark"),
                enabled = !item.Contains("enabled") || !item["enabled"].IsBoolean || item["enabled"].AsBoolean,
                key_tail = secrets.Tail(item.Text("key"))
            };
        }).ToArray();

    private static string Cut(string value, int length) => string.Concat(value.EnumerateRunes().Take(length).Select(rune => rune.ToString()));
    private static ApiError Error(string message) => new("ValidateError", 2, message);
}
