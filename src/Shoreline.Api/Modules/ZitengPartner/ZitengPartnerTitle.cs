using System.Text.RegularExpressions;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Byte-for-byte port of <c>app/modules/ziteng_partner/title.py</c>. Only the
/// title is normalized here; the series parentheses are intentionally kept.
/// </summary>
public static partial class ZitengPartnerTitle
{
    private const string Ws = ZitengPartnerText.WhitespacePattern;

    [GeneratedRegex("^" + Ws + "*[\\[【（(#]" + Ws + "*(?:no\\.?" + Ws + "*)?\\d{1,5}" + Ws +
                    "*[\\]】）)]" + Ws + "*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIndex();

    [GeneratedRegex("[\\[【][^\\[\\]【】]{1,60}[\\]】]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketTag();

    [GeneratedRegex("[（(]" + Ws + "*(?:DL版|中国翻訳|中国翻译|漢化|汉化|無修正|无修正|カラー|彩页)" + Ws +
                    "*[)）]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionTag();

    [GeneratedRegex(Ws + "+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public static string Extract(string? projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return "";
        var text = ZitengPartnerText.Strip(projectName);
        text = LeadingIndex().Replace(text, "");
        text = VersionTag().Replace(text, " ");
        text = BracketTag().Replace(text, " ");
        text = Whitespace().Replace(text, " ");
        return ZitengPartnerText.Strip(text);
    }
}
