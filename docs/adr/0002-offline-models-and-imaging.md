# ADR 0002: Offline models and flattened document redaction

- Status: accepted
- Date: 2026-08-14

## Decision

Ship a dynamically int8-quantized derivative of the MIT-licensed `dslim/bert-base-NER` ONNX model. It adds English person, location, and organization recognition to both target frameworks without a runtime download.

Use Tesseract behind `IOcrEngine`; keep OCR native dependencies in `Deidentify.Imaging`. Image output is flattened PNG. PDF output rasterizes every page before rebuilding. DICOM applies fo-dicom's Basic Application Confidentiality Profile, removes overlays/graphic annotations, and replaces rendered frames with uncompressed RGB.

## Consequences

- The core package is roughly 104 MiB larger because offline NER is included.
- PDF output is no longer searchable, but hidden text and removable annotations cannot retain sensitive values.
- Tesseract is verified on Windows; Linux OCR requires a different `IOcrEngine` or separately managed compatible native Tesseract installation.
- DICOM compressed transfer syntaxes require a compatible codec before the source frames can be rendered.
