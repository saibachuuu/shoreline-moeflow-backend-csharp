using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Features;
using Shoreline.Api.Formats;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Reads the Python AST reference vectors committed under
/// tools/legacy-reference/fixtures/goldens.json. Expected strings are never
/// produced by the C# writer; they are compared byte-for-byte.
/// </summary>
public sealed class OutputGoldenTests
{
    [Fact]
    public void LabelPlus_FileToLabelPlus_MatchesPythonGolden()
    {
        using var document = LoadGolden();
        var export = document.RootElement.GetProperty("fixtures").GetProperty("labelplus_export");
        var file = export.GetProperty("file");
        var displayName = string.Join("/",
            file.GetProperty("ancestors").EnumerateArray().Select(value => value.GetString())
                .Append(file.GetProperty("name").GetString()));

        var body =
            LabelPlusWriter.SourceEntry(1, new BsonDouble(12.5), new BsonDouble(34.25), 0) +
            LabelPlusWriter.TranslationLine(Translation("普通", "校对 😀\n第二行 <&>")) +
            LabelPlusWriter.SourceEntry(2, new BsonInt32(0), new BsonInt32(0), 1) +
            LabelPlusWriter.TranslationLine(Translation("普通\n换行", null)) +
            LabelPlusWriter.SourceEntry(3, new BsonDouble(-1.5), new BsonInt32(2), 2) +
            LabelPlusWriter.TranslationLine(string.Empty);

        Assert.Equal(export.GetProperty("plain").GetString(),
            LabelPlusWriter.FileHeader(displayName) + body);
        Assert.Equal(export.GetProperty("export_name").GetString(),
            LabelPlusWriter.FileHeader("custom/名字 😀.png") + body);
    }

    [Fact]
    public void LabelPlus_StaffFirstAndLast_MatchesPythonGolden()
    {
        using var document = LoadGolden();
        var export = document.RootElement.GetProperty("fixtures").GetProperty("labelplus_export");
        var file = export.GetProperty("file");
        var displayName = string.Join("/",
            file.GetProperty("ancestors").EnumerateArray().Select(value => value.GetString())
                .Append(file.GetProperty("name").GetString()));
        var header = LabelPlusWriter.FileHeader(displayName);
        var staff = export.GetProperty("staff_block").GetString()!;
        var body =
            LabelPlusWriter.SourceEntry(1, new BsonDouble(12.5), new BsonDouble(34.25), 0) +
            LabelPlusWriter.TranslationLine(Translation("普通", "校对 😀\n第二行 <&>")) +
            LabelPlusWriter.SourceEntry(2, new BsonInt32(0), new BsonInt32(0), 1) +
            LabelPlusWriter.TranslationLine(Translation("普通\n换行", null)) +
            LabelPlusWriter.SourceEntry(3, new BsonDouble(-1.5), new BsonInt32(2), 2) +
            LabelPlusWriter.TranslationLine(string.Empty);

        Assert.Equal(export.GetProperty("staff_first").GetString(), header + staff + body);
        Assert.Equal(export.GetProperty("staff_last").GetString(), header + body + staff);
    }

    [Fact]
    public void ExportFileNames_ManualOrder_MatchesPythonGolden()
    {
        using var document = LoadGolden();
        var order = document.RootElement.GetProperty("fixtures").GetProperty("file_order");
        var files = order.GetProperty("files").EnumerateArray()
            .Select(row => new BsonDocument
            {
                ["_id"] = ObjectId.Parse(row.GetProperty("id").GetString()),
                ["n"] = row.GetProperty("name").GetString(),
                ["mo"] = row.GetProperty("manual_order").ValueKind == JsonValueKind.Null
                    ? BsonNull.Value
                    : new BsonInt32(row.GetProperty("manual_order").GetInt32())
            })
            .ToArray();

        var actual = LabelPlusWriter.ExportFileNames(files);
        foreach (var entry in order.GetProperty("export_with_manual").EnumerateObject())
            Assert.Equal(entry.Value.GetString(), actual[ObjectId.Parse(entry.Name)]);

        var withoutManual = files
            .Select(file => new BsonDocument
            {
                ["_id"] = file["_id"],
                ["n"] = file["n"],
                ["mo"] = BsonNull.Value
            })
            .ToArray();
        Assert.Empty(LabelPlusWriter.ExportFileNames(withoutManual));
    }

    private static string Translation(string content, string? proofread)
    {
        var translation = new BsonDocument
        {
            ["c"] = content,
            ["p"] = proofread is null ? BsonNull.Value : proofread
        };
        return LabelPlusWriter.TranslationContent(translation);
    }

    private static JsonDocument LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "legacy-reference",
            "fixtures", "goldens.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}

