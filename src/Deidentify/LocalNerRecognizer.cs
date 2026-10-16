using System.Collections.Concurrent;
using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Deidentify;

/// <summary>Offline English NER backed by the bundled quantized BERT model.</summary>
public sealed class LocalNerRecognizer : IEntityRecognizer
{
    private const int LabelCount = 9;
    private const int MaximumContentTokens = 510;
    private const int MaximumChunkCharacters = 2_000;
    private const string ContinuationPrefix = "##";
    private static readonly ConcurrentDictionary<string, Lazy<ModelState>> Models =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ModelState _model;
    private readonly double _minimumConfidence;

    private LocalNerRecognizer(ModelState model, double minimumConfidence)
    {
        _model = model;
        _minimumConfidence = minimumConfidence;
    }

    /// <inheritdoc />
    public string Id => "bert-base-ner";

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public ImmutableArray<EntityType> SupportedEntities { get; } =
        [EntityType.Person, EntityType.Location, EntityType.Organization];

    /// <summary>Creates a shared local recognizer from bundled or caller-supplied model assets.</summary>
    public static Result<LocalNerRecognizer, DeidentifyError> Create(
        string? modelDirectory = null,
        double minimumConfidence = 0.7)
    {
        if (minimumConfidence is < 0 or > 1 || double.IsNaN(minimumConfidence))
        {
            return Result.Failure<LocalNerRecognizer, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "NER confidence must be between zero and one."));
        }

        var directory = Path.GetFullPath(
            modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "Models", "bert-base-ner"));
        var modelPath = Path.Combine(directory, "model.onnx");
        var vocabularyPath = Path.Combine(directory, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabularyPath))
        {
            return Result.Failure<LocalNerRecognizer, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ModelUnavailable, "The local NER model assets are unavailable."));
        }

        try
        {
            var model = Models.GetOrAdd(
                directory,
                _ => new Lazy<ModelState>(
                    () => ModelState.Load(modelPath, vocabularyPath),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            return Result.Success<LocalNerRecognizer, DeidentifyError>(
                new LocalNerRecognizer(model, minimumConfidence));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Models.TryRemove(directory, out _);
            return Result.Failure<LocalNerRecognizer, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ModelLoadFailed, "The local NER model could not be loaded safely."));
        }
    }

    /// <inheritdoc />
    public Result<ImmutableArray<Detection>, DeidentifyError> Analyze(
        string text,
        RecognitionContext context)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            (context.Options.EntityTypes.Count > 0 &&
             !SupportedEntities.Any(context.Options.EntityTypes.Contains)))
        {
            return Result.Success<ImmutableArray<Detection>, DeidentifyError>([]);
        }

        try
        {
            var detections = ImmutableArray.CreateBuilder<Detection>();
            var offset = 0;
            while (offset < text.Length)
            {
                var chunkLength = Math.Min(MaximumChunkCharacters, text.Length - offset);
                if (offset + chunkLength < text.Length)
                {
                    var split = text.LastIndexOfAny([' ', '\r', '\n', '\t'], offset + chunkLength - 1, chunkLength);
                    if (split >= offset)
                    {
                        chunkLength = split - offset + 1;
                    }
                }

                AnalyzeChunk(text.AsSpan(offset, chunkLength).ToString(), offset, text.Length, context, detections);
                offset += chunkLength;
            }

            return Result.Success<ImmutableArray<Detection>, DeidentifyError>(detections.ToImmutable());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<ImmutableArray<Detection>, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.RecognitionFailed, "Local NER inference failed safely."));
        }
    }

    private void AnalyzeChunk(
        string chunk,
        int sourceOffset,
        int sourceLength,
        RecognitionContext context,
        ImmutableArray<Detection>.Builder detections)
    {
        var tokens = _model.Tokenizer.EncodeToTokens(chunk, out _)
            .Take(MaximumContentTokens)
            .ToArray();
        if (tokens.Length == 0)
        {
            return;
        }

        var inputIds = new long[tokens.Length + 2];
        inputIds[0] = _model.Tokenizer.ClassificationTokenId;
        for (var index = 0; index < tokens.Length; index++)
        {
            inputIds[index + 1] = tokens[index].Id;
        }

        inputIds[^1] = _model.Tokenizer.SeparatorTokenId;
        var attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();
        var tokenTypeIds = new long[inputIds.Length];
        var dimensions = new[]
        {
            1, inputIds.Length
        };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dimensions)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dimensions)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, dimensions))
        };

        using var output = _model.Session.Run(inputs);
        var logits = output[0].AsEnumerable<float>().ToArray();
        EntityAccumulator? current = null;

        foreach (var word in LabelWords(tokens, chunk.Length, logits))
        {
            var (prefix, entityType) = MapLabel(word.Label);
            if (entityType is null ||
                word.Confidence < _minimumConfidence ||
                (context.Options.EntityTypes.Count > 0 && !context.Options.EntityTypes.Contains(entityType)))
            {
                Flush(current, sourceOffset, sourceLength, detections);
                current = null;
                continue;
            }

            if (prefix == 'B' || current is null || current.EntityType != entityType)
            {
                Flush(current, sourceOffset, sourceLength, detections);
                current = new EntityAccumulator(entityType, word.Start, word.End, word.Confidence, 1);
            }
            else
            {
                current = current with
                {
                    End = word.End,
                    ConfidenceTotal = current.ConfidenceTotal + word.Confidence,
                    TokenCount = current.TokenCount + 1
                };
            }
        }

        Flush(current, sourceOffset, sourceLength, detections);
    }

    /// <summary>
    /// Groups WordPiece tokens back into the words they came from and labels each word by its first
    /// piece, the usual "first" aggregation strategy. Continuation pieces such as <c>##OE</c> in
    /// <c>DOE</c> are often labelled outside any entity; labelling them separately ended the entity
    /// mid-word, which replaced <c>D</c> and left the rest of the name in the output.
    /// </summary>
    private static IEnumerable<WordLabel> LabelWords(EncodedToken[] tokens, int chunkLength, float[] logits)
    {
        WordLabel? word = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var end = token.Offset.End.GetOffset(chunkLength);
            if (word is not null && token.Value.StartsWith(ContinuationPrefix, StringComparison.Ordinal))
            {
                word = word with { End = end };
                continue;
            }

            if (word is not null)
            {
                yield return word;
            }

            var label = GetLabel(logits, index + 1, out var confidence);
            word = new WordLabel(token.Offset.Start.GetOffset(chunkLength), end, label, confidence);
        }

        if (word is not null)
        {
            yield return word;
        }
    }

    private static int GetLabel(
        float[] logits,
        int tokenIndex,
        out double confidence)
    {
        var start = tokenIndex * LabelCount;
        var bestLabel = 0;
        var bestLogit = logits[start];
        var maxLogit = bestLogit;
        for (var label = 1; label < LabelCount; label++)
        {
            var logit = logits[start + label];
            if (logit > bestLogit)
            {
                bestLogit = logit;
                bestLabel = label;
            }

            maxLogit = Math.Max(maxLogit, logit);
        }

        var denominator = 0d;
        for (var label = 0; label < LabelCount; label++)
        {
            denominator += Math.Exp(logits[start + label] - maxLogit);
        }

        confidence = Math.Exp(bestLogit - maxLogit) / denominator;
        return bestLabel;
    }

    private static (char Prefix, EntityType? EntityType) MapLabel(int label) => label switch
    {
        3 => ('B', EntityType.Person),
        4 => ('I', EntityType.Person),
        5 => ('B', EntityType.Organization),
        6 => ('I', EntityType.Organization),
        7 => ('B', EntityType.Location),
        8 => ('I', EntityType.Location),
        _ => ('O', null)
    };

    private static void Flush(
        EntityAccumulator? entity,
        int sourceOffset,
        int sourceLength,
        ImmutableArray<Detection>.Builder detections)
    {
        if (entity is null || entity.End <= entity.Start)
        {
            return;
        }

        var detection = Detection.Create(
            entity.EntityType,
            sourceOffset + entity.Start,
            entity.End - entity.Start,
            sourceLength,
            entity.ConfidenceTotal / entity.TokenCount,
            new DetectionSource("bert-base-ner", 10, "Local ONNX named-entity recognition."));
        if (detection.IsSuccess)
        {
            detections.Add(detection.Value);
        }
    }

    private sealed record ModelState(InferenceSession Session, BertTokenizer Tokenizer)
    {
        public static ModelState Load(string modelPath, string vocabularyPath)
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Environment.ProcessorCount,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            return new ModelState(
                new InferenceSession(modelPath, options),
                BertTokenizer.Create(
                    vocabularyPath,
                    new BertOptions
                    {
                        LowerCaseBeforeTokenization = false,
                        ApplyBasicTokenization = true
                    }));
        }
    }

    private sealed record EntityAccumulator(
        EntityType EntityType,
        int Start,
        int End,
        double ConfidenceTotal,
        int TokenCount);

    private sealed record WordLabel(int Start, int End, int Label, double Confidence);
}
