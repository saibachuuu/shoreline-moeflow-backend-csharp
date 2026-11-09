using ImageMagick;

namespace Shoreline.Imaging;

/// <summary>
/// Bounded, one-frame image processing compatible with the legacy Pillow task.
/// The caller downloads the original to a local path and owns all temporary
/// files; this type never buffers the original file in memory as a byte[].
/// </summary>
public sealed class ImageProcessingOptions
{
    public long MaxSourceBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxPixels { get; init; } = 100_000_000;
    public int MaxDimension { get; init; } = 20_000;
    public ulong MaxDecodeMemoryBytes { get; init; } = 512UL * 1024 * 1024;
    public ulong MaxDecodeDiskBytes { get; init; } = 1024UL * 1024 * 1024;
}

public enum ImageProcessingFailure
{
    SourceTooLarge,
    FormatNotSupported,
    DimensionsExceeded,
    DecodeFailed,
}

public sealed class ImageProcessingException : Exception
{
    public ImageProcessingException(string message, ImageProcessingFailure failure)
        : base(message)
    {
        Failure = failure;
    }

    public ImageProcessingException(string message, ImageProcessingFailure failure, Exception inner)
        : base(message, inner)
    {
        Failure = failure;
    }

    public ImageProcessingFailure Failure { get; }
}

public sealed record ImageProcessingResult(
    string CoverPath,
    string ResamplePath,
    long CoverBytes,
    long ResampleBytes,
    int CoverWidth,
    int CoverHeight,
    int ResampleWidth,
    int ResampleHeight);

public sealed class ImageProcessor
{
    private static readonly HashSet<MagickFormat> AllowedFormats =
    [
        MagickFormat.Jpeg,
        MagickFormat.Png,
        MagickFormat.Bmp,
        MagickFormat.Gif,
        MagickFormat.WebP,
    ];

    private static int resourceLimitsApplied;
    private readonly ImageProcessingOptions options;

    public ImageProcessor(ImageProcessingOptions? options = null)
    {
        this.options = options ?? new ImageProcessingOptions();
        ApplyResourceLimits(this.options);
    }

    public ImageProcessingResult Process(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("source image is missing", sourcePath);
        }

        if (source.Length > options.MaxSourceBytes)
        {
            throw new ImageProcessingException(
                "source image exceeds the configured byte limit",
                ImageProcessingFailure.SourceTooLarge);
        }

        Directory.CreateDirectory(outputDirectory);
        var token = Guid.NewGuid().ToString("N");
        var coverPath = Path.Combine(outputDirectory, token + "-cover.webp");
        var resamplePath = Path.Combine(outputDirectory, token + "-resample.webp");

        try
        {
            // Validate the header before decoding. This also rejects SVG/PDF and
            // any other delegate-backed format before ImageMagick can invoke an
            // external coder.
            MagickImageInfo info;
            try
            {
                info = new MagickImageInfo(sourcePath);
            }
            catch (MagickException error)
            {
                throw new ImageProcessingException(
                    "source image could not be decoded", ImageProcessingFailure.DecodeFailed, error);
            }

            if (!AllowedFormats.Contains(info.Format))
            {
                throw new ImageProcessingException(
                    "source image format is not supported", ImageProcessingFailure.FormatNotSupported);
            }

            if (info.Width > options.MaxDimension || info.Height > options.MaxDimension ||
                (long)info.Width * info.Height > options.MaxPixels)
            {
                throw new ImageProcessingException(
                    "source image exceeds the configured dimension limit",
                    ImageProcessingFailure.DimensionsExceeded);
            }

            using var original = ReadFirstFrame(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();

            if (!AllowedFormats.Contains(original.Format))
            {
                throw new ImageProcessingException(
                    "source image format is not supported", ImageProcessingFailure.FormatNotSupported);
            }

            if (original.Width > options.MaxDimension || original.Height > options.MaxDimension ||
                (long)original.Width * original.Height > options.MaxPixels)
            {
                throw new ImageProcessingException(
                    "source image exceeds the configured dimension limit",
                    ImageProcessingFailure.DimensionsExceeded);
            }

            using var cover = new MagickImage(original);
            WriteCover(cover, coverPath);
            cancellationToken.ThrowIfCancellationRequested();

            using var resampled = new MagickImage(original);
            WriteResample(resampled, resamplePath);

            return new ImageProcessingResult(
                coverPath,
                resamplePath,
                new FileInfo(coverPath).Length,
                new FileInfo(resamplePath).Length,
                180,
                140,
                (int)resampled.Width,
                (int)resampled.Height);
        }
        catch
        {
            TryDelete(coverPath);
            TryDelete(resamplePath);
            throw;
        }
    }

    private static MagickImage ReadFirstFrame(string sourcePath)
    {
        try
        {
            var image = new MagickImage(sourcePath);
            // Python copies the decoded pixel buffer and never calls exif_transpose.
            // Normalising the metadata orientation prevents a later writer from
            // rotating pixels that Pillow would have left untouched.
            image.Orientation = Orientation.TopLeft;
            return image;
        }
        catch (MagickException error)
        {
            throw new ImageProcessingException(
                "source image could not be decoded", ImageProcessingFailure.DecodeFailed, error);
        }
    }

    private static void WriteCover(MagickImage image, string destination)
    {
        var width = (double)image.Width;
        var height = (double)image.Height;
        var scale = Math.Max(180d / width, 140d / height);
        var resizedWidth = Math.Max(180, (int)Math.Round(width * scale, MidpointRounding.ToEven));
        var resizedHeight = Math.Max(140, (int)Math.Round(height * scale, MidpointRounding.ToEven));

        image.FilterType = FilterType.Lanczos;
        image.Resize((uint)resizedWidth, (uint)resizedHeight);
        image.Crop(180, 140, Gravity.Center);
        image.ResetPage();
        NormalizeOutput(image);
        image.Write(destination);
    }

    private static void WriteResample(MagickImage image, string destination)
    {
        var sourceWidth = (long)image.Width;
        var sourceHeight = (long)image.Height;
        if (sourceWidth > 1920)
        {
            var ratio = 1920d / sourceWidth;
            var targetHeight = Math.Max(1, (int)(sourceHeight * ratio));
            image.FilterType = FilterType.Lanczos;
            image.Resize(1920u, (uint)targetHeight);
        }

        NormalizeOutput(image);
        image.Write(destination);
    }

    private static void NormalizeOutput(MagickImage image)
    {
        if (image.HasAlpha)
        {
            // Pillow converts RGBA/P to RGB before encoding. Removing the alpha
            // channel keeps the RGB samples, matching that behaviour.
            image.Alpha(AlphaAction.Remove);
        }

        image.Strip();
        image.Quality = 50;
        image.Format = MagickFormat.WebP;
    }

    private static void ApplyResourceLimits(ImageProcessingOptions options)
    {
        if (Interlocked.Exchange(ref resourceLimitsApplied, 1) == 1)
        {
            return;
        }

        // These are process-wide ImageMagick limits. They are deliberately fixed
        // after the first processor instance; callers cannot raise them per file.
        ResourceLimits.Memory = options.MaxDecodeMemoryBytes;
        ResourceLimits.Disk = options.MaxDecodeDiskBytes;
        ResourceLimits.Thread = 2;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
