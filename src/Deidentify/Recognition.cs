using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deidentify;

/// <summary>Context supplied to entity recognizers.</summary>
public sealed record RecognitionContext(AnalysisOptions Options);

/// <summary>Detects one or more entity types in text.</summary>
public interface IEntityRecognizer
{
    /// <summary>Stable recognizer identifier.</summary>
    string Id { get; }
    /// <summary>Tie-breaking priority.</summary>
    int Priority { get; }
    /// <summary>Entities the recognizer can produce.</summary>
    ImmutableArray<EntityType> SupportedEntities { get; }
    /// <summary>Analyzes text without retaining matched values.</summary>
    Result<ImmutableArray<Detection>, DeidentifyError> Analyze(string text, RecognitionContext context);
}

internal sealed class RegexEntityRecognizer : IEntityRecognizer
{
    private readonly EntityType _entityType;
    private readonly Regex _regex;
    private readonly double _confidence;
    private readonly ImmutableArray<string> _contextTerms;
    private readonly Func<string, bool>? _validator;
    private readonly bool _isSecret;

    public RegexEntityRecognizer(
        string id,
        EntityType entityType,
        string pattern,
        double confidence,
        IEnumerable<string>? contextTerms = null,
        Func<string, bool>? validator = null,
        int priority = 0)
    {
        Id = id;
        Priority = priority;
        _entityType = entityType;
        _confidence = confidence;
        _contextTerms = contextTerms?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        _validator = validator;
        _isSecret = entityType == EntityType.Secret;
        SupportedEntities = [entityType];
        _regex = CreateRegex(pattern);
    }

    public string Id { get; }
    public int Priority { get; }
    public ImmutableArray<EntityType> SupportedEntities { get; }

