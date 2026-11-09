using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Imaging;

namespace Shoreline.Api.Features;

/// <summary>
/// Magick-backed <see cref="IProofreadImageRenderer"/> that delegates to
/// <see cref="ProofreadImageProcessor"/> in Shoreline.Imaging.
/// Registered explicitly by hosts that ship the Imaging assembly; the shared
/// <see cref="ProofreadFeedbackRegistration"/> keeps the null renderer as the
/// safe default so hosts without Imaging still compile and run.
/// </summary>
public sealed class MagickProofreadImageRenderer : IProofreadImageRenderer
{
    public string Fingerprint(IReadOnlyList<ProofreadMarkerLabel> labels, int opacity) =>
        ProofreadImageProcessor.Fingerprint(ToImaging(labels), opacity);

    public byte[]? Render(byte[] source, IReadOnlyList<ProofreadMarkerLabel> labels, int opacity) =>
        ProofreadImageProcessor.Render(source, ToImaging(labels), opacity);

    private static List<ProofreadMarker> ToImaging(IReadOnlyList<ProofreadMarkerLabel> labels)
    {
        var mapped = new List<ProofreadMarker>(labels.Count);
        foreach (var label in labels)
            mapped.Add(new ProofreadMarker(label.X, label.Y, label.PositionType, label.LabelNum));
        return mapped;
    }
}

public static class ProofreadImagingAdapter
{
    /// <summary>
    /// Replaces the null renderer with the Magick-backed implementation.
    /// Call after <see cref="ProofreadFeedbackRegistration.AddProofreadFeedbackServices"/>.
    /// </summary>
    public static IServiceCollection AddMagickProofreadImageRenderer(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IProofreadImageRenderer, MagickProofreadImageRenderer>());
        return services;
    }
}
