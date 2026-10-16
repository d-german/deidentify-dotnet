using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deidentify.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Sanitize_replaces_deterministic_entities_and_resolves_overlaps()
    {
        var sanitizer = new SensitiveDataSanitizer();

        var result = sanitizer.Sanitize(
            "Email test.user@example.test or card 4111 1111 1111 1111.");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "Email <EMAIL_ADDRESS> or card <CREDIT_CARD>.",
            result.Value.Text);
        Assert.Equal(2, result.Value.Detections.Length);
    }

    [Fact]
    public void Invalid_values_cannot_be_created()
    {
        Assert.True(TextSpan.Create(-1, 1, 5).IsFailure);
        Assert.True(TextSpan.Create(0, 0, 5).IsFailure);
        Assert.True(ConfidenceScore.Create(1.1).IsFailure);
        Assert.True(EntityType.Create(" ").IsFailure);
    }

    [Fact]
    public void Context_raises_confidence_above_threshold()
    {
        var options = AnalysisOptions.Create(new AnalysisOptionsConfiguration
        {
            MinimumConfidence = 0.8
        }).Value;
        var analyzer = new SensitiveDataAnalyzer();

        var contextual = analyzer.Analyze("Call 212-555-0198", options);
        var isolated = analyzer.Analyze("212-555-0198", options);

        Assert.Single(contextual.Value.Detections);
        Assert.Empty(isolated.Value.Detections);
    }

    [Fact]
    public void Allow_and_deny_lists_are_applied_without_retaining_values()
    {
        var custom = EntityType.Create("PATIENT_ID").Value;
        var options = AnalysisOptions.Create(new AnalysisOptionsConfiguration
        {
            AllowList = ["test.user@example.test"],
            DenyLists = new Dictionary<EntityType, IEnumerable<string>>
            {
                [custom] = ["SYNTHETIC-42"]
            }
        }).Value;

        var result = new SensitiveDataAnalyzer().Analyze(
            "test.user@example.test SYNTHETIC-42",
            options);

        Assert.Single(result.Value.Detections);
        Assert.Equal(custom, result.Value.Detections[0].EntityType);
        Assert.DoesNotContain("SYNTHETIC-42", result.Value.Detections[0].Explanation);
    }

    [Fact]
    public void Secrets_are_opt_in()
    {
        const string text = "Authorization: Bearer synthetic.token.value";
        var analyzer = new SensitiveDataAnalyzer();

        Assert.Empty(analyzer.Analyze(text).Value.Detections);
        Assert.Single(analyzer.Analyze(
            text,
            AnalysisOptions.Create(new AnalysisOptionsConfiguration
            {
                EnableSecretRecognition = true
            }).Value).Value.Detections);
    }

    [Fact]
    public void Hmac_pseudonymization_is_stable_for_a_caller_managed_key()
    {
        using var hmac = HmacSha256Operator.Create(new byte[32]).Value;
        var options = new AnonymizationOptions(hmac);
        var sanitizer = new SensitiveDataSanitizer();

        var first = sanitizer.Sanitize("test.user@example.test", anonymizationOptions: options);
        var second = sanitizer.Sanitize("test.user@example.test", anonymizationOptions: options);

        Assert.Equal(first.Value.Text, second.Value.Text);
        Assert.DoesNotContain("test.user", first.Value.Text);
    }

    [Fact]
    public void Authenticated_encryption_round_trips_and_rejects_tampering()
    {
        var detection = Detection.Create(
            EntityType.EmailAddress,
            0,
            22,
            22,
            1,
            "test").Value;
        using var encrypt = AesGcmEncryptionOperator.Create(new byte[32]).Value;
        using var decrypt = AesGcmDecryptionOperator.Create(new byte[32]).Value;

        var encrypted = encrypt.Apply("test.user@example.test", detection);
        var decrypted = decrypt.Apply(encrypted.Value, detection);
        var tampered = encrypted.Value[..^2] + "AA";

        Assert.Equal("test.user@example.test", decrypted.Value);
        Assert.True(decrypt.Apply(tampered, detection).IsFailure);
    }

    [Fact]
    public void Checksum_and_context_recognizers_reject_invalid_siblings()
    {
        var analyzer = new SensitiveDataAnalyzer();
        var result = analyzer.Analyze(
            "routing 021000021 invalid 021000022 NPI 1234567893 DEA AB1234563 wallet 0x52908400098527886E0F7030069857D2E4169EE7");

        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.BankRoutingNumber);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.Npi);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.MedicalLicense);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.CryptocurrencyWallet);
        Assert.DoesNotContain(
            result.Value.Detections,
            detection => detection.EntityType == EntityType.BankRoutingNumber &&
                         detection.Span.Start == "routing 021000021 invalid ".Length);
    }

    [Fact]
    public void Ip_recognizer_rejects_decimal_clinical_values()
    {
        const string text = "2.5 hours and 19.6 events/hour from 192.168.1.10";

        var result = new SensitiveDataAnalyzer().Analyze(text);
        var detection = Assert.Single(
            result.Value.Detections.Where(item => item.EntityType == EntityType.IpAddress));

        Assert.Equal("192.168.1.10", text[detection.Span.Start..detection.Span.End]);
    }

    [Fact]
    public void Local_ner_labels_whole_words_so_no_part_of_a_name_is_left()
    {
        const string text = "Dr. Alastair Kowalczyk reviewed it in Poughkeepsie.";

        var result = new SensitiveDataAnalyzer([LocalNerRecognizer.Create().Value]).Analyze(text);

        Assert.Contains(DetectedText(text, result.Value, EntityType.Person), value => value == "Alastair Kowalczyk");
        Assert.Contains(DetectedText(text, result.Value, EntityType.Location), value => value == "Poughkeepsie");
    }

    [Fact]
    public void Local_ner_spans_cover_whole_words_so_allow_lists_match()
    {
        const string text = "Why does the BFF return 401 after upgrading OnBase? HHSS logs show IdentityServer failed.";
        var options = AnalysisOptions.Create(new AnalysisOptionsConfiguration
        {
            AllowList = ["BFF", "OnBase", "HHSS", "IdentityServer"]
        }).Value;

        var result = new SensitiveDataAnalyzer([LocalNerRecognizer.Create().Value]).Analyze(text, options);

        Assert.Empty(result.Value.Detections);
    }

    [Fact]
    public void Labelled_medical_record_numbers_are_detected_without_their_labels()
    {
        const string text = "MRN 00012345; PatientId=MRN-778812; Medical record number: 556677; Order 12345678";

        var result = new SensitiveDataAnalyzer().Analyze(text);

        Assert.Equal(
            ["00012345", "MRN-778812", "556677"],
            DetectedText(text, result.Value, EntityType.MedicalRecordNumber));
    }

    [Fact]
    public void Hl7_names_patient_identifiers_and_birth_dates_are_detected()
    {
        const string text = "PID|1||778812^^^MRN||KOWALCZYK^ALASTAIR^J||19620314|M";

        var result = new SensitiveDataAnalyzer().Analyze(text);

        Assert.Equal(["KOWALCZYK^ALASTAIR^J"], DetectedText(text, result.Value, EntityType.Person));
        Assert.Equal(["778812"], DetectedText(text, result.Value, EntityType.MedicalRecordNumber));
        Assert.Equal(["19620314"], DetectedText(text, result.Value, EntityType.DateTime));
    }

    [Fact]
    public void Compact_dates_of_birth_need_a_label_and_a_real_date()
    {
        const string text = "DOB=19620314, build 20261015, DOB: 19621399";

        var result = new SensitiveDataAnalyzer().Analyze(text);

        Assert.Equal(["19620314"], DetectedText(text, result.Value, EntityType.DateTime));
    }

    private static List<string> DetectedText(string text, AnalysisResult result, EntityType entityType) =>
        result.Detections
            .Where(detection => detection.EntityType == entityType)
            .Select(detection => text.Substring(detection.Span.Start, detection.Span.Length))
            .ToList();

    [Fact]
    public void Configuration_and_operator_boundaries_fail_closed()
    {
        Assert.True(AnalysisOptions.Create(new AnalysisOptionsConfiguration
        {
            MaximumTextLength = 0
        }).IsFailure);
        Assert.True(AnalysisRequest.Create(null).IsFailure);
        Assert.True(AnalysisRequest.Create(
            "too long",
            AnalysisOptions.Create(new AnalysisOptionsConfiguration
            {
                MaximumTextLength = 2
            }).Value).IsFailure);
        Assert.True(MaskOperator.Create(charactersToMask: -1).IsFailure);
        Assert.True(FixedReplacementOperator.Create(null).IsFailure);
        Assert.True(HmacSha256Operator.Create(new byte[8]).IsFailure);
        Assert.True(AesGcmEncryptionOperator.Create(new byte[8]).IsFailure);
        Assert.True(AesGcmDecryptionOperator.Create(new byte[8]).IsFailure);

        var detection = Detection.Create(EntityType.EmailAddress, 0, 1, 1, 1, "test").Value;
        Assert.Equal("x", KeepOperator.Instance.Apply("x", detection).Value);
        Assert.True(new CustomOperator("failure", (_, _) => throw new InvalidOperationException())
            .Apply("x", detection).IsFailure);

        var hmac = HmacSha256Operator.Create(new byte[32]).Value;
        hmac.Dispose();
        Assert.True(hmac.Apply("x", detection).IsFailure);

        var encrypt = AesGcmEncryptionOperator.Create(new byte[32]).Value;
        encrypt.Dispose();
        Assert.True(encrypt.Apply("x", detection).IsFailure);

        var decrypt = AesGcmDecryptionOperator.Create(new byte[32]).Value;
        Assert.True(decrypt.Apply("invalid", detection).IsFailure);
        decrypt.Dispose();
        Assert.True(decrypt.Apply("invalid", detection).IsFailure);
    }

    [Fact]
    public async Task Streaming_sanitizes_each_line_and_returns_only_counts()
    {
        using var reader = new StringReader("test.user@example.test\n212-555-0198");
        using var writer = new StringWriter();

        var result = await new StreamingSanitizer().SanitizeAsync(reader, writer);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.LinesProcessed);
        Assert.Contains("<EMAIL_ADDRESS>", writer.ToString());
        Assert.Equal(2, result.Value.DetectionCounts.Values.Sum());
    }

    [Fact]
    public void Railway_operations_preserve_failure_and_skip_tap()
    {
        var tapped = false;
        var failure = Result.Failure<int, DeidentifyError>(
                new DeidentifyError(DeidentifyErrorCode.InvalidInput, "Safe failure."))
            .Map(value => value + 1)
            .Tap(_ => tapped = true)
            .MapError(error => error.WithOperation("Test"));

        var combined = new[]
        {
            Result.Success<int, DeidentifyError>(1), failure
        }.Combine();

        Assert.True(failure.IsFailure);
        Assert.False(tapped);
        Assert.Equal("Test", failure.Error.Operation);
        Assert.True(combined.IsFailure);
    }

    [Fact]
    public void Public_results_are_immutable_and_utf16_spans_are_correct()
    {
        var result = new SensitiveDataAnalyzer().Analyze("😀 test.user@example.test");

        Assert.IsType<ImmutableArray<Detection>>(result.Value.Detections);
        Assert.Equal(3, result.Value.Detections[0].Span.Start);
    }

    [Fact]
    public void Custom_extensions_use_deterministic_priority_and_safe_operators()
    {
        var lower = EntityType.Create("LOWER").Value;
        var higher = EntityType.Create("HIGHER").Value;
        var analyzer = new SensitiveDataAnalyzer(
        [
            new StubRecognizer(lower, 1),
            new StubRecognizer(higher, 2)
        ]);

        var analysis = analyzer.Analyze("ABC123");
        var options = new AnonymizationOptions(
            operators: new Dictionary<EntityType, IAnonymizationOperator>
            {
                [higher] = new StubOperator()
            });
        var result = SensitiveDataAnonymizer.Anonymize("ABC123", analysis.Value, options);

        Assert.Equal(higher, Assert.Single(analysis.Value.Detections).EntityType);
        Assert.Equal("SAFE", result.Value.Text);
    }

    [Fact]
    public async Task Requested_cancellation_remains_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new StreamingSanitizer().SanitizeAsync(
                new StringReader("test.user@example.test"),
                new StringWriter(),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Structured_adapters_sanitize_json_csv_dictionaries_and_properties()
    {
        var sanitizer = new StructuredDataSanitizer();
        var dictionary = sanitizer.SanitizeDictionary(new Dictionary<string, string?>
        {
            ["contact"] = "test.user@example.test",
            ["empty"] = null
        });
        var properties = sanitizer.SanitizeProperties(
            new
            {
                Contact = "test.user@example.test",
                Count = 7
            },
            ["Contact"]);
        var json = sanitizer.SanitizeJson(
            """{"contact":"test.user@example.test","count":7,"items":["212-555-0198"]}""");
        await using var input = new MemoryStream(
            Encoding.UTF8.GetBytes("contact,note\r\ntest.user@example.test,\"call, 212-555-0198\"\r\n"));
        await using var output = new MemoryStream();
        var csv = await sanitizer.SanitizeCsvAsync(input, output);

        Assert.Equal("<EMAIL_ADDRESS>", dictionary.Value["contact"]);
        Assert.Equal("<EMAIL_ADDRESS>", properties.Value["Contact"]);
        using var jsonDocument = JsonDocument.Parse(json.Value);
        Assert.Equal("<EMAIL_ADDRESS>", jsonDocument.RootElement.GetProperty("contact").GetString());
        Assert.Equal("<PHONE_NUMBER>", jsonDocument.RootElement.GetProperty("items")[0].GetString());
        Assert.True(csv.IsSuccess);
        Assert.Contains(
            "\"call, <PHONE_NUMBER>\"",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void Bundled_local_ner_detects_names_locations_and_organizations_offline()
    {
        var recognizer = LocalNerRecognizer.Create(minimumConfidence: 0.6);
        Assert.True(recognizer.IsSuccess);
        var options = AnalysisOptions.Create(new AnalysisOptionsConfiguration
        {
            EntityTypes = [EntityType.Person, EntityType.Location, EntityType.Organization]
        }).Value;
        var result = new SensitiveDataAnalyzer([recognizer.Value]).Analyze(
            "Sarah works for Microsoft in London.",
            options);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.Person);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.Location);
        Assert.Contains(result.Value.Detections, detection => detection.EntityType == EntityType.Organization);
    }

    [Fact]
    public void Local_ner_validates_assets_thresholds_and_filters()
    {
        Assert.True(LocalNerRecognizer.Create(minimumConfidence: -1).IsFailure);
        Assert.True(LocalNerRecognizer.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).IsFailure);

        var recognizer = LocalNerRecognizer.Create().Value;
        var empty = recognizer.Analyze(string.Empty, new RecognitionContext(AnalysisOptions.Default));
        var emailOnly = recognizer.Analyze(
            "Sarah",
            new RecognitionContext(AnalysisOptions.Create(new AnalysisOptionsConfiguration
            {
                EntityTypes = [EntityType.EmailAddress]
            }).Value));

        Assert.Empty(empty.Value);
        Assert.Empty(emailOnly.Value);
    }

    [Fact]
    public async Task Streaming_and_structured_failures_return_safe_codes()
    {
        var streaming = new StreamingSanitizer();
        var readFailure = await streaming.SanitizeAsync(new ThrowingReader(), new StringWriter());
        var writeFailure = await streaming.SanitizeAsync(
            new StringReader("test.user@example.test"),
            new ThrowingWriter());
        var structured = new StructuredDataSanitizer();
        var malformedJson = structured.SanitizeJson("{");
        var emptyJson = structured.SanitizeJson(string.Empty);
        var propertyFailure = structured.SanitizeProperties(
            new ThrowingProperty(),
            [nameof(ThrowingProperty.Value)]);
        await using var malformedCsv = new MemoryStream(Encoding.UTF8.GetBytes("\"unterminated"));
        await using var csvOutput = new MemoryStream();
        var csv = await structured.SanitizeCsvAsync(malformedCsv, csvOutput);

        Assert.Equal(DeidentifyErrorCode.FileReadFailed, readFailure.Error.Code);
        Assert.Equal(DeidentifyErrorCode.FileWriteFailed, writeFailure.Error.Code);
        Assert.Equal(DeidentifyErrorCode.InvalidInput, malformedJson.Error.Code);
        Assert.Equal(DeidentifyErrorCode.InvalidInput, emptyJson.Error.Code);
        Assert.Equal(DeidentifyErrorCode.InvalidInput, propertyFailure.Error.Code);
        Assert.Equal(DeidentifyErrorCode.InvalidInput, csv.Error.Code);
    }

    private sealed class StubRecognizer(EntityType entityType, int priority) : IEntityRecognizer
    {
        public string Id => entityType.Value;
        public int Priority => priority;
        public ImmutableArray<EntityType> SupportedEntities => [entityType];

        public Result<ImmutableArray<Detection>, DeidentifyError> Analyze(
            string text,
            RecognitionContext context) =>
            Detection.Create(entityType, 0, text.Length, text.Length, 0.9, Id, priority)
                .Map(detection => ImmutableArray.Create(detection));
    }

    private sealed class StubOperator : IAnonymizationOperator
    {
        public string Id => "stub";

        public Result<string, DeidentifyError> Apply(string value, Detection detection) =>
            Result.Success<string, DeidentifyError>("SAFE");
    }

    private sealed class ThrowingProperty
    {
        public static string Value => throw new InvalidOperationException();
    }

    private sealed class ThrowingReader : StringReader
    {
        public ThrowingReader() : base(string.Empty)
        {
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<string?>(new IOException());
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException());
    }
}
