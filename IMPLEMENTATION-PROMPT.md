# Deidentify.NET implementation prompt

You are implementing **Deidentify.NET**, an independent, reusable .NET library for detecting and de-identifying sensitive information in text, structured data, logs, images, PDFs, and, where feasible, DICOM files.

This repository starts empty. Build the implementation here. Do not modify or depend on SleepEdit. SleepEdit will consume the finished NuGet packages later, but this library must also work independently in ASP.NET Core, Blazor Server, WPF Blazor Hybrid, console applications, Windows services, background workers, and log-processing tools.

Do not stop after producing a design or scaffold. Research, implement, test, analyze, package, and document a working solution. Deliver the full practical implementation in testable phases, and clearly document any parity gaps that cannot be closed because of licensing, unavailable models, platform restrictions, or unacceptable accuracy.

## Product identity

- Repository: `deidentify-dotnet`
- Solution: `Deidentify.sln`
- Root namespace: `Deidentify`
- Core NuGet package: `Deidentify`
- Optional imaging NuGet package: `Deidentify.Imaging`
- CLI sample: `Deidentify.Cli`

This is inspired by the capabilities and concepts of Presidio, but it is not an official Presidio or Microsoft product. Do not use branding that implies affiliation.

## Primary objectives

1. Perform all production processing locally and in-process.
2. Require no Python runtime, Docker container, Presidio server, localhost HTTP service, cloud service, or network connection at runtime.
3. Multi-target `net8.0` and `net10.0` from one source base.
4. Provide reusable APIs for analysis, anonymization, pseudonymization, structured data, streaming logs, OCR, and image redaction.
5. Package cleanly as NuGet libraries that can be included in self-contained web and desktop deployments.
6. Use an object-oriented shell with a functional, immutable core and railway-oriented application pipelines.
7. Fail closed: a failed required analysis or redaction operation must never be reported as successful or return unredacted input as if it were safe.
8. Never expose sensitive input through logs, metrics, exceptions, error messages, traces, or test artifacts.
9. Achieve practical functional parity with Presidio and prove behavior with an explicit parity corpus.

## Authoritative references

Review these sources before implementation:

- Presidio repository: <https://github.com/data-privacy-stack/presidio>
- Presidio documentation: <https://presidio.dataprivacystack.org/>
- Presidio Analyzer: <https://presidio.dataprivacystack.org/analyzer/>
- Presidio Anonymizer: <https://presidio.dataprivacystack.org/anonymizer/>
- ONNX Runtime C# documentation: <https://onnxruntime.ai/docs/get-started/with-csharp.html>
- SonarQube Community Build documentation: <https://docs.sonarsource.com/sonarqube-community-build/>

Pin the exact Presidio tag or commit used as the behavioral reference. Presidio is MIT licensed. Preserve all required attribution, license, and NOTICE material for anything copied or adapted. Add `THIRD-PARTY-NOTICES.md` and a short provenance table identifying what was copied, adapted, or independently implemented.

Also inspect these local examples before designing the Result pipelines:

- `C:\work\security-dashboard\src\CheckmarxTool.Core\Extensions\ResultHelpers.cs`
- `C:\work\security-dashboard\src\CheckmarxTool.Core\Extensions\ResultExtensions.cs`
- `C:\work\security-dashboard\src\CheckmarxTool.Web\Services\Dependabot\GitHubDependabotClient.cs`

They use `CSharpFunctionalExtensions` 3.6.0 and demonstrate `Result`, `Bind`, `Map`, `Tap`, `MapError`, and `Combine`. Preserve the good railway flow, but do not copy the unsafe practice of returning raw `Exception.Message` values because those messages may contain sensitive input.

Use Rider MCP for solution navigation, semantic inspection, diagnostics, builds, and tests whenever it is available. Use the .NET CLI for reproducible command-line verification.

## Required solution structure

Start with the minimum useful projects:

```text
Deidentify.sln
src/
  Deidentify/
    Deidentify.csproj
  Deidentify.Imaging/
    Deidentify.Imaging.csproj
tests/
  Deidentify.Tests/
    Deidentify.Tests.csproj
  Deidentify.Imaging.Tests/
    Deidentify.Imaging.Tests.csproj
samples/
  Deidentify.Cli/
    Deidentify.Cli.csproj
```

Do not introduce more projects, architectural layers, factories, service hosts, or dependency-injection packages unless a demonstrated requirement justifies them. `Deidentify.Imaging` may depend on `Deidentify`; the core package must not depend on imaging.

