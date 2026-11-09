using System.Text;
using System.Text.RegularExpressions;

namespace Shoreline.Api.Modules.ZitengPartner;

public static class ZitengPartnerMatchLevel
{
    public const string Exact = "exact";
    public const string Contains = "contains";
    public const string None = "none";
}

public sealed record ZitengPartnerSuspect(ZitengPartnerWork Work, string Level, double Score)
{
    public int StateRank => StateRankOf(Work.State);

    public static int StateRankOf(string? state) => state switch
    {
        "in_progress" => 0,
        "published" => 1,
        "withdrawn" => 2,
        _ => 99,
    };
}

/// <summary>
/// Port of <c>app/modules/ziteng_partner/matching.py</c>. NFKC + Python
/// lowercase are used deliberately; case folding would change the reference
/// behaviour.
/// </summary>
public static partial class ZitengPartnerMatching
{
    private const string Ws = ZitengPartnerText.WhitespacePattern;

    [GeneratedRegex(Ws + "+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[~〜～\-—–_・:：!！?？。.,，、'""“”‘’()（）\[\]【】]", RegexOptions.CultureInvariant)]
    private static partial Regex Decorations();

    [GeneratedRegex(@"[（(][^（()）]*[)）]", RegexOptions.CultureInvariant)]
    private static partial Regex SeriesParentheses();

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var normalized = text.Normalize(NormalizationForm.FormKC);
        normalized = ZitengPartnerText.Lower(normalized);
        normalized = Whitespace().Replace(normalized, "");
        return Decorations().Replace(normalized, "");
    }

    public static string Core(string? title) =>
        Normalize(SeriesParentheses().Replace(title ?? "", ""));

    public static (string Level, double Score) Score(string? projectTitle, ZitengPartnerWork candidate)
    {
        var left = Core(ZitengPartnerTitle.Extract(projectTitle));
        var right = Core(candidate.OriginalTitle);
        if (left.Length == 0 || right.Length == 0) return (ZitengPartnerMatchLevel.None, 0);
        if (string.Equals(left, right, StringComparison.Ordinal)) return (ZitengPartnerMatchLevel.Exact, 1);

        var shorter = left;
        var longer = right;
        if (ZitengPartnerText.Length(left) > ZitengPartnerText.Length(right))
        {
            shorter = right;
            longer = left;
        }
        if (ZitengPartnerText.Length(shorter) >= 4 &&
            longer.Contains(shorter, StringComparison.Ordinal))
        {
            return (ZitengPartnerMatchLevel.Contains,
                (double)ZitengPartnerText.Length(shorter) / ZitengPartnerText.Length(longer));
        }
        return (ZitengPartnerMatchLevel.None, 0);
    }

    public static List<ZitengPartnerSuspect> FindSuspects(string? projectTitle,
        IEnumerable<ZitengPartnerWork> candidates, int limit = 5)
    {
        var suspects = new List<ZitengPartnerSuspect>();
        foreach (var work in candidates)
        {
            var (level, score) = Score(projectTitle, work);
            if (level == ZitengPartnerMatchLevel.None) continue;
            suspects.Add(new ZitengPartnerSuspect(work, level, score));
        }

        // LINQ OrderBy/ThenBy are stable, matching Python's stable list.sort with
        // the same (state rank, -score) key.
        var ordered = suspects
            .OrderBy(suspect => suspect.StateRank)
            .ThenByDescending(suspect => suspect.Score)
            .ToList();
        if (limit >= 0) return ordered.Take(limit).ToList();
        return ordered.Take(Math.Max(0, ordered.Count + limit)).ToList();
    }

    /// <summary>Python <c>round(score, 3)</c>, including banker's rounding.</summary>
    public static double RoundScore(double score) =>
        Math.Round(score, 3, MidpointRounding.ToEven);
}
