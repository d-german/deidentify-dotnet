using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace Deidentify.Imaging;

/// <summary>Rasterizes, redacts, and rebuilds PDFs without hidden text or annotation layers.</summary>
public sealed class PdfRedactor
{
    private const string Operation = "RedactPdf";
    private readonly ImageRedactor _imageRedactor;

    /// <summary>Creates a PDF redactor.</summary>
    public PdfRedactor(ImageRedactor imageRedactor) =>
        _imageRedactor = imageRedactor ?? throw new ArgumentNullException(nameof(imageRedactor));

    /// <summary>Redacts a PDF into a flattened PDF.</summary>
    public async Task<Result<DocumentRedactionResult, DeidentifyError>> RedactAsync(
        ReadOnlyMemory<byte> pdf,
        AnalysisOptions? analysisOptions = null,
        ImageRedactionOptions? imageOptions = null,
        int maximumPages = 100,
        CancellationToken cancellationToken = default)
    {
        if (pdf.Length == 0)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "PDF data is required.", Operation));
        }

        if (maximumPages <= 0)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Maximum PDF pages must be positive.", Operation));
        }

        try
        {
            using var reader = DocLib.Instance.GetDocReader(pdf.ToArray(), new PageDimensions(2));
            var pageCount = reader.GetPageCount();
            if (pageCount > maximumPages)
            {
                return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                    new DeidentifyError(DeidentifyErrorCode.InputTooLarge, "PDF exceeds the configured page limit.", Operation));
            }

            using var output = new MemoryStream();
            using var document = SKDocument.CreatePdf(output);
            var detections = ImmutableArray.CreateBuilder<Detection>();
            var regions = 0;

            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = reader.GetPageReader(pageIndex);
                var width = page.GetPageWidth();
                var height = page.GetPageHeight();
                using var bitmap = CreateBitmap(page.GetImage(), width, height);
                using var sourceImage = SKImage.FromBitmap(bitmap);
                using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
                var redacted = await _imageRedactor.RedactAsync(
                    sourceData.ToArray(),
                    analysisOptions,
                    imageOptions,
                    cancellationToken);
                if (redacted.IsFailure)
                {
                    return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                        redacted.Error.WithOperation(Operation));
                }

                using var pageBitmap = SKBitmap.Decode(redacted.Value.PngImage);
                var canvas = document.BeginPage(width, height);
                canvas.DrawBitmap(pageBitmap, 0, 0);
                document.EndPage();
                detections.AddRange(redacted.Value.Detections);
                regions += redacted.Value.RedactedRegions.Length;
            }

            document.Close();
            return Result.Success<DocumentRedactionResult, DeidentifyError>(
                new DocumentRedactionResult(output.ToArray(), pageCount, detections.ToImmutable(), regions));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.UnsupportedFormat, "PDF processing failed safely.", Operation));
        }
    }

    private static SKBitmap CreateBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }
}

/// <summary>Flattened document output and aggregate safe metadata.</summary>
public sealed record DocumentRedactionResult(
    byte[] Document,
    int PageCount,
    ImmutableArray<Detection> Detections,
    int RedactedRegionCount);