Both libraries must use:

```xml
<TargetFrameworks>net8.0;net10.0</TargetFrameworks>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
```

Use Central Package Management and pin every dependency version. Do not add a dependency when the .NET standard library already provides the required behavior.

## Architecture: functional core and object-oriented shell

Use object-oriented polymorphism where it expresses real variation:

- Entity recognizers
- Anonymization operators
- NLP model adapters
- OCR engines
- Document and image adapters

Use sealed implementations by default, composition over inheritance, and interfaces only when multiple implementations exist or an external dependency needs an adapter.

Use `CSharpFunctionalExtensions` **3.6.0** throughout the internal application and domain flow:

- `Bind` for operations that can fail.
- `Map` for pure transformations.
- `Ensure` for validation and invariants.
- `Tap` for safe side effects after success.
- `TapError` for safe failure diagnostics.
- `MapError` for adding safe operation context.
- `Combine` for aggregating validation or recognizer results.
- `Match` only at the outermost application boundary.
- `Maybe<T>` for genuinely optional domain values.
- `Result<T, DeidentifyError>` for fallible value-producing operations.
- `UnitResult<DeidentifyError>` for fallible operations without a returned value.

Do not access `.Value` unless success has already been proven. Prefer composition with `Bind`, `Map`, `Match`, or `GetValueOrDefault`.

A representative orchestration pipeline should read like this:

```csharp
return AnalysisRequest.Create(text, options)
    .Bind(inputValidator.Validate)
    .Bind(analyzer.Analyze)
    .Bind(overlapResolver.Resolve)
    .Map(detections => new AnalysisResult(detections))
    .Tap(result => metrics.RecordDetectionCounts(result))
    .MapError(error => error.WithOperation("Analyze"));
```

`Tap` and `TapError` may record safe counts, durations, entity types, operation names, and error codes. They must never record input text, matched values, OCR output, secrets, tokens, file contents, or unsafe exception messages.

## Immutable domain model

Prefer:

- Sealed records
- `readonly record struct` for small validated values
- `ImmutableArray<T>`
- `ImmutableDictionary<TKey, TValue>`
- `FrozenDictionary<TKey, TValue>` for configuration built once and read repeatedly
- Init-only properties only where serializers require them

Mutable lists, dictionaries, DTOs, streams, framework request objects, and serializer models are allowed at external edges. Convert them into validated immutable domain values once, then remain immutable internally.

Candidate immutable domain values include:

- `EntityType`
- `TextSpan`
- `ConfidenceScore`
- `Detection`
- `AnalysisRequest`
- `AnalysisOptions`
- `AnalysisResult`
- `AnonymizationRequest`
- `AnonymizationResult`
- `RecognitionContext`
- `SanitizationOptions`
- `DeidentifyError`

Create values with invariants through factories returning `Result`, for example:

```csharp
Result<TextSpan, DeidentifyError> TextSpan.Create(
    int start,
    int length,
    int sourceLength);
```

Once constructed, a domain value must be valid.

## Structured error model

Do not use arbitrary strings as internal errors. Define a safe immutable error, such as:

```csharp
public sealed record DeidentifyError(
    DeidentifyErrorCode Code,
    string SafeMessage,
    string? Operation = null);
```

Use stable error codes for at least:

- `InvalidInput`
- `InputTooLarge`
- `RecognitionFailed`
- `ModelUnavailable`
- `ModelLoadFailed`
- `OcrFailed`
- `InvalidSpan`
- `AnonymizationFailed`
- `FileReadFailed`
- `FileWriteFailed`
- `UnsupportedFormat`
- `ConfigurationInvalid`
- `Cancelled` only if cancellation is modeled explicitly instead of propagated

Errors must never contain analyzed text, detected PII or PHI, OCR output, secrets, tokens, file contents, or potentially unsafe exception messages.

## Exception and cancellation policy

Expected failures use Results and do not throw exceptions. Do not scatter `try/catch` through domain or application code.

Result types cannot automatically intercept exceptions from file I/O, regex execution, JSON serialization, ONNX Runtime, OCR libraries, native libraries, or other framework APIs. Catch those exceptions once at the relevant infrastructure boundary and immediately convert them into a safe Result failure. Centralize conversion in a minimal boundary helper or adapter.

Rules:

