# ADR 0001: Local functional core

- Status: accepted
- Date: 2026-08-14
- Presidio baseline: `2.2.364` / `779dbd286d5ef4d1fbe2514275fb1bce358f2417`

## Decision

Deidentify.NET is an in-process, host-neutral library targeting `net8.0` and `net10.0`. The core package depends only on CSharpFunctionalExtensions 3.6.0. Domain values are immutable and validated through `Result<T, DeidentifyError>`. Recognizers and anonymization operators are the only public polymorphic extension points.

Deterministic recognition is implemented before local NER. Regexes use `NonBacktracking` when supported and a 100 ms timeout otherwise. Overlaps resolve by confidence, length, recognizer priority, then source order. Replacements run from the end of the input.

Imaging remains a separate package so native OCR and document dependencies cannot leak into text-only deployments.

## Consequences

- Production text and log sanitization has no service, Python, Docker, model download, or network dependency.
- Failures expose stable codes and safe messages, never input or exception messages.
- Line streaming is bounded but cannot detect entities split across lines.
- Current parity is deliberately partial; NER, OCR, PDF, DICOM, multilingual packs, self-contained host matrices, and SonarQube verification remain later phases.
