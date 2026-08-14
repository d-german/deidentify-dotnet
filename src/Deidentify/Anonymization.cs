using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using CSharpFunctionalExtensions;

namespace Deidentify;

/// <summary>Transforms one detected value.</summary>
public interface IAnonymizationOperator
{
    /// <summary>Stable operator identifier.</summary>
    string Id { get; }
    /// <summary>Transforms one detected value.</summary>
    Result<string, DeidentifyError> Apply(string value, Detection detection);
}

/// <summary>Replaces a value with its entity marker.</summary>
public sealed class EntityMarkerOperator : IAnonymizationOperator
{
    /// <summary>The stateless shared instance.</summary>
    public static EntityMarkerOperator Instance { get; } = new();
    private EntityMarkerOperator() { }
    /// <inheritdoc />
    public string Id => "replace";
    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection) =>
        Result.Success<string, DeidentifyError>($"<{detection.EntityType.Value}>");
}

/// <summary>Removes a detected value.</summary>
public sealed class RedactOperator : IAnonymizationOperator
{
    /// <summary>The stateless shared instance.</summary>
    public static RedactOperator Instance { get; } = new();
    private RedactOperator() { }
    /// <inheritdoc />
    public string Id => "redact";
    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection) =>
        Result.Success<string, DeidentifyError>(string.Empty);
}

/// <summary>Replaces a value with caller-provided text.</summary>
public sealed class FixedReplacementOperator : IAnonymizationOperator
{
    private readonly string _replacement;
    private FixedReplacementOperator(string replacement) => _replacement = replacement;
    /// <inheritdoc />
    public string Id => "fixed";

    /// <summary>Creates a fixed replacement operator.</summary>
    public static Result<FixedReplacementOperator, DeidentifyError> Create(string? replacement) =>
        replacement is null
            ? Result.Failure<FixedReplacementOperator, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Replacement text is required."))
            : Result.Success<FixedReplacementOperator, DeidentifyError>(new FixedReplacementOperator(replacement));

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection) =>
        Result.Success<string, DeidentifyError>(_replacement);
}

/// <summary>Masks a configurable number of characters.</summary>
public sealed class MaskOperator : IAnonymizationOperator
{
    private readonly char _maskingCharacter;
    private readonly int? _charactersToMask;
    private readonly bool _fromEnd;

    private MaskOperator(char maskingCharacter, int? charactersToMask, bool fromEnd)
    {
        _maskingCharacter = maskingCharacter;
        _charactersToMask = charactersToMask;
        _fromEnd = fromEnd;
    }

    /// <inheritdoc />
    public string Id => "mask";

    /// <summary>Creates a masking operator.</summary>
    public static Result<MaskOperator, DeidentifyError> Create(
        char maskingCharacter = '*',
        int? charactersToMask = null,
        bool fromEnd = false) =>
        charactersToMask < 0
            ? Result.Failure<MaskOperator, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Characters to mask cannot be negative."))
            : Result.Success<MaskOperator, DeidentifyError>(
                new MaskOperator(maskingCharacter, charactersToMask, fromEnd));

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection)
    {
        var count = Math.Min(value.Length, _charactersToMask ?? value.Length);
        var characters = value.ToCharArray();
        var start = _fromEnd ? characters.Length - count : 0;
        Array.Fill(characters, _maskingCharacter, start, count);
        return Result.Success<string, DeidentifyError>(new string(characters));
    }
}

