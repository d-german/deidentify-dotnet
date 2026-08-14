using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Deidentify;

/// <summary>Runs local recognizers and deterministic overlap resolution.</summary>
public sealed class SensitiveDataAnalyzer
{
    private readonly ImmutableArray<IEntityRecognizer> _recognizers;

    /// <summary>Creates an analyzer with built-in and optional custom recognizers.</summary>
    public SensitiveDataAnalyzer(IEnumerable<IEntityRecognizer>? customRecognizers = null)
    {
        _recognizers = customRecognizers is null
            ? BuiltInRecognizers.Create()
            : BuiltInRecognizers.Create().AddRange(customRecognizers);
    }

    /// <summary>Analyzes text locally and returns non-overlapping detections.</summary>
    public Result<AnalysisResult, DeidentifyError> Analyze(
        string? text,
        AnalysisOptions? options = null) =>
        AnalysisRequest.Create(text, options)
            .Bind(AnalyzeRecognizers)
            .Map(ResolveOverlaps)
            .Map(detections => new AnalysisResult(detections))
            .MapError(error => error.WithOperation("Analyze"));

    private Result<ImmutableArray<Detection>, DeidentifyError> AnalyzeRecognizers(AnalysisRequest request)
    {
        var context = new RecognitionContext(request.Options);
        return _recognizers
            .Where(recognizer => !request.Options.DisabledRecognizers.Contains(recognizer.Id))
            .Select(recognizer => RunRecognizer(recognizer, request.Text, context))
            .Combine()
            .Map(results => results
                .SelectMany(result => result)
                .Where(detection => detection.Confidence.Value >= request.Options.MinimumConfidence.Value)
                .Where(detection => !IsAllowed(request.Text, detection, request.Options.AllowList))
                .ToImmutableArray());
    }

    private static Result<ImmutableArray<Detection>, DeidentifyError> RunRecognizer(
        IEntityRecognizer recognizer,
        string text,
        RecognitionContext context)
    {
        try
        {
            return recognizer.Analyze(text, context);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<ImmutableArray<Detection>, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.RecognitionFailed, "A recognizer failed safely."));
        }
    }

    private static bool IsAllowed(
        string text,
        Detection detection,
        ImmutableHashSet<string> allowList) =>
        allowList.Contains(text.Substring(detection.Span.Start, detection.Span.Length));

    private static ImmutableArray<Detection> ResolveOverlaps(ImmutableArray<Detection> detections)
    {
        var selected = new List<Detection>();
        var ordered = detections
            .Select((value, order) => (value, order))
            .OrderByDescending(item => item.value.Confidence.Value)
            .ThenByDescending(item => item.value.Span.Length)
            .ThenByDescending(item => item.value.RecognizerPriority)
            .ThenBy(item => item.order)
            .Select(item => item.value);
        foreach (var detection in ordered.Where(
                     detection => selected.All(existing => !existing.Span.Overlaps(detection.Span))))
        {
            selected.Add(detection);
        }

        return
        [
            .. selected.OrderBy(detection => detection.Span.Start)
        ];
    }
}
