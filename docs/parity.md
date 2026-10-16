# Presidio parity plan

Reference: Presidio `2.2.364`, commit `779dbd286d5ef4d1fbe2514275fb1bce358f2417`.

| Capability/entity | Deidentify.NET evidence | Status | Gap or difference |
|---|---|---|---|
| Email | `CoreTests.Sanitize_replaces_deterministic_entities_and_resolves_overlaps` | Equivalent for tested corpus | Broader parity corpus pending |
| US phone | `CoreTests.Context_raises_confidence_above_threshold` | Partial | Current pattern is North America focused |
| US SSN / ITIN | Built-in validated patterns | Partial | Full positive/negative corpus pending |
| Credit card | Luhn validation and core test | Equivalent for tested corpus | Card-brand-specific rules are not implemented |
| IP address | `IPAddress.TryParse` validation, `CoreTests.Ip_recognizer_rejects_decimal_clinical_values` | Intentionally different | Uses the .NET parser rather than Presidio pattern logic; IPv4 needs four dotted parts, so clinical decimals such as `2.5` are not matched |
| MAC address | Built-in deterministic pattern | Partial | Corpus pending |
| URL | Built-in HTTP/HTTPS pattern | Partial | Only HTTP and HTTPS |
| IBAN | ISO 13616 mod-97 validation | Partial | Country-length table not yet enforced |
| Date | ISO and US numeric dates through `DateOnly` | Partial | Natural-language dates and times need NER/parser work |
| Context scoring | `CoreTests.Context_raises_confidence_above_threshold` | Intentionally different | Fixed local window; no lemmatizer yet |
| Allowlist / denylist | `CoreTests.Allow_and_deny_lists_are_applied_without_retaining_values` | Equivalent for tested behavior | Denylists are explicitly typed |
| Replace / redact / fixed / mask | Operator implementations | Partial | Full operator corpus pending |
| Pseudonymization | HMAC-SHA256 test | Intentionally different | Safer caller-keyed HMAC instead of ordinary hashing |
| Streaming logs | `CoreTests.Streaming_sanitizes_each_line_and_returns_only_counts` | Supported | Entities split across lines are not detected |
| ABA routing / NPI / DEA | Checksum tests in `CoreTests` | Equivalent for tested corpus | Broader negative corpus pending |
| Bank account / passport / driver license | Context-qualified recognizers | Partial | US-focused and intentionally precision-biased |
| Medical record number | `CoreTests.Labelled_medical_record_numbers_are_detected_without_their_labels` | Extension | No Presidio equivalent; requires an MRN, medical record, patient, encounter or visit label, and replaces only the value |
| HL7 v2 names and PID fields | `CoreTests.Hl7_names_patient_identifiers_and_birth_dates_are_detected` | Extension | No Presidio equivalent; PID-3 first component and PID-7 only, other PID fields rely on the general recognizers |
| Compact date of birth | `CoreTests.Compact_dates_of_birth_need_a_label_and_a_real_date` | Extension | `yyyyMMdd` only after a DOB label, validated as a real date |
| Cryptocurrency wallets | Ethereum, Bech32-like and Base58-like recognizer | Partial | Full address checksum coverage pending |
| Authenticated encryption | AES-GCM round-trip and tamper test | Intentionally different | Uses authenticated encryption rather than Presidio's legacy-compatible operator |
| Structured JSON / CSV / batch | `Structured_adapters_sanitize_json_csv_dictionaries_and_properties` | Supported | CSV processing is record bounded |
| Local NER | Bundled quantized `dslim/bert-base-NER` test on both TFMs; `CoreTests.Local_ner_labels_whole_words_so_no_part_of_a_name_is_left` | Partial | English only; no street-address entity; labels each word by its first WordPiece, matching Presidio's default `first` aggregation |
| OCR and images | Tesseract smoke test and fake-engine pixel test | Supported on Windows | Tesseract native deployment is not verified on Linux |
| PDF | Rasterize/redact/rebuild test | Intentionally different | Flattened output sacrifices searchable text to guarantee removal |
| DICOM | Basic Profile metadata and pixel replacement test | Partial | Compressed transfer syntaxes need an external compatible codec |
| Multilingual | Deterministic locale-neutral recognizers only | Partial | Bundled NER model is English |

No claim of full Presidio parity is made.