/// <summary>Produces deterministic HMAC-SHA256 pseudonyms using a caller-managed key.</summary>
public sealed class HmacSha256Operator : IAnonymizationOperator, IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    private HmacSha256Operator(byte[] key) => _key = key;
    /// <inheritdoc />
    public string Id => "hmac-sha256";

    /// <summary>Creates an operator with a caller-managed key of at least 128 bits.</summary>
    public static Result<HmacSha256Operator, DeidentifyError> Create(ReadOnlySpan<byte> key) =>
        key.Length < 16
            ? Result.Failure<HmacSha256Operator, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "The HMAC key must contain at least 16 bytes."))
            : Result.Success<HmacSha256Operator, DeidentifyError>(new HmacSha256Operator([
                .. key
            ]));

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection)
    {
        if (_disposed)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "The pseudonymization operator is unavailable."));
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Result.Success<string, DeidentifyError>(
                Convert.ToHexString(HMACSHA256.HashData(_key, bytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Clears the private key buffer.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}

/// <summary>Preserves a selected value unchanged.</summary>
public sealed class KeepOperator : IAnonymizationOperator
{
    /// <summary>The stateless shared instance.</summary>
    public static KeepOperator Instance { get; } = new();

    private KeepOperator()
    {
    }

    /// <inheritdoc />
    public string Id => "keep";

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection) =>
        Result.Success<string, DeidentifyError>(value);
}

/// <summary>Runs a caller-provided in-process transformation.</summary>
public sealed class CustomOperator : IAnonymizationOperator
{
    private readonly Func<string, Detection, string> _operation;

    /// <summary>Creates a custom operator.</summary>
    public CustomOperator(string id, Func<string, Detection, string> operation)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Operator identifier is required.", nameof(id))
            : id;
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection)
    {
        try
        {
            return Result.Success<string, DeidentifyError>(_operation(value, detection));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "The custom anonymization operator failed safely."));
        }
    }
}

/// <summary>Encrypts values using caller-keyed AES-GCM authenticated encryption.</summary>
public sealed class AesGcmEncryptionOperator : IAnonymizationOperator, IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    private AesGcmEncryptionOperator(byte[] key) => _key = key;

    /// <inheritdoc />
    public string Id => "encrypt";

    /// <summary>Creates an AES-GCM operator with a 128, 192, or 256-bit key.</summary>
    public static Result<AesGcmEncryptionOperator, DeidentifyError> Create(ReadOnlySpan<byte> key) =>
        key.Length is 16 or 24 or 32
            ? Result.Success<AesGcmEncryptionOperator, DeidentifyError>(new AesGcmEncryptionOperator([
                .. key
            ]))
            : Result.Failure<AesGcmEncryptionOperator, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "AES-GCM keys must contain 16, 24, or 32 bytes."));

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection)
    {
        if (_disposed)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "The encryption operator is unavailable."));
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(_key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var payload = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            payload[0] = 1;
            nonce.CopyTo(payload, 1);
            tag.CopyTo(payload, 1 + nonce.Length);
            ciphertext.CopyTo(payload, 1 + nonce.Length + tag.Length);
            return Result.Success<string, DeidentifyError>(Convert.ToBase64String(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <summary>Clears the copied key.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}

/// <summary>Decrypts values emitted by <see cref="AesGcmEncryptionOperator"/>.</summary>
public sealed class AesGcmDecryptionOperator : IAnonymizationOperator, IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    private AesGcmDecryptionOperator(byte[] key) => _key = key;

    /// <inheritdoc />
    public string Id => "decrypt";

    /// <summary>Creates an AES-GCM decryption operator.</summary>
    public static Result<AesGcmDecryptionOperator, DeidentifyError> Create(ReadOnlySpan<byte> key) =>
        key.Length is 16 or 24 or 32
            ? Result.Success<AesGcmDecryptionOperator, DeidentifyError>(new AesGcmDecryptionOperator([
                .. key
            ]))
            : Result.Failure<AesGcmDecryptionOperator, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "AES-GCM keys must contain 16, 24, or 32 bytes."));

    /// <inheritdoc />
    public Result<string, DeidentifyError> Apply(string value, Detection detection)
    {
        if (_disposed)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "The decryption operator is unavailable."));
        }

        try
        {
            var payload = Convert.FromBase64String(value);
            if (payload.Length < 29 || payload[0] != 1)
            {
                return Result.Failure<string, DeidentifyError>(
                    new DeidentifyError(DeidentifyErrorCode.InvalidInput, "The encrypted value format is invalid."));
            }

            var nonce = payload.AsSpan(1, 12);
            var tag = payload.AsSpan(13, 16);
            var ciphertext = payload.AsSpan(29);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(_key, tag.Length);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return Result.Success<string, DeidentifyError>(Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return Result.Failure<string, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.AnonymizationFailed, "The encrypted value could not be authenticated."));
        }
    }

    /// <summary>Clears the copied key.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}