- No `try/catch` in pure domain logic or pipeline orchestration.
- Boundary adapters may contain the few unavoidable catches.
- Never catch an exception merely to rethrow it unchanged.
- Never return raw `Exception.Message` from a public API.
- Never silently swallow cleanup failures.
- Preserve requested cancellation. Do not convert `OperationCanceledException` caused by the caller's `CancellationToken` into an ordinary failure.
- Document every location where an exception is converted into a Result.

## Core public API

Keep the public API small and strongly typed. It should provide equivalents of:

- `SensitiveDataAnalyzer`
- `SensitiveDataAnonymizer`
- `IEntityRecognizer`
- `IAnonymizationOperator`
- `Detection`
- `AnalysisOptions`
- `AnalysisResult`
- `AnonymizationOptions`
- `AnonymizationResult`
- A convenience `Sanitize` operation

Detection information must include:

- Entity type
- Start position
- Length
- Confidence score
- Recognizer name or identifier
- Safe explanatory metadata that never repeats the matched value

Document that positions use .NET UTF-16 string indexes.

Allow callers to:

- Select entity types.
- Configure minimum confidence.
- Supply allowlists and denylists.
- Enable or disable recognizers.
- Register custom recognizers.
- Choose an anonymization operator by entity type.
- Configure context terms and recognizer priority.
- Apply country, language, or domain-specific recognizer packs.

## Analyzer implementation

Implement deterministic recognizers first, including appropriate positive, negative, contextual, and checksum validation:

- Email address
- Phone number
- Social Security number
- ITIN
- Credit-card number with Luhn validation
- IP address
- MAC address
- URL
- IBAN
- Bank and account identifiers where reliable
- Passport identifiers where reliable
- Driver-license identifiers where reliable
- Medical-license identifiers where reliable
- Dates and times
- Cryptocurrency wallet identifiers supported by Presidio
- Other Presidio built-in deterministic entities that can be reproduced accurately

Provide opt-in recognizers for log and secret sanitization:

- Authorization bearer tokens
- Common API-key formats
- Passwords and secrets in key/value configuration
- Connection-string passwords
- Common private-key headers

Secret detection is distinct from PII detection and must remain independently configurable to control false positives.

Use `RegexOptions.NonBacktracking` when suitable. Otherwise use short explicit regex timeouts. Test for catastrophic backtracking and ReDoS. Prefer parsers and checksums over regex where the standard library already supports them.

Support context-aware confidence scoring. Resolve overlaps deterministically using:

1. Higher confidence
2. Longer match
3. Explicit recognizer priority
4. Stable original order

## Local NLP and named-entity recognition

Implement local named-entity recognition for people, locations, and organizations. Prefer an ONNX model callable through ONNX Runtime from C# when it provides acceptable accuracy and redistribution terms.

Before adopting a model, document:

- Exact source and version
- License and redistribution permission
- Supported languages
- Download and installed size
- CPU and memory requirements
- Tokenizer requirements
- Accuracy on the parity corpus
- Whether it is embedded or installed as package content

Models must be available without a runtime download. Load expensive sessions once and reuse them safely across concurrent calls. Do not load a model per request.

Add multilingual support where suitably licensed models and adequate evaluation data exist. Clearly state the evaluated languages and known gaps.

## Anonymization and pseudonymization

Implement:

- Entity marker replacement, such as `<EMAIL_ADDRESS>`
- Complete redaction
- Character masking
- Caller-provided fixed replacement
- Safe custom operators
- HMAC-SHA256 pseudonymization with a key supplied and managed by the caller
- Reversible encryption and decryption only if implemented using modern authenticated encryption with explicit key-management requirements
- Presidio operators that can be reproduced safely and usefully

Do not use an ordinary unsalted hash for pseudonymization. Do not invent cryptography. Apply replacements from the end of the input toward the beginning so earlier indexes remain valid.

## Structured and streaming data

Support:

- Plain strings
- Line-oriented logs
- `TextReader` to `TextWriter` streaming
- Dictionaries and object properties through explicit adapters
- JSON
- CSV and tabular records
- Batch operations

The asynchronous streaming API must:

- Use bounded memory.
- Preserve line endings where practical.
- Support `CancellationToken`.
- Return aggregate counts by entity type without retaining matched values.
- Never write original sensitive values to diagnostics.

Document that line-oriented processing cannot detect an entity split across lines. Retain whole-text processing for callers that require cross-line detection.

