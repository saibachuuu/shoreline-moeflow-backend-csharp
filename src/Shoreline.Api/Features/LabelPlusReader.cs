using System.Globalization;
using System.Text.RegularExpressions;

namespace Shoreline.Api.Features;

/// <summary>
/// A label parsed from a LabelPlus export. Coordinates are intentionally kept
/// as raw doubles: the legacy model validates the 0..1 range when a source is
/// created, so out-of-range values must fail there rather than being clamped.
/// </summary>
public sealed record LabelPlusLabel(double X, double Y, long PositionType, string Translation);

/// <summary>One file entry and its labels, in LabelPlus export order.</summary>
public sealed record LabelPlusFile(string FileName, IReadOnlyList<LabelPlusLabel> Labels);

/// <summary>
/// Port of app/utils/labelplus.py::load_from_labelplus. The parser is tolerant
/// of unrelated header lines: only lines matching the two legacy regular
/// expressions affect the result. A label before any file line is a parse
/// failure, matching the Python IndexError path.
/// </summary>
public static class LabelPlusReader
{
    private static readonly Regex FilePattern = new(@".+>>>\[(.+)\]<<<.+", RegexOptions.Compiled);
    private static readonly Regex LabelPattern = new(@".+---\[.+\]---.+\[(.+),(.+),(.+)\]", RegexOptions.Compiled);

    public static List<LabelPlusFile> Read(string? text)
    {
        var files = new List<LabelPlusFile>();
        if (string.IsNullOrEmpty(text)) return files;

        var lines = SplitLines(text);
        var fileIndex = -1;
        var labelIndex = -1;

        foreach (var line in lines)
        {
            var file = FilePattern.Match(line);
            if (file.Success)
            {
                var name = file.Groups[1].Value;
                if (name.Length > 0)
                {
                    files.Add(new LabelPlusFile(name, new List<LabelPlusLabel>()));
                    fileIndex++;
                    labelIndex = -1;
                    continue;
                }
            }

            var label = LabelPattern.Match(line);
            if (label.Success)
            {
                var xText = label.Groups[1].Value;
                var yText = label.Groups[2].Value;
                var positionText = label.Groups[3].Value;
                if (xText.Length > 0)
                {
                    if (fileIndex < 0) throw new FormatException("label before file");
                    var x = ParseDouble(xText);
                    var y = ParseDouble(yText);
                    var position = ParseInt(positionText);
                    var labels = (List<LabelPlusLabel>)files[fileIndex].Labels;
                    labels.Add(new LabelPlusLabel(x, y, position, ""));
                    labelIndex++;
                    continue;
                }
            }

            if (fileIndex >= 0 && labelIndex >= 0)
            {
                var labels = (List<LabelPlusLabel>)files[fileIndex].Labels;
                labels[labelIndex] = labels[labelIndex] with { Translation = labels[labelIndex].Translation + line + "\n" };
            }
        }

        // Python strips exactly one final "\n" from each accumulated translation.
        var result = new List<LabelPlusFile>(files.Count);
        foreach (var file in files)
        {
            var labels = file.Labels
                .Select(label => label.Translation.EndsWith('\n')
                    ? label with { Translation = label.Translation[..^1] }
                    : label)
                .ToArray();
            result.Add(file with { Labels = labels });
        }
        return result;
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        // str.splitlines() does not emit a final empty element for a trailing
        // newline; dropping exactly one mirrors that behaviour.
        if (normalized.EndsWith('\n') && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static double ParseDouble(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException("invalid label coordinate");
    }

    private static long ParseInt(string value)
    {
        // Python ints are unbounded. The importer only distinguishes IN from
        // everything else, so the widest CLR integer keeps old exports working
        // instead of rejecting huge position_type values as malformed.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException("invalid label position type");
    }
}
