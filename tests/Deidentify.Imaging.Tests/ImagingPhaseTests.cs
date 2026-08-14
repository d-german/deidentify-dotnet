using CSharpFunctionalExtensions;
using Docnet.Core;
using Docnet.Core.Models;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using SkiaSharp;

namespace Deidentify.Imaging.Tests;

public sealed class ImagingPhaseTests
{
    [Fact]
    public void Imaging_package_remains_separate_from_core()
    {
        var references = typeof(SensitiveDataAnalyzer).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, assembly => assembly.Name == "Deidentify.Imaging");
    }

    [Fact]
    public async Task Image_redaction_maps_text_spans_to_irreversible_pixels()
    {
        using var bitmap = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        using var sourceImage = SKImage.FromBitmap(bitmap);
        using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var ocrText = "test.user@example.test";
        var span = TextSpan.Create(0, ocrText.Length, ocrText.Length).Value;
        var redactor = new ImageRedactor(new FakeOcrEngine(new OcrResult(
            ocrText,
            [new OcrWord(ocrText, 1, new PixelRectangle(10, 20, 200, 30), span)])));

        var result = await redactor.RedactAsync(sourceData.ToArray());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.RedactedRegions);
        using var redacted = SKBitmap.Decode(result.Value.PngImage);
        Assert.Equal(SKColors.Black, redacted.GetPixel(50, 30));
        Assert.Equal(SKColors.White, redacted.GetPixel(250, 30));
    }

    [Fact]
    public async Task Tesseract_extracts_words_and_boxes_from_a_synthetic_image_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var bitmap = new SKBitmap(900, 140);
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 52))
        using (var paint = new SKPaint
               {
                   Color = SKColors.Black,
                   IsAntialias = true
               })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("test.user@example.test", 20, 90, SKTextAlign.Left, font, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ocr = new TesseractOcrEngine();

        var result = await ocr.RecognizeAsync(data.ToArray());

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Value.Words);
        Assert.Contains("example", result.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Value.Words, word => Assert.True(word.Bounds.Width > 0));
    }

    [Fact]
    public async Task Pdf_redaction_rasterizes_and_rebuilds_pages()
    {
        using var source = new MemoryStream();
        using (var document = SKDocument.CreatePdf(source))
        {
            var canvas = document.BeginPage(300, 100);
            canvas.Clear(SKColors.White);
            document.EndPage();
            document.Close();
        }

        var span = TextSpan.Create(0, 22, 22).Value;
        var imageRedactor = new ImageRedactor(new FakeOcrEngine(new OcrResult(
            "test.user@example.test",
            [new OcrWord("test.user@example.test", 1, new PixelRectangle(10, 10, 180, 30), span)])));
        var result = await new PdfRedactor(imageRedactor).RedactAsync(source.ToArray());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PageCount);
        using var reader = DocLib.Instance.GetDocReader(result.Value.Document, new PageDimensions(1));
        Assert.Equal(1, reader.GetPageCount());
    }

    [Fact]
    public async Task Dicom_redaction_anonymizes_metadata_and_replaces_pixels()
    {
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            {
                DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage
            },
            {
                DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID()
            },
            {
                DicomTag.PatientName, "Synthetic^Patient"
            },
            {
                DicomTag.PatientID, "SYNTHETIC-42"
            },
            {
                DicomTag.Rows, (ushort)20
            },
            {
                DicomTag.Columns, (ushort)20
            },
            {
                DicomTag.SamplesPerPixel, (ushort)1
            },
            {
                DicomTag.PhotometricInterpretation, "MONOCHROME2"
            },
            {
                DicomTag.BitsAllocated, (ushort)8
            },
            {
                DicomTag.BitsStored, (ushort)8
            },
            {
                DicomTag.HighBit, (ushort)7
            },
            {
                DicomTag.PixelRepresentation, (ushort)0
            }
        };
        var pixels = DicomPixelData.Create(dataset, true);
        pixels.AddFrame(new MemoryByteBuffer([
            .. Enumerable.Repeat((byte)255, 400)
        ]));
        var file = new DicomFile(dataset);
        using var input = new MemoryStream();
        await file.SaveAsync(input);

        var span = TextSpan.Create(0, 22, 22).Value;
        var redactor = new DicomRedactor(new ImageRedactor(new FakeOcrEngine(new OcrResult(
            "test.user@example.test",
            [new OcrWord("test.user@example.test", 1, new PixelRectangle(2, 2, 8, 8), span)]))));
        var result = await redactor.RedactAsync(input.ToArray());

        Assert.True(result.IsSuccess);
        using var output = new MemoryStream(result.Value.Document);
        var redacted = await DicomFile.OpenAsync(output, FileReadOption.ReadAll);
        Assert.NotEqual("Synthetic^Patient", redacted.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty));
        Assert.Equal("RGB", redacted.Dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation));
        Assert.Equal("NO", redacted.Dataset.GetSingleValue<string>(DicomTag.BurnedInAnnotation));
    }

    [Fact]
    public async Task Imaging_boundaries_reject_invalid_input_and_limits()
    {
        Assert.True(ImageRedactionOptions.Create(maximumImageBytes: 0).IsFailure);
        var redactor = new ImageRedactor(new FakeOcrEngine(new OcrResult(string.Empty, [])));
        Assert.True((await redactor.RedactAsync(Array.Empty<byte>())).IsFailure);
        Assert.True((await redactor.RedactAsync(
            new byte[]
            {
                1, 2
            },
            imageOptions: ImageRedactionOptions.Create(maximumImageBytes: 1).Value)).IsFailure);
        Assert.Equal(
            DeidentifyErrorCode.UnsupportedFormat,
            (await redactor.RedactAsync(new byte[]
            {
                1, 2, 3
            })).Error.Code);
        Assert.Equal(
            DeidentifyErrorCode.OcrFailed,
            (await new ImageRedactor(new FailingOcrEngine()).RedactAsync(new byte[]
            {
                1
            })).Error.Code);

        Assert.True((await new PdfRedactor(redactor).RedactAsync(Array.Empty<byte>())).IsFailure);
        Assert.True((await new PdfRedactor(redactor).RedactAsync(new byte[]
        {
            1
        }, maximumPages: 0)).IsFailure);
        Assert.True((await new PdfRedactor(redactor).RedactAsync(new byte[]
        {
            1, 2, 3
        })).IsFailure);
        Assert.True((await new DicomRedactor(redactor).RedactAsync(Array.Empty<byte>())).IsFailure);
        Assert.True((await new DicomRedactor(redactor).RedactAsync(new byte[]
        {
            1
        }, maximumFrames: 0)).IsFailure);
        Assert.True((await new DicomRedactor(redactor).RedactAsync(new byte[]
        {
            1, 2, 3
        })).IsFailure);
    }

    [Fact]
    public async Task Ocr_adapter_reports_missing_data_and_disposal_safely()
    {
        using var missing = new TesseractOcrEngine(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Equal(
            DeidentifyErrorCode.ModelUnavailable,
            (await missing.RecognizeAsync(new byte[]
            {
                1
            })).Error.Code);

        var disposed = new TesseractOcrEngine();
        disposed.Dispose();
        Assert.Equal(
            DeidentifyErrorCode.OcrFailed,
            (await disposed.RecognizeAsync(new byte[]
            {
                1
            })).Error.Code);
    }

    [Fact]
    public async Task Pdf_and_dicom_page_limits_fail_before_redaction()
    {
        using var pdf = new MemoryStream();
        using (var document = SKDocument.CreatePdf(pdf))
        {
            for (var page = 0; page < 2; page++)
            {
                document.BeginPage(20, 20);
                document.EndPage();
            }

            document.Close();
        }

        var redactor = new ImageRedactor(new FakeOcrEngine(new OcrResult(string.Empty, [])));
        var pdfResult = await new PdfRedactor(redactor).RedactAsync(pdf.ToArray(), maximumPages: 1);

        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            {
                DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage
            },
            {
                DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID()
            },
            {
                DicomTag.Rows, (ushort)2
            },
            {
                DicomTag.Columns, (ushort)2
            },
            {
                DicomTag.SamplesPerPixel, (ushort)1
            },
            {
                DicomTag.PhotometricInterpretation, "MONOCHROME2"
            },
            {
                DicomTag.BitsAllocated, (ushort)8
            },
            {
                DicomTag.BitsStored, (ushort)8
            },
            {
                DicomTag.HighBit, (ushort)7
            },
            {
                DicomTag.PixelRepresentation, (ushort)0
            }
        };
        var pixels = DicomPixelData.Create(dataset, true);
        pixels.AddFrame(new MemoryByteBuffer([255, 255, 255, 255]));
        pixels.AddFrame(new MemoryByteBuffer([255, 255, 255, 255]));
        using var dicom = new MemoryStream();
        await new DicomFile(dataset).SaveAsync(dicom);
        var dicomResult = await new DicomRedactor(redactor).RedactAsync(dicom.ToArray(), maximumFrames: 1);

        Assert.Equal(DeidentifyErrorCode.InputTooLarge, pdfResult.Error.Code);
        Assert.Equal(DeidentifyErrorCode.InputTooLarge, dicomResult.Error.Code);
    }

    private sealed class FakeOcrEngine(OcrResult result) : IOcrEngine
    {
        public Task<Result<OcrResult, DeidentifyError>> RecognizeAsync(
            ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<OcrResult, DeidentifyError>(result));
    }

    private sealed class FailingOcrEngine : IOcrEngine
    {
        public Task<Result<OcrResult, DeidentifyError>> RecognizeAsync(
            ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<OcrResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.OcrFailed, "Safe failure.")));
    }
}