/// <summary>Immutable operator selection.</summary>
public sealed record AnonymizationOptions
{
    /// <summary>Creates immutable per-entity operator selection.</summary>
    public AnonymizationOptions(
        IAnonymizationOperator? defaultOperator = null,
        IReadOnlyDictionary<EntityType, IAnonymizationOperator>? operators = null)
    {
        DefaultOperator = defaultOperator ?? EntityMarkerOperator.Instance;
        Operators = operators?.ToImmutableDictionary()
                    ?? ImmutableDictionary<EntityType, IAnonymizationOperator>.Empty;
    }

    /// <summary>The operator used when an entity has no override.</summary>
    public IAnonymizationOperator DefaultOperator { get; }
    /// <summary>Per-entity operator overrides.</summary>
    public ImmutableDictionary<EntityType, IAnonymizationOperator> Operators { get; }

    internal IAnonymizationOperator GetOperator(EntityType entityType) =>
        Operators.GetValueOrDefault(entityType, DefaultOperator);
}

/// <summary>Applies operators from the end of the input toward the beginning.</summary>
public static class SensitiveDataAnonymizer
{
    /// <summary>Anonymizes validated detections without changing their source indexes.</summary>
    public static Result<AnonymizationResult, DeidentifyError> Anonymize(
        string? text,
        AnalysisResult analysis,
        AnonymizationOptions? options = null)
    {
        if (text is null)
        {
            return Result.Failure<AnonymizationResult, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Text is required."));
        }

        options ??= new AnonymizationOptions();
        return analysis.Detections
            .Select(detection => TextSpan.Create(
                detection.Span.Start,
                detection.Span.Length,
                text.Length))
            .Combine()
            .Bind(_ => ApplyOperators(text, analysis.Detections, options))
            .Map(result => new AnonymizationResult(result, analysis.Detections))
            .MapError(error => error.WithOperation("Anonymize"));
    }

    private static Result<string, DeidentifyError> ApplyOperators(
        string text,
        ImmutableArray<Detection> detections,
        AnonymizationOptions options)
    {
        var output = text;
        foreach (var detection in detections.OrderByDescending(item => item.Span.Start))
        {
            var value = output.Substring(detection.Span.Start, detection.Span.Length);
            var replacement = options.GetOperator(detection.EntityType).Apply(value, detection);
            if (replacement.IsFailure)
            {
                return Result.Failure<string, DeidentifyError>(replacement.Error);
            }

            output = string.Concat(
                output.AsSpan(0, detection.Span.Start),
                replacement.Value,
                output.AsSpan(detection.Span.End));
        }

        return Result.Success<string, DeidentifyError>(output);
    }
}

/// <summary>Convenience analysis and anonymization entry point.</summary>
public sealed class SensitiveDataSanitizer
{
    private readonly SensitiveDataAnalyzer _analyzer;

    /// <summary>Creates the combined analyzer and anonymizer facade.</summary>
    public SensitiveDataSanitizer(SensitiveDataAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new SensitiveDataAnalyzer();
    }

    /// <summary>Analyzes and anonymizes text in one fail-closed pipeline.</summary>
    public Result<AnonymizationResult, DeidentifyError> Sanitize(
        string? text,
        AnalysisOptions? analysisOptions = null,
        AnonymizationOptions? anonymizationOptions = null) =>
        _analyzer.Analyze(text, analysisOptions)
            .Bind(result => SensitiveDataAnonymizer.Anonymize(text, result, anonymizationOptions))
            .MapError(error => error.WithOperation("Sanitize"));
}
