using MongoDB.Bson;

namespace Shoreline.Api.Features;

public readonly record struct BatchSelection(ObjectId SourceId, ObjectId TranslationId);

public sealed record SourceEditRequest(
    string? Content = null,
    double? X = null,
    double? Y = null,
    IReadOnlyList<double>? Vertices = null,
    int? PositionType = null)
{
    public bool HasAny => Content is not null || X is not null || Y is not null ||
                          Vertices is not null || PositionType is not null;
}

public sealed record TranslationEditRequest(
    string? Content = null,
    string? ProofreadContent = null,
    bool? Selected = null)
{
    public bool HasAny => Content is not null || ProofreadContent is not null || Selected is not null;
}

public sealed record LabelListResult(
    IReadOnlyList<Dictionary<string, object?>> Items,
    long Total,
    int Page,
    int Limit,
    bool Paged);
