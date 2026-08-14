# Bundled model provenance

## English named-entity recognition

| Field | Value |
|---|---|
| Upstream model | `dslim/bert-base-NER` |
| Pinned upstream commit | `d1a3e8f13f8c3566299d95fcfc9a8d2382a9affc` |
| License | MIT |
| Training/evaluation | English CoNLL-2003; upstream reports test F1 about 0.91 |
| Source graph | `onnx/model.onnx` from the pinned upstream repository |
| Transformation | Dynamic signed-int8 weight quantization with ONNX Runtime 1.22.1; no retraining |
| Bundled graph SHA-256 | `0AB59D5D0485917166B03FD44FF145DDD41652DD609BD007AB2EB5D79B9604F0` |
| Bundled graph size | 108,537,863 bytes |
| Vocabulary SHA-256 | `EEAA9875B23B04B4C54EF759D03DB9D1BA1554838F8FB26C5D96FA551DF93D02` |
| Runtime | ONNX Runtime 1.29.0 and Microsoft.ML.Tokenizers 2.0.0 |
| Entities | `PERSON`, `LOCATION`, `ORGANIZATION` |
| Known limits | English news-domain training, 510 content-token chunks, no address-specific entity, model bias and missed entities remain possible |

The model is package content and is never downloaded at runtime.
The ONNX graph is stored in the repository through Git LFS.

## OCR

TesseractOCR 5.5.2 uses Tesseract 5.5.1 under Apache-2.0. English language data comes from `Tesseract.Data.English` 4.0.0 under Apache-2.0. The current managed adapter is verified on Windows; other platforms can supply an `IOcrEngine`.
