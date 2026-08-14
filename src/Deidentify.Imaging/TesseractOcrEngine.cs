using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using CSharpFunctionalExtensions;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace Deidentify.Imaging;

/// <summary>Windows-local Tesseract OCR adapter with bounded engine reuse.</summary>
public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly string _dataPath;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentBag<Engine> _engines = [];
    private bool _disposed;

    /// <summary>Creates an English OCR adapter using bundled tessdata by default.</summary>
    public TesseractOcrEngine(string? dataPath = null, int maximumConcurrency = 1)
    {
        if (maximumConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                "OCR concurrency must be positive.");
        }

        _dataPath = Path.GetFullPath(dataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata"));
        _concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    /// <inheritdoc />
    public async Task<Result<OcrResult, DeidentifyError>> RecognizeAsync(
        ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return Result.Failure<OcrResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.OcrFailed, "The OCR engine is unavailable."));
        }

        if (!Directory.Exists(_dataPath))
        {
            return Result.Failure<OcrResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ModelUnavailable, "OCR language data is unavailable."));
        }

        await _concurrency.WaitAsync(cancellationToken);
        Engine? engine = null;
        try
        {
            engine = _engines.TryTake(out var cached)
                ? cached
                : new Engine(_dataPath, Language.English, EngineMode.LstmOnly);
            using var pix = Image.LoadFromMemory(image.ToArray());
            using var page = engine.Process(pix, PageSegMode.Auto);
            return Result.Success<OcrResult, DeidentifyError>(MapPage(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            engine?.Dispose();
            engine = null;
            return Result.Failure<OcrResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.OcrFailed, "OCR failed safely."));
        }
        finally
        {
            if (engine is not null)
            {
                _engines.Add(engine);
            }

            _concurrency.Release();
        }
    }

    /// <summary>Disposes pooled native OCR engines.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_engines.TryTake(out var engine))
        {
            engine.Dispose();
        }

        _concurrency.Dispose();
    }

    private static OcrResult MapPage(Page page)
    {
        var text = new StringBuilder();
        var words = ImmutableArray.CreateBuilder<OcrWord>();
        foreach (var word in page.Layout
                     .SelectMany(block => block.Paragraphs)
                     .SelectMany(paragraph => paragraph.TextLines)
                     .SelectMany(line => line.Words))
        {
            AddWord(word, text, words);
        }

        return new OcrResult(text.ToString(), words.ToImmutable());
    }

    private static void AddWord(
        TesseractOCR.Layout.Word word,
        StringBuilder text,
        ImmutableArray<OcrWord>.Builder words)
    {
        var value = word.Text?.Trim();
        if (string.IsNullOrEmpty(value) || word.BoundingBox is not { } bounds)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(' ');
        }

        var start = text.Length;
        text.Append(value);
        var span = TextSpan.Create(start, value.Length, text.Length);
        if (span.IsSuccess)
        {
            words.Add(new OcrWord(
                value,
                Math.Clamp(word.Confidence, 0, 1),
                new PixelRectangle(bounds.X1, bounds.Y1, bounds.Width, bounds.Height),
                span.Value));
        }
    }
}
