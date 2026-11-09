using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed class ProofreadTip
{
    public string? Author { get; set; }
    public string Content { get; set; } = "";
    public string EditTime { get; set; } = "";
}

public sealed class ProofreadLabel
{
    public string SourceId { get; set; } = "";
    public int LabelNum { get; set; }
    public string SourceText { get; set; } = "";
    public string OrigTranslation { get; set; } = "";
    public string ProofreadTranslation { get; set; } = "";
    public bool IsChanged { get; set; }
    public string DiffHtml { get; set; } = "";
    public List<ProofreadTip> Tips { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public int PositionType { get; set; } = 1;
}

public sealed class ProofreadPage
{
    public int PageNumber { get; set; }
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string MarkedImageUrl { get; set; } = "";
    public int TotalSources { get; set; }
    public int ChangedCount { get; set; }
    public int TipCount { get; set; }
    public List<int> ChangedLabelNums { get; set; } = [];
    public List<ProofreadLabel> Labels { get; set; } = [];
}

public sealed record ProofreadDraftResult(int StatusCode, Dictionary<string, object?> Body);

/// <summary>Normalized marker geometry passed to the imaging implementation.</summary>
public sealed record ProofreadMarkerLabel(double X, double Y, int PositionType, int LabelNum);

/// <summary>
/// Narrow port between the API assembly and the optional imaging assembly.
/// The shared API project intentionally does not reference Shoreline.Imaging;
/// the isolated verification snapshot (and the future host wiring) registers
/// the Magick-backed implementation.
/// </summary>
public interface IProofreadImageRenderer
{
    string Fingerprint(IReadOnlyList<ProofreadMarkerLabel> labels, int opacity);
    byte[]? Render(byte[] source, IReadOnlyList<ProofreadMarkerLabel> labels, int opacity);
}

public sealed class NullProofreadImageRenderer : IProofreadImageRenderer
{
    public string Fingerprint(IReadOnlyList<ProofreadMarkerLabel> labels, int opacity) => "disabled";

    public byte[]? Render(byte[] source, IReadOnlyList<ProofreadMarkerLabel> labels, int opacity) => null;
}

internal static class ProofreadBson
{
    public static string Text(BsonDocument document, string key, string fallback = "") =>
        document.TryGetValue(key, out var value) && value.IsString ? value.AsString : fallback;

    public static long Integer(BsonDocument document, string key, long fallback = 0) =>
        document.TryGetValue(key, out var value) && value.IsNumeric ? value.ToInt64() : fallback;

    public static double Double(BsonDocument document, string key, double fallback = 0) =>
        document.TryGetValue(key, out var value) && value.IsNumeric ? value.ToDouble() : fallback;

    public static bool Flag(BsonDocument document, string key, bool fallback = false) =>
        document.TryGetValue(key, out var value) && value.IsBoolean ? value.AsBoolean : fallback;

    public static string? NullableId(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || !value.IsObjectId) return null;
        return value.AsObjectId.ToString();
    }
}
