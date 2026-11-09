using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Formats;

/// <summary>
/// LabelPlus text generation compatible with the legacy Python exporter
/// (app/models/project.py and app/models/file.py).
/// </summary>
public static partial class LabelPlusWriter
{
    /// <summary>Chinese catalog values; the legacy worker exports in the default zh locale.</summary>
    public static readonly string Header =
        "1,0\r\n" +
        "-\r\n" +
        "框内\r\n" +
        "框外\r\n" +
        "-\r\n" +
        "可使用 LabelPlus Photoshop 脚本导入 psd 中\r\n";

    public const int DefaultStaffPage = 1;

    public static readonly string[] WorkerTagOrder =
    [
        "raw_provider", "scanner", "cropper", "cleaner",
        "translator", "proofreader", "typesetter"
    ];

    private static readonly Dictionary<string, string> WorkerTagLabels = new(StringComparer.Ordinal)
    {
        ["raw_provider"] = "图源",
        ["scanner"] = "扫图",
        ["cropper"] = "裁切",
        ["cleaner"] = "修图",
        ["translator"] = "翻译",
        ["proofreader"] = "校对",
        ["typesetter"] = "嵌字"
    };

    private static readonly Regex UnsafeFilename = new("[/\\\\?%*:|\"<>\x7F\x00-\x1F]", RegexOptions.Compiled);

    /// <summary>
    /// Python <c>Project._staff_list_page_resolved</c>: project setting, then
    /// team setting, then the built-in first page.
    /// </summary>
    public static int ResolveStaffPage(int? projectPage, int? teamPage) =>
        projectPage ?? teamPage ?? DefaultStaffPage;

    /// <summary>Python <c>Project._staff_list_target_index</c> with all clamps.</summary>
    public static int StaffTargetIndex(int fileCount, int page)
    {
        if (fileCount <= 0) return -1;
        if (page > 0)
        {
            var index = page - 1;
            return index >= fileCount ? fileCount - 1 : index;
        }
        var negative = fileCount + page;
        return negative < 0 ? 0 : negative;
    }

    /// <summary>
    /// Python <c>Project._staff_list_block</c>. Rows are expected to be the
    /// active project-member documents of one project, in display order.
    /// </summary>
    public static string? StaffBlock(IEnumerable<BsonDocument> members)
    {
        var lines = new List<string>();
        foreach (var tag in WorkerTagOrder)
        {
            var names = new List<string>();
            foreach (var member in members)
            {
                var statusValue = member.GetValue("status", "active");
                var status = statusValue.IsString ? statusValue.AsString : "active";
                if (!string.Equals(status, "active", StringComparison.Ordinal)) continue;
                if (!HasTag(member, tag)) continue;
                var nameValue = member.GetValue("display_name", "");
                var displayName = nameValue.IsString ? nameValue.AsString : "";
                names.Add(displayName.Replace("\r", " ").Replace("\n", " "));
            }
            if (names.Count == 0) continue;
            lines.Add($"{WorkerTagLabels[tag]}：{string.Join("、", names)}");
        }
        return lines.Count == 0
            ? null
            : "----------------[0]----------------[0.5,0.5,2]\r\n" + string.Join("\r\n", lines) + "\r\n";
    }

    private static bool HasTag(BsonDocument member, string tag)
    {
        var value = member.GetValue("tags", BsonNull.Value);
        if (value is not BsonArray array) return false;
        foreach (var item in array)
        {
            if (item.IsString && string.Equals(item.AsString, tag, StringComparison.Ordinal)) return true;
            if (!item.IsString && string.Equals(item.ToString(), tag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Python <c>export_file_names</c>: only files with a manual order use the
    /// numbered export name; otherwise LabelPlus and the zip keep the DB name.
    /// </summary>
    public static Dictionary<ObjectId, string> ExportFileNames(IReadOnlyList<BsonDocument> files)
    {
        var names = new Dictionary<ObjectId, string>();
        var anyManual = files.Any(file => !file.GetValue("mo", BsonNull.Value).IsBsonNull);
        if (!anyManual) return names;

        var width = Math.Max(4, files.Count.ToString(CultureInfo.InvariantCulture).Length);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            names[file["_id"].AsObjectId] =
                (index + 1).ToString("D" + width, CultureInfo.InvariantCulture) + "_" + file.Text("n");
        }
        return names;
    }

    /// <summary>Python <c>File.to_labelplus</c> file line.</summary>
    public static string FileHeader(string displayName) =>
        ">>>>>>>>[" + displayName + "]<<<<<<<<\r\n";

    /// <summary>Python <c>File.to_labelplus</c> source line.</summary>
    public static string SourceEntry(int index, double x, double y, int positionType) =>
        SourceEntry(index, new BsonDouble(x), new BsonDouble(y), positionType);

    /// <summary>
    /// Preserves the stored BSON numeric type: Python <c>str(0)</c> is "0",
    /// while <c>str(0.0)</c> is "0.0".
    /// </summary>
    public static string SourceEntry(int index, BsonValue x, BsonValue y, int positionType) =>
        "----------------[" + index.ToString(CultureInfo.InvariantCulture) +
        "]----------------[" + NumberText(x) + "," + NumberText(y) + "," +
        positionType.ToString(CultureInfo.InvariantCulture) + "]\r\n";

    private static string NumberText(BsonValue value) => value.IsDouble || value.IsDecimal128
        ? PythonValue.Float(value.ToDouble())
        : value.IsNumeric
            ? value.ToInt64().ToString(CultureInfo.InvariantCulture)
            : "0";

    /// <summary>
    /// Python applies <c>content.replace("\n", "\r\n")</c>, so an existing
    /// CRLF deliberately becomes CRCRLF rather than being normalized.
    /// </summary>
    public static string TranslationLine(string content) => content.Replace("\n", "\r\n") + "\r\n";

    public static string TranslationContent(BsonDocument? translation)
    {
        if (translation is null) return "";
        var proofread = translation.Text("p");
        return proofread.Length > 0 ? proofread : translation.Text("c");
    }

    /// <summary>Python SAFE_FILENAME_REGEX replacement used only for download names.</summary>
    public static string SafeDownloadName(string value) => UnsafeFilename.Replace(value, "□");

    /// <summary>
    /// Defense-in-depth for zip entry names. Normal DB filenames never contain
    /// separators, but archive paths must never escape the images/ directory.
    /// </summary>
    public static string SafeEntryPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .Select(segment => segment == ".." ? "□" : segment)
            .ToArray();
        return segments.Length == 0 ? "file" : string.Join('/', segments);
    }
}
