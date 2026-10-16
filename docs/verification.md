# Verification report

Verified on Windows 11 with .NET SDK 10.0.301 on 2026-08-14.

| Check | Command | Outcome |
|---|---|---|
| Locked restore | `dotnet restore Deidentify.sln --locked-mode` | Passed |
| Release build | `dotnet build Deidentify.sln -c Release --no-incremental` | Passed, 0 warnings, 0 errors |
| Tests | `dotnet test Deidentify.sln -c Release --no-build` | Passed on `net8.0` and `net10.0`: 24 core and 8 imaging tests per framework |
| Packages | `dotnet pack src\Deidentify\Deidentify.csproj -c Release --no-build -o artifacts\packages` and imaging equivalent | Passed; core package 64.3 MiB, imaging package 16.0 MiB |
| Package consumption | Restore and run a clean `net8.0` console against only `artifacts\packages` plus NuGet.org | Passed; NER model, tessdata, and Windows OCR native binaries copied transitively |
| CLI offline smoke | `dotnet run --project samples\Deidentify.Cli -c Release -f net8.0 -- "Sarah uses test.user@example.test in London."` | `<PERSON> uses <EMAIL_ADDRESS> in <LOCATION>.` |
| Vulnerabilities | `dotnet list Deidentify.sln package --vulnerable --include-transitive` | No vulnerable packages reported by configured sources |
| Self-contained hosts | `powershell -File eng\verify-consumers.ps1` | `win-x64` ASP.NET Core and WPF published and ran successfully; `linux-x64` ASP.NET Core published with model assets |
| SonarQube | `powershell -File eng\sonar.ps1` | Quality gate passed; coverage 86.8%, new-code coverage 97.7%, duplication 1.3%, reliability A, security A, 0 major-or-higher open issues, 0 hotspots to review |

## Imaging evidence

- Tesseract OCR extracted words and bounding boxes from a synthetic image on Windows.
- Image redaction mapped OCR spans to black pixels and emitted flattened PNG.
- PDF redaction rasterized and rebuilt a synthetic PDF.
- DICOM redaction applied the Basic Application Confidentiality Profile and replaced source pixels with uncompressed RGB.

## Verified limitations

- The bundled NER model is English and news-domain trained; multilingual NER is not claimed.
- The bundled Tesseract native adapter is verified on Windows. Linux OCR is available through a caller-supplied `IOcrEngine`, not the bundled adapter.
- Compressed DICOM transfer syntaxes require a compatible fo-dicom codec before frame rendering.
- PDF output is intentionally flattened and not searchable.
- Automated detection remains probabilistic and cannot guarantee removal of all PII or PHI.
