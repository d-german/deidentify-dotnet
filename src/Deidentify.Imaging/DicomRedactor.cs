using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using SkiaSharp;

namespace Deidentify.Imaging;

/// <summary>Applies DICOM PS 3.15 metadata anonymization and burned-in pixel redaction.</summary>
public sealed class DicomRedactor
{
    private const string Operation = "RedactDicom";
    private readonly ImageRedactor _imageRedactor;

    /// <summary>Creates a DICOM redactor.</summary>
    public DicomRedactor(ImageRedactor imageRedactor) =>
        _imageRedactor = imageRedactor ?? throw new ArgumentNullException(nameof(imageRedactor));

    /// <summary>Redacts metadata and every rendered frame into uncompressed RGB DICOM pixel data.</summary>
    public async Task<Result<DocumentRedactionResult, DeidentifyError>> RedactAsync(
        ReadOnlyMemory<byte> dicom,
        AnalysisOptions? analysisOptions = null,
        ImageRedactionOptions? imageOptions = null,
        int maximumFrames = 100,
        CancellationToken cancellationToken = default)
    {
        if (dicom.Length == 0)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "DICOM data is required.", Operation));
        }

        if (maximumFrames <= 0)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Maximum DICOM frames must be positive.", Operation));
        }

        try
        {
            using var input = new MemoryStream(dicom.ToArray(), false);
            var sourceFile = await DicomFile.OpenAsync(input, FileReadOption.ReadAll);
            var anonymized = new DicomAnonymizer().Anonymize(sourceFile);
            var image = new DicomImage(sourceFile.Dataset);
            if (image.NumberOfFrames > maximumFrames)
            {
                return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                    new DeidentifyError(DeidentifyErrorCode.InputTooLarge, "DICOM exceeds the configured frame limit.", Operation));
            }

            var outputDataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
            outputDataset.Add(anonymized.Dataset.Where(item => item.Tag != DicomTag.PixelData));
            outputDataset.Remove(item => item.Tag.Group is >= 0x6000 and <= 0x60FF);
            outputDataset.Remove(DicomTag.GraphicAnnotationSequence);
            outputDataset.AddOrUpdate(DicomTag.Rows, (ushort)image.Height);
            outputDataset.AddOrUpdate(DicomTag.Columns, (ushort)image.Width);
            outputDataset.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)3);
            outputDataset.AddOrUpdate(DicomTag.PhotometricInterpretation, "RGB");
            outputDataset.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0);
            outputDataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)8);
            outputDataset.AddOrUpdate(DicomTag.BitsStored, (ushort)8);
            outputDataset.AddOrUpdate(DicomTag.HighBit, (ushort)7);
            outputDataset.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
            outputDataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "NO");
            if (image.NumberOfFrames > 1)
            {
                outputDataset.AddOrUpdate(DicomTag.NumberOfFrames, image.NumberOfFrames.ToString());
            }

            var pixelData = DicomPixelData.Create(outputDataset, true);
            var detections = ImmutableArray.CreateBuilder<Detection>();
            var regionCount = 0;

            for (var frame = 0; frame < image.NumberOfFrames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var rendered = image.RenderImage(frame);
                using var bitmap = CreateBitmap(rendered.AsBytes(), image.Width, image.Height);
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

                using var redactedBitmap = SKBitmap.Decode(redacted.Value.PngImage);
                pixelData.AddFrame(new MemoryByteBuffer(ToRgb(redactedBitmap)));
                detections.AddRange(redacted.Value.Detections);
                regionCount += redacted.Value.RedactedRegions.Length;
            }

            var outputFile = new DicomFile(outputDataset);
            using var output = new MemoryStream();
            await outputFile.SaveAsync(output);
            return Result.Success<DocumentRedactionResult, DeidentifyError>(
                new DocumentRedactionResult(output.ToArray(), image.NumberOfFrames, detections.ToImmutable(), regionCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<DocumentRedactionResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.UnsupportedFormat, "DICOM processing failed safely.", Operation));
        }
    }

    private static SKBitmap CreateBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }

    private static byte[] ToRgb(SKBitmap bitmap)
    {
        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        var index = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                rgb[index++] = color.Red;
                rgb[index++] = color.Green;
                rgb[index++] = color.Blue;
            }
        }

        return rgb;
    }
}
