using System.Text.Json;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

public static class IdentityAliases
{
    public static string[] Normalize(JsonElement data, string name, int maxCount)
    {
        if (data.ValueKind != JsonValueKind.Array) throw Error("aliases must be an array");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var value in data.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw Error("alias must be a string");
            var alias = value.GetString()!.Normalize().Trim();
            if (alias.Length == 0) continue;
            if (alias.EnumerateRunes().Count() > 64) throw Error("alias is too long");
            var key = SearchText.Normalize(alias);
            if (key == SearchText.Normalize(name)) throw Error("alias cannot equal the user's name");
            if (seen.Add(key)) result.Add(alias);
        }
        if (result.Count > maxCount) throw Error("too many aliases");
        return result.ToArray();
    }

    public static ApiError Error(string detail) => new("AliasValidationError", 5114, "alias 格式或数量无效: " + detail, 422)
    { IdentityCode = "ALIAS_INVALID" };
}