The CLI sample must sanitize supplied text or a log file by calling the class library. It must contain no recognition or anonymization business logic.

## OCR, images, PDFs, and DICOM

Implement imaging in `Deidentify.Imaging`, keeping native and large dependencies out of the core text package.

Evaluate publicly available open-source OCR engines that can be legally redistributed and used locally from .NET. Tesseract is an expected candidate, but do not choose it without comparing accuracy, maintenance, deployment size, native runtime requirements, and license terms against credible alternatives.

The imaging flow must:

1. Extract words, text, confidence, and bounding boxes.
2. Construct a stable mapping between OCR text spans and bounding boxes.
3. Analyze the extracted text through the core analyzer.
4. Map detected spans back to one or more image regions.
5. Redact those regions irreversibly.
6. Return safe metadata and a redacted image or document.

Support common image formats. Add PDF processing and DICOM redaction where appropriate .NET libraries and licenses permit it. Preserve original files by default; write to a separate output unless the caller explicitly requests replacement.

Test rotated text, low contrast, multiple bounding boxes, Unicode, multipage documents, metadata leakage, malformed files, and unsupported formats. Redaction must remove sensitive pixels rather than merely draw a removable annotation layer.

## Web and desktop compatibility

The same core implementation must run inside:

- ASP.NET Core
- Blazor Server
- WPF Blazor Hybrid
- Console applications
- Windows services
- Background workers

The core library must not depend on ASP.NET Core, WPF, or another host framework. Blazor WebAssembly running inside the browser is not an initial target.

All analysis must occur in the consuming process. No call to localhost or another service may be required.

Provide concise examples showing manual registration of a thread-safe singleton in ASP.NET Core and WPF. Do not create a separate dependency-injection package merely for one registration extension.

For a web farm, assume every application instance loads its own local model. Make analyzers and model sessions safe for concurrent requests. Add configurable bounds for:

- Maximum text size
- Maximum image size
- Maximum PDF or DICOM page count
- OCR concurrency
- Analysis timeout and cancellation

Do not use global mutable request or tenant configuration.

## Self-contained deployment

Verify consumption from self-contained hosts:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r linux-x64 --self-contained true
```

At minimum:

- WPF consumption works on `win-x64`.
- ASP.NET Core consumption works on `win-x64` and `linux-x64`.
- Required ONNX models, OCR language data, and native binaries are copied into publish output automatically.
- Published applications work with network access disabled and without Python or Docker.
- Unavoidable operating-system dependencies are documented.

Do not require single-file publishing initially. First prove a normal self-contained publish directory. Investigate single-file deployment only afterward.

## Security and privacy requirements

- Never log original input or matched values.
- Never put sensitive values in exceptions, Results, tracing tags, assertion messages, snapshots, or generated test reports.
- Use only synthetic data in the repository and tests.
- Validate input size and format at trust boundaries.
- Remain thread-safe and stateless unless state is explicitly documented.
- Fail closed when required analysis or redaction fails.
- Avoid regex denial-of-service.
- Avoid path traversal and unsafe output replacement.
- Do not retain input longer than required.
- Zero or dispose buffers where practical and meaningful; do not make unverifiable memory-erasure guarantees for immutable .NET strings.
- Clearly state that automated detection cannot guarantee all PII or PHI will be found and does not itself establish HIPAA compliance.

## Testing and parity

Use synthetic data only. Tests must cover:

- Positive and negative cases for every recognizer
- Common false positives
- Context scoring
- Checksums
- Unicode and UTF-16 span correctness
- Overlapping detections
- Confidence thresholds
- Allowlists and denylists
- Custom recognizers and operators
- Every anonymization operation
- Repeated values
- Empty, malformed, and bounded large inputs
- Regex timeout behavior
- Concurrent analysis
- Streaming logs
- Cancellation
- OCR span-to-box mapping
- Image, PDF, and DICOM redaction where supported
- Self-contained deployment asset inclusion
- No sensitive data in logs or errors

Functional tests must prove:

- Pipelines short-circuit after failure.
- `Tap` does not run after failure.
- `Map` preserves failures.
- `MapError` preserves failure state.
- `Combine` fails when any required component fails.
- Invalid domain values cannot be constructed.
- Public result collections are immutable.
- Cancellation remains cancellation.
- Exception conversion occurs only at infrastructure boundaries.

Create a synthetic reference corpus that can be executed against both the pinned Presidio version and Deidentify.NET. Presidio, Python, or Docker may be used only by development parity tooling, never by production runtime. Store expected behavior without storing real sensitive information.

Produce a parity table containing:

- Presidio capability or entity
- Deidentify.NET implementation
- Test evidence
- Equivalent, intentionally different, partial, or unsupported status
- Reason for any gap

Do not claim full parity until the evidence supports it.

## Local SonarQube quality gate

Create a local SonarQube Community Build environment used only for development analysis. It must not become a runtime or package dependency.

Add:

- `compose.sonar.yml`
- `eng/sonar.ps1`
- A repository-local .NET tool manifest containing SonarScanner for .NET and `dotnet-coverage`
- README setup instructions

Use the official SonarQube Community Build image pinned to a current explicit version at implementation time. Do not use an unversioned `latest` tag.

The instance must:

- Listen only on `localhost:9000`.
- Use persistent named Docker volumes.
- Use embedded H2 only because this is a disposable local development instance.
- Never be exposed publicly.
- Never commit passwords, tokens, or populated environment files.

`eng/sonar.ps1` must:

1. Verify Docker is available.
2. Start SonarQube when needed.
3. Wait until it is healthy.
4. Verify `SONAR_TOKEN` exists without printing it.
5. Restore local .NET tools.
6. Begin analysis.
7. Build the complete solution in Release mode with no incremental build.
8. Run every test and generate coverage using `dotnet-coverage`.
9. Submit analysis.
10. Wait for the quality gate with `sonar.qualitygate.wait=true`.
11. Exit nonzero for any build, test, scanner, or quality-gate failure.

Import coverage through `sonar.cs.vscoveragexml.reportsPaths`. Do not merely generate a report and assume SonarQube imported it.

Required quality outcomes:

- Quality gate passes.
- Release build has zero warnings.
- All tests pass.
- New-code coverage is at least 80%.
- New-code duplication is below 3%.
- No unresolved high-severity reliability or security issues.
- Every security hotspot is reviewed.
- No production source tree is excluded to hide findings.

Do not add `// NOSONAR`, lower gates, broadly exclude files, or suppress rules merely to obtain a pass. An individually accepted false positive must include the rule identifier and a written technical justification.

