using System.Text;

namespace Shoreline.Api.Infrastructure;

// NFC + full Unicode case folding, matching Python's search projections (not locale-sensitive casing).
public static class SearchText
{
    private static readonly Dictionary<int, string> Special = new()
    {
        [181] = "μ", [223] = "ss", [304] = "i̇", [329] = "ʼn", [383] = "s", [496] = "ǰ",
        [837] = "ι", [912] = "ΐ", [944] = "ΰ", [962] = "σ", [976] = "β", [977] = "θ",
        [981] = "φ", [982] = "π", [1008] = "κ", [1009] = "ρ", [1013] = "ε", [1415] = "եւ",
        [7296] = "в", [7297] = "д", [7298] = "о", [7299] = "с", [7300] = "т", [7301] = "т",
        [7302] = "ъ", [7303] = "ѣ", [7304] = "ꙋ", [7830] = "ẖ", [7831] = "ẗ", [7832] = "ẘ",
        [7833] = "ẙ", [7834] = "aʾ", [7835] = "ṡ", [7838] = "ss", [8016] = "ὐ", [8018] = "ὒ",
        [8020] = "ὔ", [8022] = "ὖ", [8114] = "ὰι", [8115] = "αι", [8116] = "άι", [8118] = "ᾶ",
        [8119] = "ᾶι", [8124] = "αι", [8126] = "ι", [8130] = "ὴι", [8131] = "ηι", [8132] = "ήι",
        [8134] = "ῆ", [8135] = "ῆι", [8140] = "ηι", [8146] = "ῒ", [8147] = "ΐ", [8150] = "ῖ",
        [8151] = "ῗ", [8162] = "ῢ", [8163] = "ΰ", [8164] = "ῤ", [8166] = "ῦ", [8167] = "ῧ",
        [8178] = "ὼι", [8179] = "ωι", [8180] = "ώι", [8182] = "ῶ", [8183] = "ῶι", [8188] = "ωι",
        [64256] = "ff", [64257] = "fi", [64258] = "fl", [64259] = "ffi", [64260] = "ffl", [64261] = "st",
        [64262] = "st", [64275] = "մն", [64276] = "մե", [64277] = "մի", [64278] = "վն", [64279] = "մխ"
    };

    public static string Normalize(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormC).Trim().EnumerateRunes())
        {
            var cp = rune.Value;
            if (Special.TryGetValue(cp, out var replacement)) output.Append(replacement);
            else if (cp is >= 5024 and <= 5109) output.Append(rune.ToString());
            else if (cp is >= 5112 and <= 5117) output.Append(new Rune(cp - 8).ToString());
            else if (cp is >= 43888 and <= 43967) output.Append(new Rune(cp - 38864).ToString());
            else if (cp is >= 8064 and <= 8111)
            {
                var root = cp < 8080 ? 7936 : cp < 8096 ? 7968 : 8032;
                output.Append(new Rune(root + (cp & 7)).ToString()).Append('ι');
            }
            else output.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return output.ToString();
    }
}
