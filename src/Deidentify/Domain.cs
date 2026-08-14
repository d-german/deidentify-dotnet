using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Deidentify;

/// <summary>Stable categories for safe failures.</summary>
public enum DeidentifyErrorCode
{
    /// <summary>Input was missing or invalid.</summary>
    InvalidInput,
    /// <summary>Input exceeded a configured bound.</summary>
    InputTooLarge,
    /// <summary>Recognition failed without exposing input.</summary>
    RecognitionFailed,
    /// <summary>A required local model is unavailable.</summary>
    ModelUnavailable,
    /// <summary>A required local model could not be loaded.</summary>
    ModelLoadFailed,
    /// <summary>Optical character recognition failed.</summary>
    OcrFailed,
    /// <summary>A text span is outside its source.</summary>
    InvalidSpan,
    /// <summary>An anonymization operator failed.</summary>
    AnonymizationFailed,
    /// <summary>An input file could not be read.</summary>
    FileReadFailed,
    /// <summary>An output file could not be written.</summary>
    FileWriteFailed,
    /// <summary>The supplied format is unsupported.</summary>
    UnsupportedFormat,
    /// <summary>Configuration is invalid.</summary>
    ConfigurationInvalid,
    /// <summary>An operation was cancelled.</summary>
    Cancelled
}

/// <summary>A failure containing no sensitive input or exception text.</summary>
public sealed record DeidentifyError(
    DeidentifyErrorCode Code,
    string SafeMessage,
    string? Operation = null) : ICombine
{
    /// <summary>Adds safe operation context.</summary>
    public DeidentifyError WithOperation(string operation) => this with
    {
        Operation = operation
    };

    ICombine ICombine.Combine(ICombine value) => this;
}

/// <summary>A validated entity category.</summary>
public sealed record EntityType
{
    private EntityType(string value) => Value = value;

    /// <summary>The stable upper-case entity name.</summary>
    public string Value { get; }

    /// <summary>Email address.</summary>
    public static EntityType EmailAddress { get; } = new("EMAIL_ADDRESS");
    /// <summary>Telephone number.</summary>
    public static EntityType PhoneNumber { get; } = new("PHONE_NUMBER");
    /// <summary>United States Social Security number.</summary>
    public static EntityType SocialSecurityNumber { get; } = new("US_SSN");
    /// <summary>United States Individual Taxpayer Identification Number.</summary>
    public static EntityType IndividualTaxpayerIdentificationNumber { get; } = new("US_ITIN");
    /// <summary>Payment card number.</summary>
    public static EntityType CreditCard { get; } = new("CREDIT_CARD");
    /// <summary>IPv4 or IPv6 address.</summary>
    public static EntityType IpAddress { get; } = new("IP_ADDRESS");
    /// <summary>Network interface MAC address.</summary>
    public static EntityType MacAddress { get; } = new("MAC_ADDRESS");
    /// <summary>HTTP or HTTPS URL.</summary>
    public static EntityType Url { get; } = new("URL");
    /// <summary>International Bank Account Number.</summary>
    public static EntityType Iban { get; } = new("IBAN_CODE");
    /// <summary>Calendar date or time.</summary>
    public static EntityType DateTime { get; } = new("DATE_TIME");
    /// <summary>Opt-in credential or secret.</summary>
    public static EntityType Secret { get; } = new("SECRET");
    /// <summary>Natural-person name.</summary>
    public static EntityType Person { get; } = new("PERSON");
    /// <summary>Named geographic location.</summary>
    public static EntityType Location { get; } = new("LOCATION");
    /// <summary>Named organization.</summary>
    public static EntityType Organization { get; } = new("ORGANIZATION");
    /// <summary>United States ABA routing number.</summary>
    public static EntityType BankRoutingNumber { get; } = new("US_BANK_ROUTING_NUMBER");
    /// <summary>Context-qualified bank account identifier.</summary>
    public static EntityType BankAccount { get; } = new("BANK_ACCOUNT");
    /// <summary>United States passport identifier.</summary>
    public static EntityType Passport { get; } = new("US_PASSPORT");
    /// <summary>United States driver-license identifier.</summary>
    public static EntityType DriverLicense { get; } = new("US_DRIVER_LICENSE");
    /// <summary>Medical-license or DEA identifier.</summary>
    public static EntityType MedicalLicense { get; } = new("MEDICAL_LICENSE");
    /// <summary>United States National Provider Identifier.</summary>
    public static EntityType Npi { get; } = new("US_NPI");
    /// <summary>Supported cryptocurrency wallet identifier.</summary>
    public static EntityType CryptocurrencyWallet { get; } = new("CRYPTO");