## Dependency and quality policy

- Prefer the .NET standard library before adding packages.
- Use maintained, redistributable dependencies with compatible licenses.
- Run vulnerability checks for NuGet and any JavaScript tooling introduced.
- Commit lock files and tool manifests needed for reproducibility.
- Enable deterministic builds.
- Add analyzers only when they provide clear value and do not duplicate SonarQube without reason.
- Keep public XML documentation useful and concise.
- Do not create speculative abstractions or boilerplate for hypothetical future requirements.

## Implementation phases

These are delivery checkpoints, not reasons to stop early:

1. Research, architecture decision record, licensing, and parity plan
2. Immutable domain model and Result-based pipelines
3. Deterministic recognizers and contextual scoring
4. Overlap resolution and anonymization operators
5. Streaming logs and structured data
6. Local ONNX NER
7. OCR and image redaction
8. PDF and DICOM support where feasible
9. Multilingual support where feasible
10. Self-contained web and desktop consumption tests
11. Local SonarQube gate, packaging, documentation, and final parity report

At every phase, run the smallest relevant test set, then run the complete Release validation before claiming the phase is complete. Commit coherent, reviewable increments.

## Definition of done

The work is complete only when:

- `dotnet build Deidentify.sln -c Release` succeeds with zero warnings and errors.
- Tests pass under both `net8.0` and `net10.0`.
- `dotnet pack -c Release` produces usable packages.
- Text and log sanitization run completely offline.
- Local NER runs without downloading a model at runtime.
- Supported OCR and imaging operations run without an external service.
- ASP.NET Core and WPF consumers produce equivalent analysis results.
- Self-contained `win-x64` and `linux-x64` validation succeeds for applicable hosts.
- The local SonarQube quality gate passes with imported coverage.
- Dependency vulnerability checks pass.
- The parity table and known limitations are accurate.
- README examples show text analysis, anonymization, log sanitization, ASP.NET Core registration, WPF registration, and imaging.
- No test, log, error, or artifact contains real sensitive data.

The final report must include exact commands and outcomes for builds, tests, coverage, SonarQube, vulnerability checks, packaging, self-contained publishing, and parity evaluation. Distinguish verified results from remaining limitations. Do not describe work as production-ready based only on compilation or unit tests.
