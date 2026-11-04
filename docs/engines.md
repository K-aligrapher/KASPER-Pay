# Document engines

| Engine | Used for | Install | Required? |
|---|---|---|---|
| PDFsharp 6.2.4 (built in, MIT) | merge, pick pages, rotate, images → PDF, watermark, page numbers, lossless compress | — | built in |
| LibreOffice | Office ↔ PDF, ODF, PDF → Word (approximate) | `winget install TheDocumentFoundation.LibreOffice` | for Office conversions |
| pdf2docx (GPL-3.0, free) | PDF → Word with paragraphs, tables, images and columns kept | done by `install.ps1` (Python venv in `%LOCALAPPDATA%\KASPER\engines\py`) | preferred for PDF → Word |
| Pandoc | Markdown, HTML, DOCX, EPUB, RTF, ODT; Word/Markdown/HTML → PowerPoint (one slide per heading); PowerPoint → text formats | `winget install JohnMacFarlane.Pandoc` | for text formats |
| Ghostscript | strong compression; renders PDFs for OCR | `scoop install ghostscript` (not on winget) | for compress (best) and OCR |
| QPDF | lossless optimisation fallback; password protect/unlock | `winget install QPDF.QPDF` | for passwords |
| Tesseract | OCR | `winget install UB-Mannheim.TesseractOCR` | for OCR |

KASPER finds engines on PATH (including PATH changes made after it started), in their usual install folders
(e.g. `C:\Program Files\qpdf 12.4.2\bin`) and in scoop shims. `kasper doctor` shows what it found.

Conversions are routed through installed engines (up to 3 hops, preferring lossless). PDF → Word uses pdf2docx when installed and falls back to LibreOffice.
Scanned PDFs have no text: run OCR first. Word → PowerPoint makes a slide for each heading.
PDFsharp is pinned to 6.2.x: 6.5 requires a license file.
