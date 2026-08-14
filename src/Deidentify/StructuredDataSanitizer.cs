using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using Microsoft.VisualBasic.FileIO;

namespace Deidentify;

/// <summary>Sanitizes explicit structured-data edges through the text pipeline.</summary>
public sealed class StructuredDataSanitizer
{
    private const string SanitizeJsonOperation = "SanitizeJson";
    private readonly SensitiveDataSanitizer _sanitizer;

    /// <summary>Creates a structured-data sanitizer.</summary>
    public StructuredDataSanitizer(SensitiveDataSanitizer? sanitizer = null) =>
        _sanitizer = sanitizer ?? new SensitiveDataSanitizer();

    /// <summary>Sanitizes a batch without retaining matched values.</summary>
    public Result<ImmutableArray<AnonymizationResult>, DeidentifyError> SanitizeBatch(
        IEnumerable<string> values,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Select(value => _sanitizer.Sanitize(value, analysisOptions, anonymizationOptions))
            .Combine()
            .Map(results => results.ToImmutableArray())
            .MapError(error => error.WithOperation("SanitizeBatch"));
    }

    /// <summary>Sanitizes string values in a dictionary.</summary>
    public Result<ImmutableDictionary<string, string?>, DeidentifyError> SanitizeDictionary(
        IReadOnlyDictionary<string, string?> values,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        var sanitized = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (pair.Value is null)
            {
                sanitized[pair.Key] = null;
                continue;
            }

            var result = _sanitizer.Sanitize(pair.Value, analysisOptions, anonymizationOptions);
            if (result.IsFailure)
            {
                return Result.Failure<ImmutableDictionary<string, string?>, DeidentifyError>(
                    result.Error.WithOperation("SanitizeDictionary"));
            }

            sanitized[pair.Key] = result.Value.Text;
        }

        return Result.Success<ImmutableDictionary<string, string?>, DeidentifyError>(
            sanitized.ToImmutable());
    }

    /// <summary>Sanitizes selected public string properties into an immutable projection.</summary>
    public Result<ImmutableDictionary<string, object?>, DeidentifyError> SanitizeProperties(
        object value,
        IEnumerable<string> propertyNames,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyNames);

        var selected = propertyNames.ToImmutableHashSet(StringComparer.Ordinal);
        var output = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.GetType().GetProperties()
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Result.Failure<ImmutableDictionary<string, object?>, DeidentifyError>(
                    new DeidentifyError(
                        DeidentifyErrorCode.InvalidInput,
                        "A selected object property could not be read safely.",
                        "SanitizeProperties"));
            }

            if (selected.Contains(property.Name) && propertyValue is string text)
            {
                var result = _sanitizer.Sanitize(text, analysisOptions, anonymizationOptions);
                if (result.IsFailure)
                {
                    return Result.Failure<ImmutableDictionary<string, object?>, DeidentifyError>(
                        result.Error.WithOperation("SanitizeProperties"));
                }

                propertyValue = result.Value.Text;
            }

            output[property.Name] = propertyValue;
        }

        return Result.Success<ImmutableDictionary<string, object?>, DeidentifyError>(
            output.ToImmutable());
    }

    /// <summary>Sanitizes every JSON string value while preserving structure and scalar types.</summary>
    public Result<string, DeidentifyError> SanitizeJson(
        string? json,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null)
    {
        if (json is null)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "JSON is required.", SanitizeJsonOperation));
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return Result.Failure<string, DeidentifyError>(
                    new DeidentifyError(DeidentifyErrorCode.InvalidInput, "JSON must contain a value.", SanitizeJsonOperation));
            }

            var result = SanitizeNode(node, analysisOptions, anonymizationOptions);
            return result.Map(value => value.ToJsonString()).MapError(error => error.WithOperation(SanitizeJsonOperation));
        }
        catch (JsonException)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "JSON is malformed.", SanitizeJsonOperation));
        }
    }

    /// <summary>Sanitizes RFC 4180-style CSV fields with bounded record processing.</summary>
    public async Task<Result<StreamingSanitizationResult, DeidentifyError>> SanitizeCsvAsync(
        Stream input,
        Stream output,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var counts = new Dictionary<EntityType, long>();
        long records = 0;
        try
        {
            using var parser = new TextFieldParser(input, Encoding.UTF8, true)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(",");

            await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = parser.ReadFields();
                if (fields is null)
                {
                    break;
                }

                for (var index = 0; index < fields.Length; index++)
                {
                    var result = _sanitizer.Sanitize(fields[index], analysisOptions, anonymizationOptions);
                    if (result.IsFailure)
                    {
                        return Result.Failure<StreamingSanitizationResult, DeidentifyError>(
                            result.Error.WithOperation("SanitizeCsv"));
                    }

                    fields[index] = result.Value.Text;
                    foreach (var group in result.Value.Detections.GroupBy(detection => detection.EntityType))
                    {
                        counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.LongCount();
                    }
                }

                await writer.WriteLineAsync(string.Join(',', fields.Select(EscapeCsv)));
                records++;
            }

            await writer.FlushAsync(cancellationToken);
            return Result.Success<StreamingSanitizationResult, DeidentifyError>(
                new StreamingSanitizationResult(records, counts.ToImmutableDictionary()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MalformedLineException)
        {
            return Result.Failure<StreamingSanitizationResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "CSV is malformed.", "SanitizeCsv"));
        }
        catch (IOException)
        {
            return Result.Failure<StreamingSanitizationResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.FileWriteFailed, "CSV processing failed safely.", "SanitizeCsv"));
        }
    }

    private Result<JsonNode, DeidentifyError> SanitizeNode(
        JsonNode node,
        AnalysisOptions? analysisOptions,
        AnonymizationOptions? anonymizationOptions)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                return SanitizeObject(jsonObject, analysisOptions, anonymizationOptions);

            case JsonArray jsonArray:
                return SanitizeArray(jsonArray, analysisOptions, anonymizationOptions);

            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                return _sanitizer.Sanitize(text, analysisOptions, anonymizationOptions)
                    .Map(result => (JsonNode)JsonValue.Create(result.Text));

            default:
                return Result.Success<JsonNode, DeidentifyError>(node);
        }
    }

    private Result<JsonNode, DeidentifyError> SanitizeObject(
        JsonObject jsonObject,
        AnalysisOptions? analysisOptions,
        AnonymizationOptions? anonymizationOptions)
    {
        foreach (var property in jsonObject.ToArray())
        {
            if (property.Value is null)
            {
                continue;
            }

            var result = SanitizeNode(property.Value, analysisOptions, anonymizationOptions);
            if (result.IsFailure)
            {
                return result;
            }

            jsonObject[property.Key] = result.Value;
        }

        return Result.Success<JsonNode, DeidentifyError>(jsonObject);
    }

    private Result<JsonNode, DeidentifyError> SanitizeArray(
        JsonArray jsonArray,
        AnalysisOptions? analysisOptions,
        AnonymizationOptions? anonymizationOptions)
    {
        for (var index = 0; index < jsonArray.Count; index++)
        {
            if (jsonArray[index] is not { } child)
            {
                continue;
            }

            var result = SanitizeNode(child, analysisOptions, anonymizationOptions);
            if (result.IsFailure)
            {
                return result;
            }

            jsonArray[index] = result.Value;
        }

        return Result.Success<JsonNode, DeidentifyError>(jsonArray);
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