    private static Regex CreateRegex(string pattern)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, options, TimeSpan.FromMilliseconds(100));
        }
    }

    public Result<ImmutableArray<Detection>, DeidentifyError> Analyze(
        string text,
        RecognitionContext context)
    {
        if (_isSecret && !context.Options.EnableSecretRecognition)
        {
            return Result.Success<ImmutableArray<Detection>, DeidentifyError>([]);
        }

        if (context.Options.EntityTypes.Count > 0 && !context.Options.EntityTypes.Contains(_entityType))
        {
            return Result.Success<ImmutableArray<Detection>, DeidentifyError>([]);
        }

        try
        {
            var detections = ImmutableArray.CreateBuilder<Detection>();
            foreach (Match match in _regex.Matches(text))
            {
                if (_validator is not null && !_validator(match.Value))
                {
                    continue;
                }

                var score = HasContext(text, match, context.Options) ? Math.Min(1, _confidence + 0.15) : _confidence;
                var detection = Detection.Create(
                    _entityType,
                    match.Index,
                    match.Length,
                    text.Length,
                    score,
                    new DetectionSource(
                        Id,
                        Priority,
                        _validator is null
                            ? "Deterministic pattern match."
                            : "Deterministic pattern and validation match."));

                if (detection.IsFailure)
                {
                    return Result.Failure<ImmutableArray<Detection>, DeidentifyError>(detection.Error);
                }

                detections.Add(detection.Value);
            }

            return Result.Success<ImmutableArray<Detection>, DeidentifyError>(detections.ToImmutable());
        }
        catch (RegexMatchTimeoutException)
        {
            return Result.Failure<ImmutableArray<Detection>, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.RecognitionFailed, "A recognizer exceeded its safe execution limit."));
        }
    }

    private bool HasContext(string text, Match match, AnalysisOptions options)
    {
        var terms = _contextTerms;
        if (options.ContextTerms.TryGetValue(_entityType, out var callerTerms))
        {
            terms = terms.AddRange(callerTerms);
        }

        if (terms.IsDefaultOrEmpty)
        {
            return false;
        }

        const int contextRadius = 50;
        var start = Math.Max(0, match.Index - contextRadius);
        var end = Math.Min(text.Length, match.Index + match.Length + contextRadius);
        var context = text.AsSpan(start, end - start);
        foreach (var term in terms)
        {
            if (context.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class DenyListRecognizer : IEntityRecognizer
{
    public string Id => "caller-deny-lists";
    public int Priority => 100;
    public ImmutableArray<EntityType> SupportedEntities => [];

    public Result<ImmutableArray<Detection>, DeidentifyError> Analyze(
        string text,
        RecognitionContext context)
    {
        var detections = ImmutableArray.CreateBuilder<Detection>();
        foreach (var pair in context.Options.DenyLists)
        {
            if (context.Options.EntityTypes.Count > 0 && !context.Options.EntityTypes.Contains(pair.Key))
            {
                continue;
            }

            foreach (var value in pair.Value)
            {
                var start = 0;
                while ((start = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var detection = Detection.Create(
                        pair.Key,
                        start,
                        value.Length,
                        text.Length,
                        1,
                        new DetectionSource(Id, Priority, "Caller denylist match."));

                    if (detection.IsFailure)
                    {
                        return Result.Failure<ImmutableArray<Detection>, DeidentifyError>(detection.Error);
                    }

                    detections.Add(detection.Value);
                    start += value.Length;
                }
            }
        }

        return Result.Success<ImmutableArray<Detection>, DeidentifyError>(detections.ToImmutable());
    }
}

internal static class BuiltInRecognizers
{
    public static ImmutableArray<IEntityRecognizer> Create() =>
    [
        new RegexEntityRecognizer(
            "email",
            EntityType.EmailAddress,
            @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,63}\b",
            0.85,
            ["email", "e-mail"]),
        new RegexEntityRecognizer(
            "phone",
            EntityType.PhoneNumber,
            @"(?<!\w)(?:\+?1[\s.\-]?)?(?:\(\d{3}\)|\d{3})[\s.\-]\d{3}[\s.\-]\d{4}(?!\w)",
            0.7,
            ["phone", "mobile", "call", "tel"]),
        new RegexEntityRecognizer(
            "us-ssn",
            EntityType.SocialSecurityNumber,
            @"(?<!\d)(?!000|666|9\d{2})\d{3}[\- ](?!00)\d{2}[\- ](?!0000)\d{4}(?!\d)",
            0.85,
            ["ssn", "social security"],
            priority: 20),
        new RegexEntityRecognizer(
            "us-itin",
            EntityType.IndividualTaxpayerIdentificationNumber,
            @"(?<!\d)9\d{2}[\- ](?:7\d|8[0-8]|9[0-2]|9[4-9])[\- ]\d{4}(?!\d)",
            0.85,
            ["itin", "taxpayer"],
            priority: 20),
        new RegexEntityRecognizer(
            "credit-card",
            EntityType.CreditCard,
            @"(?<!\d)(?:\d[ \-]?){13,19}(?!\d)",
            0.8,
            ["card", "visa", "mastercard", "payment"],
            Validators.IsLuhnValid,
            30),
        new RegexEntityRecognizer(
            "ip-address",
            EntityType.IpAddress,
            @"(?<![\w:])(?:[A-F0-9:.]{3,})(?![\w:])",
            0.7,
            ["ip", "address", "host"],
            value => value.IndexOfAny(['.', ':']) >= 0 && IPAddress.TryParse(value, out _)),
        new RegexEntityRecognizer(
            "mac-address",
            EntityType.MacAddress,
            @"\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b",
            0.8,
            ["mac", "adapter"]),
        new RegexEntityRecognizer(
            "url",
            EntityType.Url,
            @"\bhttps?://[^\s<>""'\]\[{}]*[A-Z0-9/#]",
            0.75,
            ["url", "website", "link"]),
        new RegexEntityRecognizer(
            "iban",
            EntityType.Iban,
            @"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b",
            0.8,
            ["iban", "bank", "account"],
            Validators.IsIbanValid,
            20),
        new RegexEntityRecognizer(
            "date",
            EntityType.DateTime,
            @"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})\b",
            0.55,
            ["date", "born", "dob"],
            Validators.IsDateValid),
        new RegexEntityRecognizer(
            "aba-routing",
            EntityType.BankRoutingNumber,
            @"(?<!\d)\d{9}(?!\d)",
            0.75,
            ["aba", "routing", "bank"],
            Validators.IsAbaRoutingValid,
            20),
        new RegexEntityRecognizer(
            "bank-account",
            EntityType.BankAccount,
            @"(?<!\d)\d{8,17}(?!\d)",
            0.35,
            ["account", "acct", "bank"],
            priority: 5),
        new RegexEntityRecognizer(
            "us-passport",
            EntityType.Passport,
            @"\b(?:[A-Z]\d{8}|\d{9})\b",
            0.35,
            ["passport"],
            priority: 10),
        new RegexEntityRecognizer(
            "us-driver-license",
            EntityType.DriverLicense,
            @"\b(?:[A-Z]\d{7,12}|\d{7,12})\b",
            0.35,
            ["driver", "license", "licence", "dl"],
            priority: 10),
        new RegexEntityRecognizer(
            "dea-license",
            EntityType.MedicalLicense,
            @"\b[A-Z][A-Z0-9]\d{7}\b",
            0.8,
            ["dea", "medical", "license"],
            Validators.IsDeaValid,
            20),
        new RegexEntityRecognizer(
            "npi",
            EntityType.Npi,
            @"(?<!\d)\d{10}(?!\d)",
            0.8,
            ["npi", "provider"],
            Validators.IsNpiValid,
            20),
        new RegexEntityRecognizer(
            "cryptocurrency-wallet",
            EntityType.CryptocurrencyWallet,
            @"\b(?:0x[0-9A-F]{40}|bc1[AC-HJ-NP-Z02-9]{11,71}|[13][A-KM-Z1-9]{25,34})\b",
            0.8,
            ["wallet", "bitcoin", "ethereum", "crypto"],
            Validators.IsCryptocurrencyWalletValid,
            20),
        new RegexEntityRecognizer(
            "bearer-token",
            EntityType.Secret,
            @"\bBearer\s+[A-Z0-9\-._~+/]+=*",
            0.95,
            priority: 50),
        new RegexEntityRecognizer(
            "api-key",
            EntityType.Secret,
            @"\b(?:AKIA[0-9A-Z]{16}|(?:sk|pk)_(?:live|test)_[A-Z0-9]{16,})\b",
            0.95,
            priority: 50),
        new RegexEntityRecognizer(
            "key-value-secret",
            EntityType.Secret,
            @"\b(?:password|passwd|pwd|secret)\s*[:=]\s*[^\s;,]+",
            0.95,
            priority: 50),
        new RegexEntityRecognizer(
            "private-key-header",
            EntityType.Secret,
            @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
            1,
            priority: 100),
        new DenyListRecognizer()
    ];
}

internal static class Validators
{
    public static bool IsLuhnValid(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    public static bool IsIbanValid(string value)
    {
        var iban = string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !iban.Take(2).All(char.IsLetter))
        {
            return false;
        }

        var remainder = 0;
        foreach (var character in string.Concat(iban.AsSpan(4), iban.AsSpan(0, 4)))
        {
            if (char.IsAsciiDigit(character))
            {
                remainder = ((remainder * 10) + character - '0') % 97;
                continue;
            }

            var numeric = character - 'A' + 10;
            remainder = ((remainder * 100) + numeric) % 97;
        }

        return remainder == 1;
    }

    public static bool IsDateValid(string value)
    {
        var formats = new[]
        {
            "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"
        };
        return DateOnly.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    public static bool IsAbaRoutingValid(string value)
    {
        if (value.Length != 9 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var digits = value.Select(character => character - '0').ToArray();
        var checksum =
            3 * (digits[0] + digits[3] + digits[6]) +
            7 * (digits[1] + digits[4] + digits[7]) +
            digits[2] + digits[5] + digits[8];
        return checksum % 10 == 0 && digits.Take(8).Any(digit => digit != 0);
    }

    public static bool IsDeaValid(string value)
    {
        if (value.Length != 9 ||
            !char.IsAsciiLetterUpper(value[0]) ||
            !(char.IsAsciiLetterUpper(value[1]) || char.IsAsciiDigit(value[1])) ||
            !value.AsSpan(2).ToArray().All(char.IsAsciiDigit))
        {
            return false;
        }

        var digits = value.AsSpan(2).ToArray().Select(character => character - '0').ToArray();
        return (digits[0] + digits[2] + digits[4] + 2 * (digits[1] + digits[3] + digits[5])) % 10 ==
               digits[6];
    }

    public static bool IsNpiValid(string value) =>
        value.Length == 10 &&
        value.All(char.IsAsciiDigit) &&
        IsLuhnValid($"80840{value}");

    public static bool IsCryptocurrencyWalletValid(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length == 42 && value.AsSpan(2).ToArray().All(Uri.IsHexDigit);
        }

        if (value.StartsWith("bc1", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length is >= 14 and <= 74;
        }

        return value.Length is >= 26 and <= 35 && value.All(
            character => character is not ('0' or 'O' or 'I' or 'l') &&
                         (char.IsAsciiLetterOrDigit(character)));
    }
}
