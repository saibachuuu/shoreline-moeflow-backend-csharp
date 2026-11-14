using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// A team-level list of words that must not appear in a project name. The team
/// creator owns the list; every project create checks it before writing.
///
/// Matching is a case-insensitive substring test: "school" blocks
/// "Old School Project" and "SCHOOL". Empty entries are dropped, duplicates are
/// collapsed case-insensitively, and both the list size and word length are
/// bounded so the check stays a cheap scan over a small array.
/// </summary>
public static class NameBlocklist
{
    public const string Field = "nw";
    public const int MaxWords = 100;
    public const int MaxWordLength = 50;

    /// <summary>Parses the JSON array sent by the team settings form.</summary>
    public static BsonArray Parse(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return new BsonArray();
        if (value.ValueKind != JsonValueKind.Array)
            throw ApiError.Field("project_name_blocklist", "必须是字符串数组");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new BsonArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw ApiError.Field("project_name_blocklist", "必须是字符串数组");
            var word = (item.GetString() ?? string.Empty).Trim();
            if (word.Length == 0) continue;
            if (word.Length > MaxWordLength)
                throw ApiError.Field("project_name_blocklist", $"单个词语最多 {MaxWordLength} 个字符");
            if (!seen.Add(word)) continue;
            result.Add(word);
        }
        if (result.Count > MaxWords)
            throw ApiError.Field("project_name_blocklist", $"最多 {MaxWords} 个词语");
        return result;
    }

    /// <summary>Returns the first blocked word contained in <paramref name="name"/>, or null.</summary>
    public static string? FirstMatch(BsonDocument team, string name)
    {
        if (!team.TryGetValue(Field, out var value) || value is not BsonArray words) return null;
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var entry in words)
        {
            if (!entry.IsString) continue;
            var word = entry.AsString;
            if (word.Length == 0) continue;
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return word;
        }
        return null;
    }

    /// <summary>Legacy error used by the project create path.</summary>
    public static ApiError Blocked(string word) =>
        new("ValidateError", 2, $"项目名称包含禁止使用的内容：{word}");
}
