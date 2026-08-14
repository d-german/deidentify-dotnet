using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using SkiaSharp;

namespace Deidentify.Imaging;

/// <summary>Maps sensitive OCR spans to pixels and flattens irreversible black redactions.</summary>
public sealed class ImageRedactor
{
    private const string Operation = "RedactImage";
    private readonly IOcrEngine _ocrEngine;
    private readonly SensitiveDataAnalyzer _analyzer;

    /// <summary>Creates an image redactor.</summary>
    public ImageRedactor(IOcrEngine ocrEngine, SensitiveDataAnalyzer? analyzer = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _analyzer = analyzer ?? new SensitiveDataAnalyzer();
    }

    /// <summary>Redacts a common encoded image and returns flattened PNG bytes.</summary>
    public async Task<Result<ImageRedactionResult, DeidentifyError>> RedactAsync(
        ReadOnlyMemory<byte> encodedImage,
        AnalysisOptions? analysisOptions = null,
        ImageRedactionOptions? imageOptions = null,
        CancellationToken cancellationToken = default)
    {
        imageOptions ??= ImageRedactionOptions.Default;
        if (encodedImage.Length == 0)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Image data is required.", Operation));
        }

        if (encodedImage.Length > imageOptions.MaximumImageBytes)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InputTooLarge, "Image exceeds the configured maximum size.", Operation));
        }

        var ocr = await _ocrEngine.RecognizeAsync(encodedImage, cancellationToken);
        if (ocr.IsFailure)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                ocr.Error.WithOperation(Operation));
        }

        var analysis = _analyzer.Analyze(ocr.Value.Text, analysisOptions);
        if (analysis.IsFailure)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                analysis.Error.WithOperation(Operation));
        }

        SKBitmap? bitmap;
        try
        {
            bitmap = SKBitmap.Decode(encodedImage.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.UnsupportedFormat, "The image format is unsupported.", Operation));
        }

        if (bitmap is null)
        {
            return Result.Failure<ImageRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.UnsupportedFormat, "The image format is unsupported.", Operation));
        }

        using (bitmap)
        {
            try
            {
                if ((long)bitmap.Width * bitmap.Height > imageOptions.MaximumPixels)
                {
                    return Result.Failure<ImageRedactionResult, DeidentifyError>(
                        new DeidentifyError(DeidentifyErrorCode.InputTooLarge, "Decoded image exceeds the configured pixel limit.", Operation));
                }

                var regions = MapRegions(
                    analysis.Value.Detections,
                    ocr.Value.Words,
                    bitmap.Width,
                    bitmap.Height,
                    imageOptions.PaddingPixels);
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint
                {
                    Color = SKColors.Black,
                    Style = SKPaintStyle.Fill
                };
                foreach (var region in regions)
                {
                    canvas.DrawRect(region.X, region.Y, region.Width, region.Height, paint);
                }

                canvas.Flush();
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return Result.Success<ImageRedactionResult, DeidentifyError>(
                    new ImageRedactionResult(data.ToArray(), analysis.Value.Detections, regions));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Result.Failure<ImageRedactionResult, DeidentifyError>(
                    new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "Image redaction failed safely.", Operation));
            }
        }
    }

    private static ImmutableArray<PixelRectangle> MapRegions(
        ImmutableArray<Detection> detections,
        ImmutableArray<OcrWord> words,
        int imageWidth,
        int imageHeight,
        int padding)
    {
        var regions = ImmutableArray.CreateBuilder<PixelRectangle>();
        foreach (var span in detections.Select(detection => detection.Span))
        {
            foreach (var word in words)
            {
                if (span.Start < word.Span.End && word.Span.Start < span.End)
                {
                    regions.Add(word.Bounds.Inflate(padding, imageWidth, imageHeight));
                }
            }
        }

        return
        [
            .. regions.Distinct()
        ];
    }
}