    /// <summary>Creates a normalized entity type.</summary>
    public static Result<EntityType, DeidentifyError> Create(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result.Failure<EntityType, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Entity type is required."))
            : Result.Success<EntityType, DeidentifyError>(new EntityType(value.Trim().ToUpperInvariant()));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A valid UTF-16 span in a source string.</summary>
public sealed record TextSpan
{
    private TextSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    /// <summary>Zero-based UTF-16 start index.</summary>
    public int Start { get; }
    /// <summary>UTF-16 span length.</summary>
    public int Length { get; }
    /// <summary>Exclusive UTF-16 end index.</summary>
    public int End => Start + Length;

    /// <summary>Creates a span validated against its source length.</summary>
    public static Result<TextSpan, DeidentifyError> Create(int start, int length, int sourceLength) =>
        start < 0 || length <= 0 || sourceLength < 0 || start > sourceLength - length
            ? Result.Failure<TextSpan, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidSpan, "The detected span is outside the source text."))
            : Result.Success<TextSpan, DeidentifyError>(new TextSpan(start, length));

    internal bool Overlaps(TextSpan other) => Start < other.End && other.Start < End;
}

/// <summary>A confidence value from zero through one.</summary>
public sealed record ConfidenceScore
{
    private ConfidenceScore(double value) => Value = value;

    /// <summary>The numeric confidence.</summary>
    public double Value { get; }

    /// <summary>Creates a confidence value between zero and one.</summary>
    public static Result<ConfidenceScore, DeidentifyError> Create(double value) =>
        double.IsNaN(value) || value is < 0 or > 1
            ? Result.Failure<ConfidenceScore, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Confidence must be between zero and one."))
            : Result.Success<ConfidenceScore, DeidentifyError>(new ConfidenceScore(value));
}

/// <summary>A detected sensitive span. Positions are .NET UTF-16 indexes.</summary>
public sealed record Detection
{
    private Detection(
        EntityType entityType,
        TextSpan span,
        ConfidenceScore confidence,
        string recognizerId,
        int recognizerPriority,
        string explanation)
    {
        EntityType = entityType;
        Span = span;
        Confidence = confidence;
        RecognizerId = recognizerId;
        RecognizerPriority = recognizerPriority;
        Explanation = explanation;
    }

    /// <summary>The detected entity category.</summary>
    public EntityType EntityType { get; }
    /// <summary>The detected UTF-16 span.</summary>
    public TextSpan Span { get; }
    /// <summary>The recognizer confidence.</summary>
    public ConfidenceScore Confidence { get; }
    /// <summary>The stable recognizer identifier.</summary>
    public string RecognizerId { get; }
    /// <summary>The recognizer tie-breaking priority.</summary>
    public int RecognizerPriority { get; }
    /// <summary>Safe metadata which never repeats the matched value.</summary>
    public string Explanation { get; }

    /// <summary>Creates a detection with validated span and confidence.</summary>
    public static Result<Detection, DeidentifyError> Create(
        EntityType entityType,
        int start,
        int length,
        int sourceLength,
        double confidence,
        string recognizerId,
        int recognizerPriority = 0) =>
        Create(
            entityType,
            start,
            length,
            sourceLength,
            confidence,
            new DetectionSource(recognizerId, recognizerPriority));

    /// <summary>Creates a detection with explicit safe source metadata.</summary>
    public static Result<Detection, DeidentifyError> Create(
        EntityType entityType,
        int start,
        int length,
        int sourceLength,
        double confidence,
        DetectionSource source) =>
        TextSpan.Create(start, length, sourceLength)
            .Bind(span => ConfidenceScore.Create(confidence)
                .Map(score => new Detection(
                    entityType,
                    span,
                    score,
                    source.RecognizerId,
                    source.Priority,
                    source.Explanation)));
}

/// <summary>Safe metadata supplied by a recognizer.</summary>
public sealed record DetectionSource(
    string RecognizerId,
    int Priority = 0,
    string Explanation = "Deterministic recognizer match.");

/// <summary>Mutable edge configuration converted into immutable analyzer options.</summary>
public sealed record AnalysisOptionsConfiguration
{
    /// <summary>Minimum accepted confidence.</summary>
    public double MinimumConfidence { get; init; } = 0.5;
    /// <summary>Maximum UTF-16 input length.</summary>
    public int MaximumTextLength { get; init; } = 1_000_000;
    /// <summary>Selected entities, or null for all.</summary>
    public IEnumerable<EntityType>? EntityTypes { get; init; }
    /// <summary>Exact values to suppress.</summary>
    public IEnumerable<string>? AllowList { get; init; }
    /// <summary>Exact values to detect by entity type.</summary>
    public IReadOnlyDictionary<EntityType, IEnumerable<string>>? DenyLists { get; init; }
    /// <summary>Context terms by entity type.</summary>
    public IReadOnlyDictionary<EntityType, IEnumerable<string>>? ContextTerms { get; init; }
    /// <summary>Recognizer identifiers to disable.</summary>
    public IEnumerable<string>? DisabledRecognizers { get; init; }
    /// <summary>Whether credential recognizers run.</summary>
    public bool EnableSecretRecognition { get; init; }
}

