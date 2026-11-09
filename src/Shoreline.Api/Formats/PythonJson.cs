using System.Globalization;
using System.Text;
using MongoDB.Bson;

namespace Shoreline.Api.Formats;

/// <summary>
/// Small helpers that reproduce the Python scalar representations used by the
/// legacy exporter. LabelPlus embeds <c>str(float)</c> and project.json is
/// written with <c>json.dump(..., ensure_ascii=True)</c>, so .NET's default
/// formatting is not byte-compatible.
/// </summary>
public static class PythonValue
{
    public static string Float(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOfAny(['E', 'e']);
        if (exponentIndex >= 0)
        {
            var mantissa = text[..exponentIndex];
            var exponent = int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
            // Python repr(float) switches to scientific notation only for
            // abs(value) < 1e-4 or >= 1e16; .NET's "R" switches earlier.
            if (exponent >= 16 || exponent <= -5)
            {
                var sign = exponent >= 0 ? "+" : "-";
                var digits = Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
                return mantissa + "e" + sign + digits;
            }

            var negative = mantissa.StartsWith('-');
            if (negative) mantissa = mantissa[1..];
            var dot = mantissa.IndexOf('.');
            var significant = mantissa.Replace(".", "");
            var point = (dot >= 0 ? dot : mantissa.Length) + exponent;
            string expanded;
            if (point <= 0)
                expanded = "0." + new string('0', -point) + significant;
            else if (point >= significant.Length)
                expanded = significant + new string('0', point - significant.Length) + ".0";
            else
                expanded = significant[..point] + "." + significant[point..];
            return (negative ? "-" : "") + expanded;
        }
        if (!text.Contains('.') && Math.Abs(value) >= 1e16)
        {
            // .NET's shortest round-trip formatter keeps 1e16 as a fixed
            // integer, while Python repr(float) uses "1e+16".
            var negative = text.StartsWith('-');
            var digits = (negative ? text[1..] : text).TrimStart('0').TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            var exponent = (negative ? text[1..] : text).Length - 1;
            var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
            return (negative ? "-" : "") + mantissa + "e+" +
                exponent.ToString("D2", CultureInfo.InvariantCulture);
        }
        if (!text.Contains('.')) text += ".0";
        return text;
    }

    /// <summary>Python <c>datetime.isoformat()</c> for the naive UTC datetimes stored in Mongo.</summary>
    public static string Iso(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture)[..6];
        return text;
    }

    public static string Iso(BsonDateTime value) => Iso(value.ToUniversalTime());
}

/// <summary>
/// Serializes the flat object graph used by project.json exactly like
/// <c>json.dumps(value)</c> with the default separators and
/// <c>ensure_ascii=True</c>.
/// </summary>
public static class PythonJson
{
    public static string Serialize(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var output = new StringBuilder();
        WriteObject(output, fields);
        return output.ToString();
    }

    public static string Serialize(object? value)
    {
        var output = new StringBuilder();
        WriteValue(output, value);
        return output.ToString();
    }

    private static void WriteObject(StringBuilder output, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        output.Append('{');
        var first = true;
        foreach (var field in fields)
        {
            if (!first) output.Append(", ");
            first = false;
            WriteString(output, field.Key);
            output.Append(": ");
            WriteValue(output, field.Value);
        }
        output.Append('}');
    }

    private static void WriteValue(StringBuilder output, object? value)
    {
        switch (value)
        {
            case null:
                output.Append("null");
                return;
            case string text:
                WriteString(output, text);
                return;
            case bool flag:
                output.Append(flag ? "true" : "false");
                return;
            case double number:
                if (double.IsNaN(number)) output.Append("NaN");
                else if (double.IsPositiveInfinity(number)) output.Append("Infinity");
                else if (double.IsNegativeInfinity(number)) output.Append("-Infinity");
                else output.Append(PythonValue.Float(number));
                return;
            case float number:
                WriteValue(output, (double)number);
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case IEnumerable<KeyValuePair<string, object?>> fields:
                WriteObject(output, fields);
                return;
            case System.Collections.IEnumerable items:
                output.Append('[');
                var first = true;
                foreach (var item in items)
                {
                    if (!first) output.Append(", ");
                    first = false;
                    WriteValue(output, item);
                }
                output.Append(']');
                return;
            default:
                WriteString(output, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    private static void WriteString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            switch (scalar)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (scalar >= 0x20 && scalar <= 0x7E)
                    {
                        output.Append((char)scalar);
                    }
                    else if (scalar <= 0xFFFF)
                    {
                        output.Append("\\u").Append(scalar.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        var adjusted = scalar - 0x10000;
                        var high = 0xD800 + (adjusted >> 10);
                        var low = 0xDC00 + (adjusted & 0x3FF);
                        output.Append("\\u").Append(high.ToString("x4", CultureInfo.InvariantCulture));
                        output.Append("\\u").Append(low.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    break;
            }
        }
        output.Append('"');
    }
}
