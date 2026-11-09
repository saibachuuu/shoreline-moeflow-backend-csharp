using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace Shoreline.Api.Features;

internal static class NotificationManagementJson
{
    // Mirrors Python json.dumps(value, sort_keys=True, ensure_ascii=False):
    // ", " / ": " separators, literal Unicode, lowercase \u00xx control escapes.
    public static string Canonical(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    public static string Revision(object? value)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonical(value));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                WriteString(builder, text);
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case BsonDocument document:
                WritePairs(builder, document.Select(pair =>
                    KeyValuePair.Create(pair.Name, (object?)pair.Value)));
                return;
            case BsonArray array:
                WriteArray(builder, array.Select(item => (object?)item));
                return;
            case BsonValue bson:
                WriteBson(builder, bson);
                return;
            case IDictionary<string, object?> dictionary:
                WritePairs(builder, dictionary);
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                WritePairs(builder, dictionary);
                return;
            case IDictionary dictionary:
                WritePairs(builder, dictionary.Cast<DictionaryEntry>().Select(entry =>
                    KeyValuePair.Create(Convert.ToString(entry.Key,
                        CultureInfo.InvariantCulture) ?? "", entry.Value)));
                return;
            case IEnumerable enumerable:
                WriteArray(builder, enumerable.Cast<object?>());
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case float single:
                builder.Append(PythonFloat(single));
                return;
            case double number:
                builder.Append(PythonFloat(number));
                return;
            case decimal decimalNumber:
                builder.Append(PythonFloat((double)decimalNumber));
                return;
            case DateTime date:
                WriteString(builder, FormatDate(date.ToUniversalTime()));
                return;
            default:
                WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    private static void WriteBson(StringBuilder builder, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Null:
                builder.Append("null");
                break;
            case BsonType.Document:
                WritePairs(builder, value.AsBsonDocument.Select(pair =>
                    KeyValuePair.Create(pair.Name, (object?)pair.Value)));
                break;
            case BsonType.Array:
                WriteArray(builder, value.AsBsonArray.Select(item => (object?)item));
                break;
            case BsonType.String:
                WriteString(builder, value.AsString);
                break;
            case BsonType.Boolean:
                builder.Append(value.AsBoolean ? "true" : "false");
                break;
            case BsonType.Int32:
                builder.Append(value.AsInt32.ToString(CultureInfo.InvariantCulture));
                break;
            case BsonType.Int64:
                builder.Append(value.AsInt64.ToString(CultureInfo.InvariantCulture));
                break;
            case BsonType.Double:
                builder.Append(PythonFloat(value.AsDouble));
                break;
            case BsonType.Decimal128:
                builder.Append(PythonFloat((double)Decimal128.ToDecimal(value.AsDecimal128)));
                break;
            case BsonType.ObjectId:
                WriteString(builder, value.AsObjectId.ToString());
                break;
            case BsonType.DateTime:
                WriteString(builder, FormatDate(value.AsBsonDateTime.ToUniversalTime()));
                break;
            default:
                throw new InvalidOperationException("Unsupported canonical JSON BSON type");
        }
    }

    private static string FormatDate(DateTime date)
    {
        var fraction = date.Ticks % TimeSpan.TicksPerSecond;
        var text = date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        if (fraction != 0)
            text += "." + (fraction / 10).ToString("D6", CultureInfo.InvariantCulture);
        return text + "Z";
    }

    private static string PythonFloat(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        var absolute = Math.Abs(value);
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (absolute >= 1e16 || absolute < 1e-4)
        {
            var index = text.IndexOfAny(['E', 'e']);
            string mantissa;
            int exponent;
            if (index >= 0)
            {
                mantissa = text[..index];
                exponent = int.Parse(text[(index + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture);
            }
            else
            {
                (mantissa, exponent) = ScientificFromFixed(text);
            }
            if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
            return $"{mantissa}e{(exponent >= 0 ? "+" : "-")}{Math.Abs(exponent):00}";
        }

        if (!text.Contains('.')) text += ".0";
        return text;
    }

    private static (string Mantissa, int Exponent) ScientificFromFixed(string text)
    {
        var negative = text.StartsWith('-');
        var unsigned = negative ? text[1..] : text;
        var point = unsigned.IndexOf('.');
        var integer = point >= 0 ? unsigned[..point] : unsigned;
        var fraction = point >= 0 ? unsigned[(point + 1)..] : "";
        var digits = integer + fraction;
        var first = 0;
        while (first < digits.Length && digits[first] == '0') first++;
        if (first == digits.Length) return ("0", 0);

        var exponent = integer.Length - first - 1;
        var rest = digits[(first + 1)..].TrimEnd('0');
        var mantissa = digits[first].ToString();
        if (rest.Length > 0) mantissa += "." + rest;
        return (negative ? "-" + mantissa : mantissa, exponent);
    }

    private static void WritePairs(
        StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        builder.Append('{');
        var first = true;
        foreach (var (key, item) in pairs.OrderBy(pair => pair.Key, PythonStringComparer.Instance))
        {
            if (!first) builder.Append(", ");
            first = false;
            WriteString(builder, key);
            builder.Append(": ");
            Write(builder, item);
        }
        builder.Append('}');
    }

    private sealed class PythonStringComparer : IComparer<string>
    {
        public static readonly PythonStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var xi = 0;
            var yi = 0;
            while (xi < x.Length && yi < y.Length)
            {
                var xr = Rune.GetRuneAt(x, xi);
                var yr = Rune.GetRuneAt(y, yi);
                if (xr.Value != yr.Value) return xr.Value.CompareTo(yr.Value);
                xi += xr.Utf16SequenceLength;
                yi += yr.Utf16SequenceLength;
            }
            return (x.Length - xi).CompareTo(y.Length - yi);
        }
    }

    private static void WriteArray(StringBuilder builder, IEnumerable<object?> items)
    {
        builder.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first) builder.Append(", ");
            first = false;
            Write(builder, item);
        }
        builder.Append(']');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (rune.Value < 0x20)
                        builder.Append("\\u").Append(rune.Value.ToString("x4",
                            CultureInfo.InvariantCulture));
                    else
                        builder.Append(rune.ToString());
                    break;
            }
        }
        builder.Append('"');
    }
}