/// <summary>Validated analyzer configuration.</summary>
public sealed record AnalysisOptions
{
    private AnalysisOptions(
        AnalysisOptionsConfiguration configuration,
        ConfidenceScore minimumConfidence)
    {
        MinimumConfidence = minimumConfidence;
        MaximumTextLength = configuration.MaximumTextLength;
        EntityTypes = configuration.EntityTypes?.ToImmutableHashSet() ?? ImmutableHashSet<EntityType>.Empty;
        AllowList = ToStringSet(configuration.AllowList);
        DenyLists = ToDictionary(configuration.DenyLists);
        ContextTerms = ToDictionary(configuration.ContextTerms);
        DisabledRecognizers = ToStringSet(configuration.DisabledRecognizers);
        EnableSecretRecognition = configuration.EnableSecretRecognition;
    }

    /// <summary>The minimum accepted detection confidence.</summary>
    public ConfidenceScore MinimumConfidence { get; }
    /// <summary>The maximum accepted UTF-16 text length.</summary>
    public int MaximumTextLength { get; }
    /// <summary>Selected entities, or empty for all entities.</summary>
    public ImmutableHashSet<EntityType> EntityTypes { get; }
    /// <summary>Exact matched values to suppress.</summary>
    public ImmutableHashSet<string> AllowList { get; }
    /// <summary>Exact caller-provided values to detect by entity type.</summary>
    public ImmutableDictionary<EntityType, ImmutableArray<string>> DenyLists { get; }
    /// <summary>Caller-provided context terms by entity type.</summary>
    public ImmutableDictionary<EntityType, ImmutableArray<string>> ContextTerms { get; }
    /// <summary>Stable recognizer identifiers to disable.</summary>
    public ImmutableHashSet<string> DisabledRecognizers { get; }
    /// <summary>Whether opt-in secret recognizers run.</summary>
    public bool EnableSecretRecognition { get; }

    /// <summary>Default bounded analyzer options.</summary>
    public static AnalysisOptions Default { get; } = Create().Value;

    /// <summary>Creates validated immutable analyzer options.</summary>
    public static Result<AnalysisOptions, DeidentifyError> Create(
        AnalysisOptionsConfiguration? configuration = null)
    {
        configuration ??= new AnalysisOptionsConfiguration();
        return ConfidenceScore.Create(configuration.MinimumConfidence)
            .Ensure(_ => configuration.MaximumTextLength > 0,
                new DeidentifyError(DeidentifyErrorCode.ConfigurationInvalid, "Maximum text length must be positive."))
            .Map(score => new AnalysisOptions(configuration, score));
    }

    private static ImmutableHashSet<string> ToStringSet(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
        ?? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    private static ImmutableDictionary<EntityType, ImmutableArray<string>> ToDictionary(
        IReadOnlyDictionary<EntityType, IEnumerable<string>>? values) =>
        values?.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray())
        ?? ImmutableDictionary<EntityType, ImmutableArray<string>>.Empty;
}

/// <summary>A validated analysis input.</summary>
public sealed record AnalysisRequest
{
    private AnalysisRequest(string text, AnalysisOptions options)
    {
        Text = text;
        Options = options;
    }

    /// <summary>The source text.</summary>
    public string Text { get; }
    /// <summary>The validated analyzer options.</summary>
    public AnalysisOptions Options { get; }

    /// <summary>Creates a bounded analysis request.</summary>
    public static Result<AnalysisRequest, DeidentifyError> Create(
        string? text,
        AnalysisOptions? options = null)
    {
        options ??= AnalysisOptions.Default;
        if (text is null)
        {
            return Result.Failure<AnalysisRequest, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Text is required."));
        }

        return text.Length > options.MaximumTextLength
            ? Result.Failure<AnalysisRequest, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InputTooLarge, "Text exceeds the configured maximum length."))
            : Result.Success<AnalysisRequest, DeidentifyError>(new AnalysisRequest(text, options));
    }
}

/// <summary>Immutable analysis output.</summary>
public sealed record AnalysisResult(ImmutableArray<Detection> Detections);

/// <summary>Immutable anonymization output.</summary>
public sealed record AnonymizationResult(string Text, ImmutableArray<Detection> Detections);

/// <summary>Safe aggregate output for a line-oriented operation.</summary>
public sealed record StreamingSanitizationResult(
    long LinesProcessed,
    ImmutableDictionary<EntityType, long> DetectionCounts);
