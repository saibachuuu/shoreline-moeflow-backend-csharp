using System.Text.Json;

namespace Shoreline.Api.Infrastructure;

public static class RequestData
{
    public static string RequiredText(this JsonElement data, string field, int maxLength = 4096)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw ApiError.Field(field, "缺少必填字段");
        var text = value.GetString()!;
        if (text.EnumerateRunes().Count() > maxLength) throw ApiError.Field(field, "内容过长");
        return text;
    }

    public static string Password(this JsonElement data, string field)
    {
        var text = data.RequiredText(field, 60);
        if (text.EnumerateRunes().Count() < 6) throw ApiError.Field(field, "密码长度为6到60位");
        return text;
    }
}
