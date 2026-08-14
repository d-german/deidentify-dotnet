using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Deidentify;

/// <summary>Sanitizes line-oriented text with bounded memory.</summary>
public sealed class StreamingSanitizer
{
    private readonly SensitiveDataSanitizer _sanitizer;

    /// <summary>Creates a line-oriented sanitizer.</summary>
    public StreamingSanitizer(SensitiveDataSanitizer? sanitizer = null) =>
        _sanitizer = sanitizer ?? new SensitiveDataSanitizer();

    /// <summary>Sanitizes one line at a time and returns only aggregate counts.</summary>
    public async Task<Result<StreamingSanitizationResult, DeidentifyError>> SanitizeAsync(
        TextReader reader,
        TextWriter writer,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var counts = new Dictionary<EntityType, long>();
        long lines = 0;
        var reading = true;

        try
        {
            while (true)
            {
                reading = true;
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                var result = _sanitizer.Sanitize(line, analysisOptions, anonymizationOptions);
                if (result.IsFailure)
                {
                    return Result.Failure<StreamingSanitizationResult, DeidentifyError>(result.Error);
                }

                cancellationToken.ThrowIfCancellationRequested();
                reading = false;
                await writer.WriteLineAsync(result.Value.Text.AsMemory(), cancellationToken);
                foreach (var group in result.Value.Detections.GroupBy(detection => detection.EntityType))
                {
                    counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.LongCount();
                }

                lines++;
            }

            reading = false;
            await writer.FlushAsync(cancellationToken);
            return Result.Success<StreamingSanitizationResult, DeidentifyError>(
                new StreamingSanitizationResult(lines, counts.ToImmutableDictionary()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return Result.Failure<StreamingSanitizationResult, DeidentifyError>(
                new DeidentifyError(
                    reading ? DeidentifyErrorCode.FileReadFailed : DeidentifyErrorCode.FileWriteFailed,
                    "The streaming operation failed safely.",
                    "Stream"));
        }
    }
}
