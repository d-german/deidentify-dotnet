# Deidentify.NET

Local, in-process sensitive data detection and de-identification for .NET 8 and .NET 10. This project is inspired by Presidio concepts but is independent and unaffiliated.

> Automated detection cannot guarantee that all PII or PHI is found and does not establish HIPAA compliance. Use a domain-specific validation corpus before production use.

## Capabilities

- Deterministic text detection: email, phone, SSN, ITIN, cards, IP, MAC, URL, IBAN, ABA routing, contextual bank accounts, passport/driver/medical licenses, NPI, dates, cryptocurrency wallets, allowlists, typed denylists, and opt-in secrets.
- Healthcare identifiers: labelled medical record, patient, encounter and visit numbers (`MEDICAL_RECORD_NUMBER`), HL7 v2 `LAST^FIRST^MIDDLE` person names, HL7 PID-3 patient identifiers and PID-7 birth dates, and compact `yyyyMMdd` dates of birth after a DOB label.
- Bundled offline English ONNX NER for `PERSON`, `LOCATION`, and `ORGANIZATION`, labelled per whole word so no part of a name is left behind.
- Replace, redact, fixed replacement, mask, keep, custom, HMAC-SHA256, and authenticated AES-GCM encryption/decryption.
- Strings, batches, dictionaries, selected object properties, JSON, CSV, and bounded line-oriented streams.
- Local OCR word/box extraction, flattened image redaction, flattened PDF redaction, and DICOM PS 3.15 metadata plus burned-in pixel redaction.
- ASP.NET Core and WPF consumption projects plus local SonarQube automation.

See [the parity table](docs/parity.md) and [model provenance](docs/models.md).
Exact build, test, package, publish, vulnerability, and Sonar outcomes are in [the verification report](docs/verification.md).

## Text

```csharp
using Deidentify;
using CSharpFunctionalExtensions;

var sanitizer = new SensitiveDataSanitizer();
var result = sanitizer.Sanitize("Email test.user@example.test");

var safeText = result.Match(
    success => success.Text,
    error => throw new InvalidOperationException($"{error.Code}: {error.SafeMessage}"));
// Email <EMAIL_ADDRESS>
```

Positions in `Detection` are .NET UTF-16 string indexes.

## Analysis and operator selection

```csharp
var options = AnalysisOptions.Create(new()
{
    MinimumConfidence = 0.8,
    EnableSecretRecognition = true
}).Value;

var analyzer = new SensitiveDataAnalyzer();
var analysis = analyzer.Analyze("Call 212-555-0198", options);

var anonymization = new AnonymizationOptions(
    operators: new Dictionary<EntityType, IAnonymizationOperator>
    {
        [EntityType.PhoneNumber] = RedactOperator.Instance
    });

var output = analysis.Bind(value =>
    SensitiveDataAnonymizer.Anonymize("Call 212-555-0198", value, anonymization));
```

## Local NER

```csharp
var ner = LocalNerRecognizer.Create().Value;
var analyzer = new SensitiveDataAnalyzer([ner]);
var result = analyzer.Analyze("Sarah works for Microsoft in London.");
```

The bundled quantized model is English-only, about 104 MiB installed, and loads once per process. It is copied into normal and self-contained publish output without a runtime download.

## Structured data

```csharp
var structured = new StructuredDataSanitizer();
var json = structured.SanitizeJson(
    """{"contact":"test.user@example.test","count":7}""");

await structured.SanitizeCsvAsync(inputStream, outputStream, cancellationToken: cancellationToken);
```

## Streaming logs

```csharp
using var input = File.OpenText("application.log");
using var output = File.CreateText("application.sanitized.log");

var result = await new StreamingSanitizer().SanitizeAsync(
    input,
    output,
    AnalysisOptions.Create(new() { EnableSecretRecognition = true }).Value,
    cancellationToken: cancellationToken);
```

Streaming uses bounded line memory and returns counts by entity type. It cannot detect an entity split across lines.

## OCR and images

```csharp
using var ocr = new TesseractOcrEngine();
var redactor = new ImageRedactor(ocr);
var result = await redactor.RedactAsync(await File.ReadAllBytesAsync("scan.png"));
await File.WriteAllBytesAsync("scan.redacted.png", result.Value.PngImage);
```

Image output is always flattened PNG with metadata removed. The bundled Tesseract adapter is verified on Windows and requires the Visual C++ 2015-2022 runtime. Implement another `IOcrEngine` for a different local OCR runtime.

## PDF and DICOM

```csharp
var imageRedactor = new ImageRedactor(ocr);

var pdf = await new PdfRedactor(imageRedactor)
    .RedactAsync(await File.ReadAllBytesAsync("input.pdf"));

var dicom = await new DicomRedactor(imageRedactor)
    .RedactAsync(await File.ReadAllBytesAsync("input.dcm"));
```

PDF pages are rasterized before rebuilding, removing hidden text and annotation layers. DICOM uses the Basic Application Confidentiality Profile and replaces rendered frames with uncompressed RGB. Compressed DICOM transfer syntaxes require a compatible fo-dicom codec before rendering.

## ASP.NET Core singleton

```csharp
var ner = LocalNerRecognizer.Create().Value;
builder.Services.AddSingleton(new SensitiveDataAnalyzer([ner]));
builder.Services.AddSingleton<SensitiveDataSanitizer>();
```

## WPF singleton

```csharp
public partial class App : Application
{
    public SensitiveDataSanitizer Sanitizer { get; } =
        new(new SensitiveDataAnalyzer([LocalNerRecognizer.Create().Value]));
}
```

The core package has no ASP.NET Core, WPF, service, Python, Docker, or runtime network dependency.

## CLI

```powershell
dotnet run --project samples\Deidentify.Cli -- "Email test.user@example.test"
dotnet run --project samples\Deidentify.Cli -- --file input.log output.log
```

## Build

```powershell
dotnet build Deidentify.sln -c Release
dotnet test Deidentify.sln -c Release
dotnet pack Deidentify.sln -c Release -o artifacts\packages
pwsh eng\verify-consumers.ps1
```

## Local SonarQube

The disposable development instance binds only to `127.0.0.1:9000`.

```powershell
docker compose -f compose.sonar.yml up -d
$env:SONAR_TOKEN = "<local-project-token>"
pwsh eng\sonar.ps1
```
