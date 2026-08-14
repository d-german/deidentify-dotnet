using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Deidentify.Imaging;

/// <summary>An integer pixel rectangle.</summary>
public readonly record struct PixelRectangle(int X, int Y, int Width, int Height)
{
    /// <summary>The exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>The exclusive bottom edge.</summary>
    public int Bottom => Y + Height;

    internal PixelRectangle Inflate(int padding, int imageWidth, int imageHeight)
    {
        var left = Math.Max(0, X - padding);
        var top = Math.Max(0, Y - padding);
        var right = Math.Min(imageWidth, Right + padding);
        var bottom = Math.Min(imageHeight, Bottom + padding);
        return new PixelRectangle(left, top, right - left, bottom - top);
    }
}

/// <summary>One OCR word and its stable span in the assembled OCR text.</summary>
public sealed record OcrWord(
    string Text,
    double Confidence,
    PixelRectangle Bounds,
    TextSpan Span);

/// <summary>OCR text with immutable word-to-pixel mapping.</summary>
public sealed record OcrResult(
    string Text,
    ImmutableArray<OcrWord> Words);

/// <summary>Extracts local OCR words and bounding boxes.</summary>
public interface IOcrEngine
{
    /// <summary>Recognizes one encoded image without external services.</summary>
    Task<Result<OcrResult, DeidentifyError>> RecognizeAsync(
        ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounds and output settings for image redaction.</summary>
public sealed record ImageRedactionOptions
{
    /// <summary>Creates validated redaction options.</summary>
    public static Result<ImageRedactionOptions, DeidentifyError> Create(
        long maximumImageBytes = 50 * 1024 * 1024,
        long maximumPixels = 100_000_000,
        int paddingPixels = 2) =>
        maximumImageBytes <= 0 || maximumPixels <= 0 || paddingPixels < 0
            ? Result.Failure<ImageRedactionOptions, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Image redaction bounds are invalid."))
            : Result.Success<ImageRedactionOptions, DeidentifyError>(
                new ImageRedactionOptions(maximumImageBytes, maximumPixels, paddingPixels));

    private ImageRedactionOptions(long maximumImageBytes, long maximumPixels, int paddingPixels)
    {
        MaximumImageBytes = maximumImageBytes;
        MaximumPixels = maximumPixels;
        PaddingPixels = paddingPixels;
    }

    /// <summary>Maximum encoded input size.</summary>
    public long MaximumImageBytes { get; }

    /// <summary>Maximum decoded pixel count.</summary>
    public long MaximumPixels { get; }

    /// <summary>Black-box padding around mapped OCR words.</summary>
    public int PaddingPixels { get; }

    /// <summary>Default safe bounds.</summary>
    public static ImageRedactionOptions Default { get; } = Create().Value;
}

/// <summary>Flattened redacted image and safe metadata.</summary>
public sealed record ImageRedactionResult(
    byte[] PngImage,
    ImmutableArray<Detection> Detections,
    ImmutableArray<PixelRectangle> RedactedRegions);
