using System.Collections.Frozen;
using System.Text.Json;

namespace Shoreline.Api.Features;

/// <summary>
/// Legacy gettext catalogue (app/translations/en/LC_MESSAGES/messages.po).
/// Python wraps user-visible messages in gettext, so with no Accept-Language
/// (or an English one) Flask-Babel returns the English msgstr while a Chinese
/// request keeps the source string. The catalogue is embedded so error and
/// success messages follow the same rule without a Python dependency.
/// </summary>
public static class LegacyMessages
{
    private static readonly FrozenDictionary<string, string> English = Load();

    private static FrozenDictionary<string, string> Load()
    {
        using var stream = typeof(LegacyMessages).Assembly.GetManifestResourceStream("Shoreline.LegacyMessages")
            ?? throw new InvalidOperationException("Missing legacy message catalogue");
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Invalid legacy message catalogue");
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static string Translate(string message, bool chinese) =>
        chinese ? message : English.GetValueOrDefault(message, message);

    public static string Translate(string message, HttpContext context) =>
        Translate(message, Chinese(context));

    public static bool Chinese(HttpContext context)
    {
        try { return LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage); }
        catch (FormatException) { return false; }
    }
}
