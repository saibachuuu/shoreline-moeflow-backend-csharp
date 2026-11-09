using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;

namespace Shoreline.Imaging;

/// <summary>
/// Marker label geometry. Coordinates are normalized (0..1) like Source.x/y
/// in the legacy backend. PositionType uses the legacy SourcePositionType
/// values (1 = inside, 2 = outside).
/// </summary>
public sealed record ProofreadMarker(double X, double Y, int PositionType, int LabelNum);

/// <summary>
/// Renders the proofread marker overlay used by the proofread feedback mail.
/// This is a direct port of app/services/proofread_image.py. Pixel colors,
/// dimensions, alpha rounding and the cache fingerprint are kept numerically
/// equivalent; font rasterization may differ slightly between Pillow and
/// ImageMagick and is documented instead of claimed byte-identical.
/// </summary>
public static class ProofreadImageProcessor
{
    public static readonly (byte R, byte G, byte B, byte A) InColor = (255, 101, 124, 235);
    public static readonly (byte R, byte G, byte B, byte A) OutColor = (255, 213, 131, 235);
    public static readonly (byte R, byte G, byte B, byte A) InTextColor = (255, 255, 255, 255);
    public static readonly (byte R, byte G, byte B, byte A) OutTextColor = (102, 77, 3, 255);
    public const double MarkerRatio = 0.05;
    public const int MarkerMinRadius = 9;
    public const int TargetWidth = 640;
    public const int DefaultOpacity = 92;

    /// <summary>
    /// Legacy marker_fingerprint: md5(repr([(x, y, position_type, label_num), ...] + [int(opacity)]))[:8].
    /// </summary>
    public static string Fingerprint(IReadOnlyList<ProofreadMarker> labels, int opacity = DefaultOpacity)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0) text.Append(", ");
            var label = labels[i];
            text.Append('(')
                .Append(PythonFloat(label.X)).Append(", ")
                .Append(PythonFloat(label.Y)).Append(", ")
                .Append(label.PositionType.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(label.LabelNum.ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }
        if (labels.Count > 0) text.Append(", ");
        text.Append(Math.Truncate((double)opacity).ToString(CultureInfo.InvariantCulture)).Append(']');
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant()[..8];
    }

    /// <summary>
    /// Renders a WebP overlay. Returns null when the source cannot be decoded.
    /// Never mutates or re-encodes the caller's base image bytes.
    /// </summary>
    public static byte[]? Render(byte[] source, IReadOnlyList<ProofreadMarker> labels, int opacity = DefaultOpacity)
    {
        if (source.Length == 0 || labels.Count == 0) return null;
        var clamped = Math.Clamp(opacity, 0, 100);
        var alpha = (byte)Math.Round(clamped * 255.0 / 100.0, MidpointRounding.ToEven);
        try
        {
            using var image = new MagickImage(source);
            if (image.Width > TargetWidth)
            {
                var width = (uint)TargetWidth;
                var height = (uint)Math.Round(image.Height * (double)TargetWidth / image.Width, MidpointRounding.ToEven);
                image.FilterType = FilterType.Lanczos;
                image.Resize(width, Math.Max(1u, height));
            }

            var radius = Math.Max(MarkerMinRadius, (int)Math.Round(image.Width * MarkerRatio / 2.0, MidpointRounding.ToEven));
            var fontSize = Math.Max(9, radius);
            foreach (var label in labels)
            {
                if (label.X == 0 && label.Y == 0) continue;
                var centerX = image.Width * label.X;
                var centerY = image.Height * label.Y;
                var outside = label.PositionType == 2;
                var fill = outside
                    ? MagickColor.FromRgba(OutColor.R, OutColor.G, OutColor.B, alpha)
                    : MagickColor.FromRgba(InColor.R, InColor.G, InColor.B, alpha);
                new Drawables()
                    .FillColor(fill)
                    .Ellipse(centerX, centerY, radius, radius, 0, 360)
                    .Draw(image);

                var textColor = outside
                    ? MagickColor.FromRgba(OutTextColor.R, OutTextColor.G, OutTextColor.B, OutTextColor.A)
                    : MagickColor.FromRgba(InTextColor.R, InTextColor.G, InTextColor.B, InTextColor.A);
                try
                {
                    DrawLabelNumber(image, centerX, centerY, fontSize, textColor, label.LabelNum);
                }
                catch (MagickException)
                {
                    // A missing host font must not fail the whole render; the
                    // marker geometry and colors remain deterministic.
                }
            }

            image.Format = MagickFormat.WebP;
            image.Quality = 85;
            return image.ToByteArray();
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// Portable fallback: ImageMagick's default font when present, otherwise a
    /// short list of common open-source families. Missing fonts skip only the
    /// number, never the marker itself.
    /// </summary>
    private static void DrawLabelNumber(MagickImage image, double x, double y, int fontSize, MagickColor color, int number)
    {
        foreach (var family in new[] { (string?)null, "DejaVu Sans", "Liberation Sans", "Noto Sans" })
        {
            try
            {
                var draw = new Drawables()
                    .FillColor(color)
                    .FontPointSize(fontSize)
                    .TextAlignment(TextAlignment.Center);
                if (family is not null) draw = draw.Font(family);
                draw.Text(x, y, number.ToString(CultureInfo.InvariantCulture)).Draw(image);
                return;
            }
            catch (MagickException)
            {
                // Try the next portable family.
            }
        }
    }

    /// <summary>Python repr() compatible float formatting for the cache key.</summary>
    public static string PythonFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = text.IndexOfAny(['e', 'E']);
        if (exponent >= 0)
        {
            text = text[..exponent].ToLowerInvariant() + "e" + text[(exponent + 1)..].ToLowerInvariant();
            return text;
        }
        if (!text.Contains('.')) text += ".0";
        return text;
    }
}
