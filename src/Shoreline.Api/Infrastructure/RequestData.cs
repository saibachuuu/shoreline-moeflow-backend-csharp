using System.Text.Json;

namespace Shoreline.Api.Infrastructure;

public static class RequestData
{
    public static long RequiredInteger(this JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value))
            throw ApiError.Field(field, "缺少必填字段");
        if (value.ValueKind == JsonValueKind.True) return 1;
        if (value.ValueKind == JsonValueKind.False) return 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var number)) return number;
            if (value.TryGetDecimal(out var decimalNumber) && decimalNumber >= long.MinValue && decimalNumber <= long.MaxValue)
                return (long)decimal.Truncate(decimalNumber);
        }
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var textNumber)) return textNumber;
        throw ApiError.Field(field, "整数格式不正确");
    }

    public static bool RequiredBoolean(this JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value))
            throw ApiError.Field(field, "缺少必填字段");
        // Match Marshmallow's accepted Boolean values.
        if (value.ValueKind == JsonValueKind.True || value.ToString() is "1" or "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" or "t" or "T" or "y" or "Y") return true;
        if (value.ValueKind == JsonValueKind.False || value.ToString() is "0" or "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" or "f" or "F" or "n" or "N") return false;
        throw ApiError.Field(field, "布尔值格式不正确");
    }

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
