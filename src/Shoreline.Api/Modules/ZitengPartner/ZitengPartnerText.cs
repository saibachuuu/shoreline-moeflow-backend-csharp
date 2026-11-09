using System.Globalization;
using System.Text;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Small helpers that reproduce Python's Unicode semantics. In particular
/// <see cref="Length"/> counts Unicode scalar values (Python code points), not
/// UTF-16 code units, and <see cref="Lower"/> implements the context-sensitive
/// parts of Python's <c>str.lower()</c> that .NET's invariant simple lowercase
/// does not apply.
/// </summary>
public static class ZitengPartnerText
{
    // Python's str.strip()/str.isspace() and re \s include the C0 information
    // separators and the same Unicode separators .NET recognizes.
    private const string WhitespaceClass =
        "[\\x09-\\x0d\\x1c-\\x1f\\x20\\x85\\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]";

    public const string WhitespacePattern = WhitespaceClass;

    public static bool IsPythonWhitespace(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x09 and <= 0x0D or >= 0x1C and <= 0x1F or 0x20 or 0x85 or 0xA0 or 0x1680
            or >= 0x2000 and <= 0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;
    }

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var start = 0;
        var end = text.Length;
        while (start < end && IsPythonWhitespace(Rune.GetRuneAt(text, start)))
            start += Rune.GetRuneAt(text, start).Utf16SequenceLength;
        while (end > start)
        {
            var rune = Rune.GetRuneAt(text, LastRuneStart(text, end));
            if (!IsPythonWhitespace(rune)) break;
            end -= rune.Utf16SequenceLength;
        }
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    public static int Length(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    public static bool IsSearchable(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        if (Length(title) > 240) return false;
        foreach (var rune in title.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Python <c>str.lower()</c> uses full lowercase mappings and the Final_Sigma
    /// context rule. .NET's invariant culture lowercase uses simple mappings, so
    /// U+0130 and a word-final Greek capital sigma are handled explicitly.
    /// </summary>
    public static string Lower(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var runes = text.EnumerateRunes().ToArray();
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            if (rune.Value == 0x0130)
            {
                builder.Append("i\u0307");
                continue;
            }
            if (rune.Value == 0x03A3 && IsFinalSigma(runes, index))
            {
                builder.Append('\u03C2');
                continue;
            }
            builder.Append(Rune.ToLowerInvariant(rune));
        }
        return builder.ToString();
    }

    private static int LastRuneStart(string text, int endExclusive)
    {
        var index = endExclusive - 1;
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])) index--;
        return index;
    }

    private static bool IsFinalSigma(Rune[] runes, int index)
    {
        var before = false;
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            if (IsCaseIgnorable(runes[cursor])) continue;
            before = IsCased(runes[cursor]);
            break;
        }
        if (!before) return false;
        for (var cursor = index + 1; cursor < runes.Length; cursor++)
        {
            if (IsCaseIgnorable(runes[cursor])) continue;
            return !IsCased(runes[cursor]);
        }
        return true;
    }

    private static bool IsCased(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool IsCaseIgnorable(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol)
            return true;
        return rune.Value is 0x0027 or 0x00AD or 0x2019 or 0x2024 or 0xFE52 or 0xFF07 or 0xFF0E;
    }
}
