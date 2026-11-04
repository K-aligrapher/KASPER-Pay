# Document engines

| Engine | Used for | Install | Required? |
|---|---|---|---|
| PDFsharp 6.2.4 (built in, MIT) | merge, pick pages, rotate, images → PDF, watermark, page numbers, lossless compress | — | built in |
| LibreOffice | Office ↔ PDF, ODF, PDF → Word (approximate) | `winget install TheDocumentFoundation.LibreOffice` | for Office conversions |
| Pandoc | Markdown, HTML, DOCX, EPUB, RTF, ODT | `winget install JohnMacFarlane.Pandoc` | for text formats |
| Ghostscript | strong compression; renders PDFs for OCR | `scoop install ghostscript` (not on winget) | for compress (best) and OCR |
| QPDF | lossless optimisation fallback; password protect/unlock | `winget install QPDF.QPDF` | for passwords |
| Tesseract | OCR | `winget install UB-Mannheim.TesseractOCR` | for OCR |

KASPER finds engines on PATH (including PATH changes made after it started), in their usual install folders
(e.g. `C:\Program Files\qpdf 12.4.2\bin`) and in scoop shims. `kasper doctor` shows what it found.

Conversions are routed through installed engines (up to 3 hops, preferring lossless). PDF → Word is approximate by
nature; better engines (pdf2docx, Docling) can be added as extra edges in `DocumentConverter`.
PDFsharp is pinned to 6.2.x: 6.5 requires a license file.
