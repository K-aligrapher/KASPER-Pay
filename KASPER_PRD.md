# KASPER — Personal Desktop Assistant & Coworker
## Product Requirements Document (PRD)

**Version:** 2.1
**Status:** Draft — revised from *Engineering Command Center PRD v1.0* (kept unchanged alongside this file for reference)
**Project type:** Local-first Windows desktop assistant with an approval-gated AI workflow runtime
**Primary stack:** C# / .NET 10, WPF + WebView2 desktop shell, React + TypeScript (Vite) UI, SQLite, Ollama
**Cost target:** $0 for local development, inference, and document conversion (free/open-source engines only). Provider plans, API limits and workspace permissions still apply.

---

## 0. What changed from v1.0

| # | Change | Why |
|---|---|---|
| 1 | Renamed and reframed from an engineering-only "Command Center" to **KASPER**, a general desktop assistant and coworker. | The goal is a personal coworker, not only an engineering dashboard. |
| 2 | Added a **Desktop Shell**: tray icon, global hotkey, command bar, Windows notifications, drag-and-drop. | v1.0 only described a browser dashboard and an API. |
| 3 | Added **Document Studio**: any-to-any document conversion plus PDF tools (merge, split, compress, OCR, and more), similar to iLovePDF but fully local. | New core feature. v1.0 had no file handling. |
| 4 | Added **Projects**: a project links local folders, Notion pages/databases, Slack channels and GitHub repos, with tasks, status and briefings. | The "handle my projects" requirement. |
| 5 | Generalized approvals into a **Final Review gate**: every workflow that changes anything (external writes, sent messages, overwritten or deleted files) stops one step before finishing and waits for your approval. Outputs are staged first and only committed after approval. | The "wait for my final approval" requirement. v1.0 gated external writes only. |
| 6 | Slack can now **send** messages and Notion can **update** pages (both approval-gated). v1.0 was draft-only and create-only. | Needed to act as a coworker. |
| 7 | Added **local file access scoping** (allowlisted folders) and file-safety rules: never overwrite originals, staging folder, recycle bin instead of permanent delete. | New risk surface from file features. |
| 8 | Merged API and Worker into a **single desktop process** (hosted services) with a loopback API protected by a per-launch token. Kept the layer boundaries so they can be split later. | A desktop app should be a single process. |
| 9 | Reordered milestones: Document Studio comes right after the runtime. It needs no credentials and is useful on day one. | Faster usable value; lower risk. |
| 10 | Added an engine dependency list, licensing notes, a conversion matrix, and model defaults that match the installed hardware. | Implementation readiness. |
| 11 | Added a connector framework for future services (Google Drive/Calendar/Gmail, etc.) through MCP or native adapters. | "Connecting services" beyond the first three. |
| 12 | Added **brand identity**: a pixel-art bunny logo and a gold-and-black theme, used in both the desktop UI and the terminal (§24). | Owner request (v2.1). |
| 13 | Added the **`kasper` command-line interface**: typing `kasper` in cmd, PowerShell or Windows Terminal opens the assistant, with the same features and the same Final Review as the desktop app (§8.9). | Owner request (v2.1). |

Everything else from v1.0 is kept: safety principles, the tool contract, the state machine, the threat model, observability and the testing strategy. Some parts are extended.

---

## 1. Executive Summary

KASPER is a local-first Windows desktop assistant that works alongside you. You give it a task in plain language, or pick a ready-made action. KASPER makes a plan, runs the safe steps itself, and **stops for your final approval before anything is committed**. That includes sending a message, updating Notion, or saving or replacing a file.

It has four capabilities built on one runtime:

1. **Document Studio:** convert documents between formats and run PDF tools locally (merge, split, compress, OCR, watermark, page operations, and more). No uploads to third-party sites.
2. **Projects:** one place for each project's folders, tasks, notes, Notion pages, Slack channels and GitHub repos, with summaries and status briefings.
3. **Connected services:** read context from Notion, Slack and GitHub, and act on them (create or update Notion tasks, post Slack messages) after approval. Further services connect through a connector framework and MCP.
4. **AI workflow runtime:** a durable, observable engine. The local LLM (Ollama) proposes structured plans. Deterministic code validates, authorizes, executes and records them.

KASPER is not an unrestricted autonomous agent. **Model output is a proposal, never authorization.** Only registered tools can run, and the human approves before anything leaves the staging area.

## 2. Problem Statement

Daily work is spread across chat, documents, project folders, task trackers and source control. Small, repetitive jobs eat time and attention, for example:
- Converting a DOCX to PDF, merging scans, or compressing a file before emailing it, often through upload-based websites that see your private files.
- Copying decisions from Slack into Notion tasks.
- Writing a status update from scattered sources.

Generic chatbots do not keep workflow state, cannot safely touch files or services, and offer no checkpoint before acting. Unconstrained agents can send the wrong message, overwrite files or duplicate tasks.

KASPER addresses this with a desktop assistant that is:
- **Local-first:** inference, conversion and app state stay on your machine.
- **Approval-gated:** nothing is sent, published, overwritten or deleted without your final approval.
- **Context-aware:** it understands your projects and connected tools.
- **Durable and auditable:** every workflow can be resumed and inspected.
- **Extensible:** new tools, converters and services plug in without rewriting the core.

## 3. Goals

### 3.1 Product goals
- One desktop entry point (command bar plus main window) for documents, projects and connected services.
- Any-to-any document conversion for common office, PDF, image, text/markup and ebook formats, plus a full PDF toolkit.
- A project hub that links local folders, Notion, Slack and GitHub.
- Summaries of discussions and documents that extract decisions and action items, with source references.
- Creating and updating Notion tasks, and sending Slack messages, after approval.
- A **Final Review** screen that shows exactly what will happen (file preview, message text, Notion fields, diff) with Approve / Edit / Reject.
- A full trace of every workflow.

### 3.2 Engineering goals
- Modular monolith with clean layer boundaries (Domain / Application / Infrastructure / Hosts).
- Workflows resumable after app restart.
- All model-generated structured output validated against schemas.
- Workflow, conversion and policy logic testable without live services or heavy engines.
- No secrets in source control or logs.
- No paid LLM APIs or paid conversion services.

## 4. Non-Goals (first release)

- Replacing Slack, Notion, GitHub or a full office suite.
- Training or fine-tuning an LLM.
- Arbitrary shell/PowerShell/SQL commands generated by the model. There is no `execute_shell` tool.
- Autonomous merges, deployments, payments or destructive actions.
- Perfect layout fidelity for every conversion. Some conversions (such as PDF → DOCX) are lossy, and KASPER says so.
- Removing passwords or DRM from documents the user does not own or have rights to.
- Multi-user or team accounts. The first release has a single local operator.
- Cross-platform support (macOS/Linux). Windows only for v1, but the core stays platform-neutral.
- Ingesting every message, file or repo by default. Access is scoped to resources you explicitly allow.

## 5. Target User and Primary Use Cases

### Primary user
A developer, student or knowledge worker who juggles documents, several projects and team tools. They want repetitive work done for them while keeping the final say.

### Core use cases
1. **Convert:** drop files on KASPER (or right-click → "Convert with KASPER") and pick a target format. Preview the result, then approve saving it.
2. **PDF toolkit:** merge, split, compress, rotate, reorder or delete pages, OCR scans, add a watermark or page numbers, protect with a password.
3. **Batch:** "Convert every DOCX in `Reports/` to PDF and merge them into `Q3-pack.pdf`."
4. **Project status:** "What's the status of Project X?" gives a summary from linked Notion tasks, recent Slack threads, open GitHub PRs and recently changed local files.
5. **Discussion → tasks:** summarize a Slack thread, extract action items, draft Notion tasks, approve, then create them.
6. **Communicate:** "Post the Q3 summary to #team with the PDF attached." KASPER shows the exact message and attachment for Final Review, then sends after approval.
7. **Chained work:** convert → summarize → update Notion → notify Slack, as one workflow with one Final Review covering all committing steps.
8. **Inspect:** view any workflow's plan, steps, inputs/outputs, errors, retries and approvals.
9. **Extend:** register a new tool, converter or MCP server without changing the orchestration core.

## 6. Product Principles

- **Local-first:** Ollama for inference, local engines for conversion, SQLite for state.
- **Human has the final say:** stage first, commit after approval. A model statement is never treated as approval.
- **Least privilege:** minimum provider scopes, allowlisted folders, channels, pages and repos.
- **Explicit execution:** only registered tools, with schema-validated inputs.
- **Non-destructive by default:** never overwrite source files. Outputs go to staging. Deletes go to the Recycle Bin.
- **Traceability:** stable IDs and recorded outcomes for every workflow, step and approval.
- **Resilience:** state survives restarts. Transient failures are retried with limits.
- **Provider isolation:** provider and engine details stay inside adapters.
- **Incremental complexity:** direct adapters and an in-process worker first. Add MCP and extra infrastructure when justified.

## 7. Scope and Release Plan

### Phase 0 — Foundation and desktop shell
- .NET solution and project boundaries; React + Vite UI.
- WPF + WebView2 shell: main window, tray icon, global hotkey (default `Ctrl+Space`, configurable), Windows toast notifications.
- In-process ASP.NET Core API on `127.0.0.1` (random port) with a per-launch bearer token.
- Ollama adapter and health checks; SQLite with EF Core migrations; settings screen; basic tests.

### Phase 1 — AI workflow runtime and Final Review
- Tool registry with typed schemas; structured plans; plan validation; policy evaluation.
- Workflow state machine with persistent steps; hosted background worker.
- Tool invocation with timeouts, cancellation, bounded retries and error classification.
- Staging area plus **Final Review** gate (approval bound to action hash).
- Audit events, execution traces, mock tools.

### Phase 2 — Document Studio
- Conversion engine registry and format-routing graph (see §9).
- PDF toolkit operations.
- Drag-and-drop UI, batch jobs, output preview, Explorer "Send to KASPER" integration.
- Engine detection with install guidance for missing engines.

### Phase 3 — Notion
- Notion integration shared with explicitly chosen pages/data sources.
- Search/read authorized content; draft, create and update tasks (approval-gated); store page IDs/URLs.

### Phase 4 — Slack
- Slack app with minimum scopes; read allowlisted channels/threads.
- Summarize threads; draft and **send** messages and files (approval-gated).
- Event triggers only after signature verification, replay protection and permission checks.

### Phase 5 — Projects and GitHub
- Project entity linking folders, Notion targets, Slack channels and GitHub repos.
- Project dashboard, status briefing, tasks view, notes.
- GitHub read-only: repos, issues, PRs, changed files, rate-limit handling.

### Phase 6 — Connectors, MCP and hardening
- MCP client with server allowlist and per-tool permissions.
- Optional connectors (Google Drive, Gmail drafts, Google Calendar, Microsoft 365) through MCP or native adapters.
- Scheduled workflows (for example a daily briefing). These still stop at Final Review.
- Threat-model review, recovery tests, operational docs, installer.

## 8. Functional Requirements

Priority: **P0** = required for first usable release; **P1** = important follow-up; **P2** = optional.

### 8.1 Desktop shell and UX
- **FR-100 (P0):** KASPER runs as a Windows desktop app with a main window and a system-tray icon. It can start minimized at login (opt-in).
- **FR-101 (P0):** A global hotkey opens a command bar for natural-language requests and quick actions.
- **FR-102 (P0):** Users can drag and drop files onto the window or command bar to start a document workflow.
- **FR-103 (P0):** KASPER shows a Windows notification when a workflow needs approval, finishes or fails. Clicking it opens the relevant Final Review or trace.
- **FR-104 (P1):** Explorer integration through a "Send to → KASPER" shortcut (no shell-extension DLL in v1).
- **FR-105 (P0):** The main window has these views: Home (command + recent activity), Documents, Projects, Approvals, Workflows/History, Integrations, Settings.
- **FR-106 (P1):** Light/dark theme following Windows settings.
- **FR-107 (P0):** The UI stays responsive during inference, conversion and network calls, and shows progress and a cancel action for long jobs.

### 8.2 Integrations and configuration
- **FR-001 (P0):** Configurable Ollama base URL, default model and per-task model overrides (planning vs. summarizing).
- **FR-002 (P1):** Connection diagnostics for each provider and conversion engine.
- **FR-003 (P1):** Notion, Slack and GitHub through isolated adapters.
- **FR-004 (P1):** Enable or disable each integration and engine.
- **FR-005 (P0):** Access is scoped to explicitly configured folders, workspaces, channels, pages/data sources and repos.
- **FR-006 (P1):** MCP tools through an adapter layer; MCP is optional for any integration.
- **FR-007 (P0):** Credentials are stored with Windows DPAPI (current-user scope) or the Windows Credential Manager, or read from environment variables / dotnet user-secrets during development. They are never committed and never stored as plaintext in SQLite.
- **FR-008 (P2):** Additional connectors (Google Drive, Gmail drafts, Google Calendar, Microsoft 365) through the connector framework.

### 8.3 AI interaction and planning
- **FR-010 (P0):** Accept a user request and respond using the configured Ollama model.
- **FR-011 (P0):** The model sees only tools that are enabled and permitted for the current workflow.
- **FR-012 (P0):** Tool-using requests produce a structured plan (see §16).
- **FR-013 (P0):** Model output is validated against typed schemas. Malformed or unsupported actions are rejected, with one bounded repair attempt.
- **FR-014 (P1):** Retrieved context carries source identifiers and URLs or file paths.
- **FR-015 (P1):** The UI and stored results separate observed facts, model interpretations and proposed actions.
- **FR-016 (P0):** Bounded model/tool iterations and a configurable maximum step count per workflow.
- **FR-017 (P1):** Common requests ("convert X to Y", "merge these PDFs") map to deterministic quick actions without needing the LLM. The LLM is used only when intent is ambiguous or multi-step.
- **FR-018 (P2):** Optional semantic search over authorized content, with deletion and refresh behavior defined before indexing is enabled.

### 8.4 Tool registry and execution runtime
- **FR-020 (P0):** Every tool has a stable ID and version, a description, a typed input schema, an effect class (`Read`, `LocalWrite`, `ExternalWrite`, `Destructive`), required permission, timeout/retry policy, idempotency behavior and sensitivity classification.
- **FR-021 (P0):** Only registered tools can be invoked.
- **FR-022 (P0):** Every invocation is validated (schema, scope, policy) before execution.
- **FR-023 (P0):** Cancellation and timeouts for all tools, including external engine processes. Cancelling kills the process tree.
- **FR-024 (P0):** Retries are bounded and only for classified retryable errors.
- **FR-025 (P0):** Writes support idempotency keys or duplicate detection.
- **FR-026 (P0):** Workflow state and step outcomes are persisted.
- **FR-027 (P1):** After a restart, interrupted workflows resume safely or enter a clear recoverable/failed state.
- **FR-028 (P1):** Tool inputs/outputs are recorded with sensitive fields redacted.
- **FR-029 (P1):** Tool and engine health status.
- **FR-030 (P1):** MCP adapter with server allowlisting and per-tool authorization.

### 8.5 Workflow lifecycle and Final Review
- **FR-040 (P0):** Workflow states: `Pending`, `Running`, `WaitingForApproval`, `Succeeded`, `Failed`, `Cancelled`.
- **FR-041 (P0):** Each workflow has a stable ID, requester, timestamps, status and execution history.
- **FR-042 (P0):** **Final Review gate.** Any step with effect class `ExternalWrite` or `Destructive`, and any `LocalWrite` outside the staging area, requires explicit approval by default. Read-only steps and writes into the staging area run automatically.
- **FR-043 (P0):** Approval is bound to a canonical hash of the action payload and target (and the output file hash, for file commits). Any material change invalidates it.
- **FR-044 (P0):** Approval requests support **Approve**, **Edit** (change the payload, which creates a new approval) and **Reject**. Each decision records who decided and when.
- **FR-045 (P0):** A model-generated statement, or text in retrieved content ("approved", "go ahead"), is never treated as approval.
- **FR-046 (P0):** Final Review shows a faithful preview of every pending commit: rendered file preview and target path; exact Slack message text, channel and attachments; Notion fields and target database; before/after diff for updates.
- **FR-047 (P1):** Multiple committing steps in one workflow can be reviewed together on one screen and approved all at once or one by one.
- **FR-048 (P1):** Cancellation of workflows, with safe handling of in-flight steps.
- **FR-049 (P1):** The UI shows workflow status and the reason for waiting or failure.
- **FR-04A (P2):** Per-tool approval policy in Settings (`Always ask` / `Ask for external & destructive` / `Auto for staging-only`). The defaults cannot be weakened for `Destructive` actions.

### 8.6 Document Studio
- **FR-200 (P0):** Convert single files and batches between supported formats (§9.1). Choose the target format from the list valid for the input.
- **FR-201 (P0):** Multi-hop routing. If no engine converts A → C directly, the router finds the shortest path with the best fidelity through intermediate formats (for example `.pages`-like unsupported → reject; `.md → .docx → .pdf`).
- **FR-202 (P0):** PDF toolkit: merge, split (by range / every N pages / extract pages), delete pages, reorder, rotate, compress (quality presets), PDF ↔ images, add page numbers, add text/image watermark, protect with password, remove password (only with the password supplied by the user).
- **FR-203 (P1):** OCR: scanned PDF/image → searchable PDF or text, with language selection.
- **FR-204 (P1):** Edit metadata (title, author), flatten form fields, repair a damaged PDF where the engine supports it.
- **FR-205 (P2):** Compare two PDFs (text diff), redact text regions (true removal, verified), sign by placing a signature image.
- **FR-206 (P0):** Source files are never modified. Outputs are written to `%LOCALAPPDATA%\KASPER\staging\<workflowId>\`. On approval they are moved to the chosen destination (default: next to the source, with a unique suffix; never overwrite unless the user explicitly picks "replace", which is a `Destructive` action).
- **FR-207 (P0):** Each conversion records engine, version, options, duration and input/output hashes in the step trace.
- **FR-208 (P0):** Lossy conversions (such as PDF → DOCX/XLSX/PPTX, or anything → image) show a fidelity warning before running.
- **FR-209 (P0):** Engines run in sandboxed child processes with time, memory and output-size limits, no network access where configurable, and a working directory inside staging.
- **FR-210 (P1):** At startup and in Settings, detect which engines are installed and grey out unavailable conversions, with a one-click `winget` install suggestion.
- **FR-211 (P1):** A preview pane renders PDF pages and images in the app before approval.
- **FR-212 (P1):** Save conversion presets (for example "Compress for email: 150 dpi, medium quality").

### 8.7 Projects
- **FR-300 (P1):** Create a project with a name, description, local folder(s), and optionally Notion page/database, Slack channel(s) and GitHub repo(s).
- **FR-301 (P1):** The project dashboard shows linked tasks, recent Slack activity, open PRs/issues, recently modified files and recent KASPER workflows.
- **FR-302 (P1):** "Project briefing" produces an on-demand summary (what changed, blockers, next actions) with source references.
- **FR-303 (P1):** Project notes stored locally (Markdown) and optionally synced to a Notion page (approval-gated).
- **FR-304 (P1):** Tasks: a local task list per project, two-way linked with a Notion database when configured. Creating or updating Notion tasks is approval-gated.
- **FR-305 (P2):** Scheduled project briefings (daily/weekly). The output is staged and, if it would be posted anywhere, waits at Final Review.
- **FR-306 (P1):** Project folder access follows the folder allowlist. Linking a project folder adds it to the allowlist after confirmation.

### 8.8 Connected services (initial)
- **FR-400 (P1):** Notion: search/read shared pages and data sources; create tasks; update task properties/status; append content to a page. Writes are approval-gated.
- **FR-401 (P1):** Slack: read allowlisted channels and threads; summarize; draft messages; **send messages and upload files after approval**; reply in a thread after approval.
- **FR-402 (P1):** GitHub: list/read repos, issues, PRs and changed files. Read-only in v1.
- **FR-403 (P1):** Each candidate action item carries source references and never invents an owner when none is stated.
- **FR-404 (P1):** Duplicate prevention for task creation and message sending (idempotency key plus stored execution record).
- **FR-405 (P1):** End-to-end: a selected Slack thread becomes extracted action items, then Notion task previews, then Final Review, then created tasks with URLs stored, then (optionally) a Slack reply linking the tasks after a second approval in the same review.

### 8.9 Command-line interface (`kasper`)
- **FR-500 (P0):** A `kasper` command is available on `PATH` in cmd, PowerShell and Windows Terminal after install.
- **FR-501 (P0):** Typing `kasper` with no arguments opens **interactive mode**: the pixel bunny banner (§24), connection status, then a `kasper ›` prompt that accepts natural-language requests, like the desktop command bar. `exit` or `Ctrl+C` leaves.
- **FR-502 (P0):** One-shot commands for scripting and quick use:

  | Command | Does |
  |---|---|
  | `kasper ask "<request>"` | Run any natural-language request as a workflow |
  | `kasper convert <files...> --to <fmt> [--out <dir>]` | Convert files |
  | `kasper pdf <merge\|split\|compress\|rotate\|reorder\|delete-pages\|watermark\|page-numbers\|protect\|unlock\|ocr> ...` | PDF toolkit |
  | `kasper approvals` | List pending Final Reviews |
  | `kasper review <id>` | Show the preview, then **[A]pprove / [E]dit / [R]eject** |
  | `kasper status [workflowId]` | Workflow list or one workflow's trace |
  | `kasper projects`, `kasper project <name> [briefing\|tasks]` | Projects |
  | `kasper doctor` | Check Ollama, engines, integrations, and print install hints |
  | `kasper open` | Open the desktop window |

- **FR-503 (P0):** The CLI is a thin client of the same local API. It shares workflows, approvals and history with the desktop app, so you can start something in the terminal and approve it in the window, or the other way round.
- **FR-504 (P0):** If the KASPER background process is not running, the CLI starts it headless (tray only) and connects. The CLI reads the per-launch API token from `%LOCALAPPDATA%\KASPER\session.json`, which is readable by the current Windows user only.
- **FR-505 (P0):** Final Review works in the terminal: it shows the target, a summary, the file path / message text / Notion fields, and a diff where relevant. Approval needs an interactive keypress. **No `--yes` or auto-approve flag exists for `ExternalWrite` or `Destructive` actions.** In a non-interactive shell, the workflow waits and prints the `kasper review <id>` command to run.
- **FR-506 (P0):** Live progress (spinner/progress bar) for long jobs; `Ctrl+C` asks whether to cancel the workflow or just detach.
- **FR-507 (P1):** `--json` output for scripting; `--no-logo`; respects `NO_COLOR`. Falls back to a plain ASCII bunny when the terminal cannot show colors.
- **FR-508 (P1):** Tab completion for PowerShell (`kasper completion powershell`).

## 9. Document Studio Details

### 9.1 Conversion matrix (v1 target)

| From \ To | PDF | DOCX | XLSX | PPTX | ODT/ODS/ODP | HTML | MD | TXT | RTF | EPUB | PNG/JPG/WEBP | TIFF |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **PDF** | toolkit | ⚠ LO/pdf2docx | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ text | ✓ PdfPig | – | – | ✓ PDFium | ✓ |
| **DOCX / DOC** | ✓ LO | – | – | – | ✓ LO | ✓ Pandoc | ✓ Pandoc | ✓ | ✓ LO | ✓ Pandoc | via PDF | via PDF |
| **XLSX / XLS / CSV** | ✓ LO | – | ✓ (CSV↔XLSX) | – | ✓ LO | ✓ LO | ✓ (table) | ✓ CSV | – | – | via PDF | via PDF |
| **PPTX / PPT** | ✓ LO | – | – | – | ✓ LO | ✓ LO | – | ✓ text | – | – | ✓ via PDF | via PDF |
| **ODT/ODS/ODP** | ✓ LO | ✓ LO | ✓ LO | ✓ LO | – | ✓ LO | ✓ via DOCX | ✓ | ✓ LO | ✓ via DOCX | via PDF | via PDF |
| **HTML** | ✓ WebView2 print | ✓ Pandoc | – | – | ✓ Pandoc | – | ✓ Pandoc | ✓ | ✓ Pandoc | ✓ Pandoc | via PDF | – |
| **Markdown** | ✓ via HTML | ✓ Pandoc | – | ✓ Pandoc | ✓ Pandoc | ✓ Pandoc | – | ✓ | ✓ Pandoc | ✓ Pandoc | via PDF | – |
| **TXT / RTF** | ✓ LO | ✓ LO | – | – | ✓ LO | ✓ | ✓ | ✓ | ✓ | ✓ Pandoc | via PDF | – |
| **EPUB** | ✓ via HTML | ✓ Pandoc | – | – | ✓ Pandoc | ✓ Pandoc | ✓ Pandoc | ✓ | – | – | – | – |
| **Images** (PNG/JPG/WEBP/BMP/GIF/TIFF/HEIC*) | ✓ (one or many → PDF) | – | – | – | – | – | – | ✓ OCR | – | – | ✓ Magick.NET | ✓ |

✓ = supported, ⚠ = supported with fidelity warning, – = not offered. "via X" = multi-hop. *HEIC depends on codec availability.
Audio/video (via ffmpeg, already installed) is a **P2** extension: MP4 → MP3, format changes, compress. It uses the same engine contract.

### 9.2 Engines

| Engine | Used for | License | Install |
|---|---|---|---|
| LibreOffice (headless `soffice`) | Office ↔ PDF/ODF, PDF → office (via import filters) | MPL 2.0 | `winget install TheDocumentFoundation.LibreOffice` |
| Pandoc | Markdown/HTML/DOCX/EPUB/RTF/ODT conversions | GPL (separate process, OK) | `winget install JohnMacFarlane.Pandoc` |
| PDFsharp (NuGet) | Merge, split, rotate, reorder, page numbers, watermark, encrypt | MIT | NuGet |
| UglyToad.PdfPig (NuGet) | PDF text extraction | Apache 2.0 | NuGet |
| PDFium via PDFtoImage/Docnet (NuGet) | PDF → image rendering, previews | BSD/MIT | NuGet |
| QPDF | Repair, linearize, decrypt with user password, lossless optimize | Apache 2.0 | `winget install QPDF.QPDF` |
| Ghostscript | Lossy PDF compression presets | **AGPL** — OK as a separately invoked local tool for personal use; review before any distribution | `winget install ArtifexSoftware.GhostScript` |
| Tesseract OCR | OCR → searchable PDF/text | Apache 2.0 | `winget install UB-Mannheim.TesseractOCR` |
| Magick.NET (NuGet) | Image format conversion, resize | Apache 2.0 | NuGet |
| ffmpeg (P2) | Audio/video | LGPL/GPL | already installed |

Rules:
- Do **not** use iText or other AGPL/commercial PDF libraries **in-process**.
- Each engine sits behind `IConversionEngine` and declares its capability edges (`from`, `to`, `fidelity`, `options schema`). The router builds a graph from the installed engines.
- Engine processes get timeouts and are killed on cancel. LibreOffice runs with an isolated `-env:UserInstallation` profile per job to avoid lock conflicts.

## 10. Non-Functional Requirements

### Security and privacy
- **NFR-001:** The API binds to `127.0.0.1` only, on a random port, with a per-launch random bearer token passed to WebView2. Requests without the token are rejected. CORS is locked to the app origin.
- **NFR-002:** Ollama, the API and the UI are never exposed to the network without deliberate configuration and authentication.
- **NFR-003:** Never log tokens, authorization headers, passwords, document passwords or secret values.
- **NFR-004:** Least-privilege provider scopes; allowlisted folders, channels, pages and repos.
- **NFR-005:** Treat Slack messages, Notion pages, GitHub content, **document contents** and MCP output as untrusted data. They cannot change policy, add tools or authorize actions.
- **NFR-006:** Validate all inputs; enforce request and file size limits; parameterized DB access; reject path traversal and symlink/junction escapes from allowlisted folders.
- **NFR-007:** Approval is re-checked server-side immediately before every commit.
- **NFR-008:** Data minimization and retention: staging is cleaned after commit/reject (default 7-day retention for failed workflows, configurable); provider content is cached only as long as needed.
- **NFR-009:** Verify webhook signatures and timestamps and protect against replay before processing inbound events.
- **NFR-010:** MCP servers and connectors must be explicitly configured and trusted. Never auto-run servers discovered in content.
- **NFR-011:** Deletes go to the Windows Recycle Bin, never permanent delete, in v1.
- **NFR-012:** Document passwords supplied by the user are held in memory for the job only and never persisted.

### Reliability
- **NFR-020:** Persist workflow state before any side effect.
- **NFR-021:** Timeouts and cancellation for all external calls and engine processes.
- **NFR-022:** Bounded exponential backoff with jitter for retryable errors.
- **NFR-023:** Classify failures as transient, permanent, authorization, validation, rate-limit or engine-missing.
- **NFR-024:** Steps are idempotent on replay or require explicit manual recovery.
- **NFR-025:** Versioned, tested DB migrations.
- **NFR-026:** File commits are atomic: write to a temp name in the destination folder, then rename.

### Observability
- **NFR-030:** Structured logs (Serilog, rolling files in `%LOCALAPPDATA%\KASPER\logs`) with correlation, workflow and step IDs.
- **NFR-031:** Record workflow start/end, transitions, tool/engine name, duration, retry count and error category.
- **NFR-032:** Prompts, provider content and document text stay out of routine logs. Detailed traces are opt-in and redacted.
- **NFR-033:** Local health/readiness endpoints with no secrets or private content.

### Performance and resources
- **NFR-040:** The UI stays responsive. All heavy work runs on background workers.
- **NFR-041:** Long workflows run outside the HTTP request lifetime. The UI polls or receives updates (SSE).
- **NFR-042:** Configurable concurrency limits (default: 1 LLM job, 2 conversion jobs).
- **NFR-043:** Single-user, low-volume workloads. Set scale targets from measurements.
- **NFR-044:** Model size and context length are configurable. Defaults target ~8 GB VRAM (RTX 5050 class).
- **NFR-045:** App cold start under 3 s on the dev laptop, excluding model load.

### Maintainability
- **NFR-050:** Domain and application logic are testable without live services or engines.
- **NFR-051:** Providers and engines sit behind interfaces.
- **NFR-052:** Request/response DTOs with validation for all API contracts.
- **NFR-053:** Environment-specific, documented configuration.
- **NFR-054:** Docs for setup, troubleshooting, architecture, security and adding a tool/engine/connector.

## 11. Architecture and Project Structure

A modular monolith in **one desktop process**. The WPF shell hosts WebView2 (React UI) and starts the ASP.NET Core API and the background worker as hosted services in the same process. The layers stay separate so the API or worker can move to its own process later.

```text
KASPER/
├── src/
│   ├── Kasper.Desktop/            # WPF host: window, tray, hotkey, toasts, WebView2, composition root
│   ├── Kasper.Cli/                # `kasper.exe`: System.CommandLine + Spectre.Console, thin API client
│   ├── Kasper.Api/                # ASP.NET Core endpoints, middleware, token auth (class library hosted by Desktop)
│   ├── Kasper.Application/
│   │   ├── Abstractions/          # IChatModel, IToolRegistry, IToolExecutor, IConversionEngine, ...
│   │   ├── Workflows/             # orchestrator, state machine, worker loop
│   │   ├── Planning/              # plan schema, validation, repair
│   │   ├── Approvals/             # Final Review, action hashing
│   │   ├── Policies/              # effect classes, scopes, folder allowlist
│   │   ├── Documents/             # conversion router, PDF operations, presets
│   │   ├── Projects/
│   │   └── DTOs/
│   ├── Kasper.Domain/             # entities, enums, events, rules (no external deps)
│   └── Kasper.Infrastructure/
│       ├── Persistence/           # EF Core + SQLite
│       ├── Ollama/
│       ├── Engines/               # LibreOffice, Pandoc, PDFsharp, PdfPig, PDFium, QPDF, Ghostscript, Tesseract, Magick
│       ├── Notion/
│       ├── Slack/
│       ├── GitHub/
│       ├── Mcp/
│       ├── Security/              # DPAPI secret store, redaction
│       ├── FileSystem/            # staging, atomic commit, Recycle Bin
│       └── Observability/
├── ui/                            # React + TypeScript + Vite (built into Desktop's wwwroot)
├── tests/
│   ├── Kasper.UnitTests/
│   ├── Kasper.IntegrationTests/   # mocked HTTP, SQLite, real engines when present (skippable)
│   └── Kasper.ArchitectureTests/  # enforces dependency direction
├── assets/brand/                  # logo grid (source of truth), SVG, ANSI render, icons
├── docs/
│   ├── architecture.md
│   ├── threat-model.md
│   ├── provider-setup.md
│   ├── engines.md
│   └── decisions/
├── .env.example
├── .gitignore
├── README.md
└── Kasper.sln
```

**Desktop shell decision (ADR-001):** WPF + WebView2 instead of Electron or Tauri. It is pure .NET (no Rust or bundled Chromium), WebView2 ships with Windows 11, and it gives native tray, hotkey and notification APIs. Trade-off: Windows only, which matches v1 scope.

### Dependency direction
- Domain → nothing.
- Application → Domain.
- Infrastructure → Application, Domain.
- Api → Application (and Infrastructure only for wiring).
- Desktop → everything (composition root).
- Cli → Api contracts (DTOs) only, over HTTP. It holds no business logic and no provider secrets.
- UI → Api only. It never holds provider secrets.

### Core abstractions
`IChatModel`, `IToolRegistry`, `IToolExecutor`, `IWorkflowStore`, `IApprovalService`, `IAuditSink`, `IIntegrationHealthCheck`, `IClock`, plus:
- `IConversionEngine`: declares capability edges; converts in a sandboxed process.
- `IConversionRouter`: finds a conversion path through the installed engines.
- `IPdfOperations`: page-level PDF toolkit.
- `IFileScope`: validates paths against the allowlist and resolves staging/commit.
- `ISecretStore`: DPAPI / Credential Manager.
- `INotifier`: desktop notifications.
- `IProjectContextProvider`: gathers context for a project from the linked sources.

## 12. Core Domain Model

**Workflow:** `Id`, `CreatedBy`, `RequestText`, `ProjectId?`, `Status`, `CreatedAtUtc`, `UpdatedAtUtc`, `StartedAtUtc`, `CompletedAtUtc`, `CorrelationId`, `FailureCode`, `FailureSummary`.

**WorkflowStep:** `Id`, `WorkflowId`, `Sequence`, `ToolId`/`StepType`, `EffectClass`, `Status`, `InputJson` (redacted), `OutputJson` (redacted), `StartedAtUtc`, `CompletedAtUtc`, `AttemptCount`, `IdempotencyKey`, `ErrorCode`, `ErrorSummary`.

**Approval:** `Id`, `WorkflowId`, `WorkflowStepId`, `ActionHash`, `TargetDescription`, `PreviewJson`, `Status` (`Pending`, `Approved`, `Rejected`, `Expired`, `Invalidated`), `RequestedAtUtc`, `DecidedAtUtc`, `DecidedBy`.

**Artifact** *(new)*: `Id`, `WorkflowId`, `StepId`, `Kind` (file/message/page), `StagingPath`, `CommittedPath`/`ExternalUrl`, `Sha256`, `SizeBytes`, `MimeType`, `CreatedAtUtc`, `CommittedAtUtc`.

**Project** *(new)*: `Id`, `Name`, `Description`, `Status`, `CreatedAtUtc`, `UpdatedAtUtc`; children `ProjectLink` (`Type`: Folder/NotionPage/NotionDatabase/SlackChannel/GitHubRepo, `ExternalId`, `DisplayName`, `Url`/`Path`), `ProjectTask` (`Title`, `Status`, `Due`, `NotionPageId?`, `SourceRef?`), `ProjectNote`.

**AuditEvent:** `Id`, `WorkflowId`, `StepId`, `Actor`, `EventType`, `TimestampUtc`, `Target`, `Outcome`, `MetadataJson` (redacted).

**IntegrationConnection:** provider, non-secret config (allowlisted IDs), enabled flag, status, timestamps. Secrets live in `ISecretStore`.

**AllowedFolder** *(new)*: `Path`, `Access` (Read / ReadWrite), `AddedAtUtc`.

## 13. Workflow State Machine

```text
Pending            -> Running
Running            -> WaitingForApproval
WaitingForApproval -> Running        (approved; revalidate action hash, scope, idempotency key)
WaitingForApproval -> Running        (edited; new approval created, then waits again)
WaitingForApproval -> Cancelled      (rejected/cancelled; staging cleaned)
Running            -> Succeeded
Running            -> Failed
Running            -> Cancelled
Failed             -> Pending        (explicit retry, when safe)
```

Rules:
- Persist each transition in a transaction.
- **Stage-then-commit:** all steps that produce outputs run first and write only to staging. The workflow then enters `WaitingForApproval` with one Final Review covering every commit action. Only approved commits run.
- Never mark `Succeeded` until all approved commits are confirmed (file exists with the expected hash; external ID returned).
- Before each commit, re-check the action hash, approval status, policy, target scope and idempotency key.
- If a provider times out after a write, reconcile (look up by idempotency marker) before retrying.
- Approvals expire after a configurable period (default 24 h). Expired approvals need re-approval.

## 14. API Sketch (loopback only, token required)

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/health`, `/ready` | Process health / readiness (engines, Ollama, DB) |
| POST | `/api/ai/chat` | Dev-only direct model test |
| POST | `/api/workflows` | Create a workflow from a request (text and/or file refs) |
| GET | `/api/workflows`, `/api/workflows/{id}` | List / details + trace |
| GET | `/api/workflows/{id}/events` | SSE stream of status updates |
| POST | `/api/workflows/{id}/cancel` | Cancel |
| GET | `/api/approvals` | Pending Final Reviews |
| POST | `/api/approvals/{id}/decision` | Approve / reject / edit (with payload) |
| GET | `/api/artifacts/{id}/preview` | Rendered preview of a staged artifact |
| GET | `/api/documents/capabilities` | Installed engines + valid conversions |
| POST | `/api/documents/convert` | Start a conversion workflow |
| POST | `/api/documents/pdf/{operation}` | Start a PDF-toolkit workflow |
| GET/POST/PUT | `/api/projects`, `/api/projects/{id}` | Project CRUD and links |
| POST | `/api/projects/{id}/briefing` | Generate a briefing |
| GET | `/api/integrations` | Integration status |
| POST | `/api/integrations/{provider}/test` | Test connection |
| GET/PUT | `/api/settings`, `/api/settings/folders` | Settings and folder allowlist |

## 15. Tool Contract

Every tool declares: stable ID and version; description; typed input schema; **effect class** (`Read`, `LocalWrite`, `ExternalWrite`, `Destructive`); required permission; allowed targets; timeout and retry policy; idempotency behavior; data sensitivity; whether approval is required (derived from the effect class, and can only be made stricter).

Initial tools:

| Tool | Effect | Approval |
|---|---|---|
| `files.list` / `files.read_metadata` | Read | No |
| `documents.convert` (to staging) | LocalWrite (staging) | No |
| `pdf.merge` / `split` / `compress` / `rotate` / `reorder` / `delete_pages` / `watermark` / `page_numbers` / `protect` / `unlock` / `ocr` (to staging) | LocalWrite (staging) | No |
| `files.commit` (staging → destination, new file) | LocalWrite | **Yes** |
| `files.replace` / `files.recycle` | Destructive | **Yes, always** |
| `documents.extract_text` / `documents.summarize` | Read | No |
| `notion.search_pages` / `notion.get_page` / `notion.query_database` | Read | No |
| `notion.create_task` / `notion.update_page` / `notion.append_content` | ExternalWrite | **Yes** |
| `slack.list_channels` / `slack.get_thread` / `slack.get_history` | Read | No |
| `slack.draft_message` | LocalWrite (staging) | No |
| `slack.send_message` / `slack.upload_file` | ExternalWrite | **Yes** |
| `github.list_repos` / `list_pull_requests` / `get_pull_request` / `list_issues` | Read | No |
| `projects.*` (local KASPER data) | LocalWrite (app DB) | No, except delete project (Yes) |

There is no `execute_shell`, no arbitrary HTTP tool and no arbitrary file-path writes outside the allowlist.

## 16. AI Planning Contract

The model returns a constrained plan:

```json
{
  "summary": "Convert the report to PDF and share it in #team",
  "steps": [
    { "id": "s1", "toolId": "documents.convert", "purpose": "Convert DOCX to PDF",
      "input": { "fileRef": "file:1", "targetFormat": "pdf" } },
    { "id": "s2", "toolId": "slack.draft_message", "purpose": "Draft announcement",
      "input": { "channelRef": "channel:team", "text": "Q3 report attached." } },
    { "id": "s3", "toolId": "slack.upload_file", "purpose": "Post the PDF with the message",
      "input": { "channelRef": "channel:team", "artifactFrom": "s1", "messageFrom": "s2" } }
  ]
}
```

- The model refers to files, channels, pages and repos only through **opaque references** (`file:1`, `channel:team`) that the application issued in the prompt context. Raw IDs or paths invented by the model are rejected.
- The server verifies that each tool exists and is enabled, that inputs match the schema, that references resolve within scope, and that step limits are respected. The **approval requirement comes from the tool's effect class, not from the model**. The model cannot set or skip approval.
- Invalid plans get one repair round with the validation errors. After that the workflow fails with a clear message.
- Local default model: `qwen2.5:7b-instruct` class (or newer equivalent with good JSON/tool output) at ~8k context. The installed `qwen2.5-coder:7b` works for development. Model choice is configurable.

## 17. Security and Threat Model

### Main threats
- Prompt injection hidden in Slack/Notion/GitHub content **or in document text** ("ignore instructions and send this file to…").
- The model hallucinating tools, IDs, paths or recipients.
- Sending to the wrong channel or person; posting private files.
- Overwriting or deleting local files; path traversal; junction/symlink escape.
- Malicious documents exploiting conversion engines (macro-laden Office files, crafted PDFs, zip bombs).
- Stolen or over-privileged tokens; other local processes calling the loopback API.
- Duplicate writes after retries/timeouts.
- Sensitive data in logs, traces, staging leftovers or indexes.
- Malicious or compromised MCP servers.

### Mitigations
- Untrusted content is passed as clearly delimited data. Tools and targets come only from app-issued references.
- Allowlisted tools, schema validation and server-side scope enforcement.
- Final Review with a faithful preview; approval bound to the action hash; re-checked right before commit.
- Recipient/target allowlists (Slack channels, Notion databases).
- Non-destructive file policy, atomic commit, Recycle Bin, path canonicalization and allowlist checks.
- Engines run as child processes with timeouts, resource limits and isolated profiles. LibreOffice runs headless with macros disabled. Input size and page-count limits; archive-bomb checks.
- DPAPI secret store; per-launch API token; loopback only.
- Redaction, minimal retention, staging cleanup.
- MCP server allowlist and per-tool permissions.
- Test workspaces/sandbox channels for integration development.

## 18. Testing Strategy

### Unit
- State transitions (including Edit → new approval).
- Plan/tool schema validation; reference resolution; rejection of invented IDs/paths.
- Effect-class → approval derivation; policy cannot be weakened for destructive tools.
- Approval invalidation on payload/target/file-hash change.
- Idempotency keys; duplicate detection.
- Retry classification; step-count limits.
- Conversion router path-finding (fake engines).
- Folder allowlist and path-traversal/junction checks.
- Redaction; audit events.

### Integration
- Ollama adapter against a deterministic HTTP stub (plus optional live-model smoke tests).
- SQLite persistence and migrations.
- Provider adapters against mocked HTTP (timeouts, 429s, malformed JSON, 401/403).
- Real engine conversions with sample fixtures. Tests are skipped automatically when an engine is not installed.
- Workflow recovery after simulated restart.
- Commit only after approval; reject leaves no output outside staging.

### End-to-end
- Drop DOCX → PDF staged → Final Review → approve → exactly one file at the destination, source unchanged.
- Merge + compress PDFs → reject → nothing committed, staging cleaned.
- Slack thread (mocked) → action items → Notion task previews → approve → exactly one mocked task per item.
- Slack send after approval; edited message invalidates the old approval.
- Provider timeout after write → reconciliation, no duplicate.
- Prompt injection inside a document cannot add a send step or change the target.

CI never depends on live Slack/Notion/GitHub accounts.

## 19. Acceptance Criteria — First Useful Release (v0.1)

1. KASPER installs and runs on Windows 11 as a desktop app with tray icon, hotkey command bar and notifications, with documented setup.
2. Ollama is called through a typed .NET adapter. The app works (minus AI features) when Ollama is offline.
3. Users can convert files between the formats in §9.1 for which engines are installed, and use the P0 PDF toolkit operations.
4. Every output is staged and committed only after Final Review approval. Source files are never modified.
5. Users can create and inspect a persisted workflow and its trace.
6. Only registered, schema-validated tools run. Invented references are rejected.
7. Workflow state survives app restart.
8. Timeouts and failures are recorded and handled without unbounded retries. Engine processes are killed on cancel/timeout.
9. Approval is invalidated when payload, target or artifact hash changes.
10. Secrets are stored with DPAPI, not committed and not logged.
11. Unit and integration tests cover approval, rejection, failure, duplicate-action and path-safety scenarios.
12. At least one real provider (Notion) can read authorized data and create exactly one task after approval in a test workspace.
13. Typing `kasper` in a new cmd or PowerShell window shows the bunny banner and accepts requests. Conversions and Final Review work from the terminal, and they share history with the desktop app.
14. The desktop UI, tray icon, app icon and CLI all use the bunny logo and the theme in §24.

v0.2 adds Slack send, Projects and GitHub. v0.3 adds MCP connectors and scheduling.

## 20. Development Plan

### Milestone 1 — Foundation and shell
Solution and projects; WPF + WebView2 shell with tray, hotkey and toast; in-process API with token; React skeleton with the gold/black theme and bunny logo; `kasper` CLI with banner, interactive prompt, `doctor` and `ask`; Ollama adapter; SQLite + migrations; health endpoints; settings; tests.
**Exit:** the app launches, the UI loads, and "Reply with READY" round-trips through Ollama from both the window and `kasper ask`.

### Milestone 2 — Durable runtime and Final Review
Workflow/step/approval/artifact/audit entities; state machine; hosted worker; tool registry; mock tools; retries, cancellation, idempotency; staging; Final Review UI; SSE updates.
**Exit:** a mock multi-step workflow stages output, waits for approval, survives restart, commits after approval, and records an accurate trace.

### Milestone 3 — Document Studio
Engine detection; PDFsharp/PdfPig/PDFium/Magick.NET in-process ops; LibreOffice, Pandoc, QPDF, Ghostscript, Tesseract adapters; router; batch; preview; presets; Send-To shortcut.
**Exit:** DOCX→PDF, PDF merge/split/compress, images→PDF and PDF→images work end-to-end through Final Review.

### Milestone 4 — Notion
DPAPI secret entry; Notion adapter; search/read; task create/update with preview, action hash, idempotency and reconciliation.
**Exit:** an approved draft creates exactly one task and stores its URL.

### Milestone 5 — Slack
Slack app setup; allowlisted channel reads; thread summarization with source refs; drafts; send/upload after approval.
**Exit:** a selected Slack thread becomes reviewed, approved Notion tasks plus an approved Slack reply.

### Milestone 6 — Projects and GitHub
Project entities and dashboard; linking; briefings; tasks synced with Notion; GitHub read-only adapter.
**Exit:** "status of Project X" returns a sourced briefing across folder, Notion, Slack and GitHub.

### Milestone 7 — Connectors, MCP and hardening
MCP client with allowlist; optional Google/Microsoft connectors; scheduled workflows; threat-model review; installer (MSIX or Velopack); operational docs.
**Exit:** a new MCP tool can be registered and used without orchestration changes.

## 21. Setup

### Prerequisites (already present on the dev laptop)
- .NET SDK 10.0.402 ✓, Node.js 24 ✓, Ollama 0.20.5 ✓ (update recommended), ffmpeg ✓, Git ✓, WebView2 runtime (ships with Windows 11).

### Install document engines

```powershell
winget install TheDocumentFoundation.LibreOffice
winget install JohnMacFarlane.Pandoc
winget install QPDF.QPDF
winget install ArtifexSoftware.GhostScript
winget install UB-Mannheim.TesseractOCR
```

KASPER also finds engines through configured paths when they are not on `PATH`.

### Model

```powershell
ollama pull qwen2.5:7b-instruct   # general planning/summarizing default
# qwen2.5-coder:7b (already installed) is fine for development
```

### Scaffold (from repo root)

```powershell
dotnet new sln -n Kasper
dotnet new classlib -n Kasper.Domain          -o src/Kasper.Domain
dotnet new classlib -n Kasper.Application     -o src/Kasper.Application
dotnet new classlib -n Kasper.Infrastructure  -o src/Kasper.Infrastructure
dotnet new classlib -n Kasper.Api             -o src/Kasper.Api
dotnet new wpf      -n Kasper.Desktop         -o src/Kasper.Desktop
dotnet new console  -n Kasper.Cli             -o src/Kasper.Cli   # AssemblyName = kasper
dotnet new xunit    -n Kasper.UnitTests       -o tests/Kasper.UnitTests
dotnet new xunit    -n Kasper.IntegrationTests -o tests/Kasper.IntegrationTests
dotnet new xunit    -n Kasper.ArchitectureTests -o tests/Kasper.ArchitectureTests
npm create vite@latest ui -- --template react-ts
```

(Project references follow the dependency direction in §11.)

## 22. Risks and Open Questions

- **Conversion fidelity:** PDF → Office is inherently lossy. Set expectations in the UI and test with real sample files.
- **Engine footprint:** LibreOffice is ~350 MB. Consider making engines optional, on-demand installs.
- **Local model quality:** 7B models may produce weak plans. Deterministic quick actions (FR-017), strict validation and a repair round reduce reliance on the model.
- **Provider plans/permissions:** confirm Slack app installation rights and Notion integration sharing for your workspaces.
- **Slack sending identity:** decide between bot token (posts as "KASPER") and user token (posts as you). The default is bot token, which is safer.
- **Duplicate writes:** reconcile before retrying after ambiguous timeouts.
- **MCP trust:** every server/tool needs explicit review.
- **Privacy:** local inference does not make third-party data safe automatically. Minimize retention and protect `%LOCALAPPDATA%\KASPER`.
- **Laptop resources:** limit concurrent LLM and LibreOffice jobs; watch VRAM, RAM and temperatures.
- **Ghostscript AGPL:** fine for personal local use as an external tool; revisit before distributing KASPER.

**Decisions from the owner (2026-10-30):**
1. **Notion comes first** after Document Studio, then Slack.
2. **Slack posts as a KASPER bot**, not as the user.
3. **Final Review is required before sending, updating, deleting and saving.** Saving any output outside staging needs approval (FR-042, as specified).
4. **Other services (Google, Microsoft 365, etc.) are deferred** and will be decided later. The connector framework stays in Milestone 7.

## 23. Definition of Done

A feature is complete only when:
- Behavior and failure cases are documented.
- Inputs and model outputs are validated.
- Permissions and folder scope are enforced server-side.
- Committing actions go through Final Review where required.
- Workflow state, artifacts and audit events are persisted.
- Unit and integration tests pass.
- Logs redact secrets and avoid unnecessary content retention.
- Setup and recovery steps are documented.
- It works without paid LLM APIs or paid conversion services.

## 24. Brand and Theme

### 24.1 Logo — pixel bunny
- A 16 × 15 pixel-art bunny face: gold head, dark-gold inner ears and cheeks, black eyes with cream highlights, a small nose and an "ω" mouth, with a black outline.
- **Source of truth:** [`assets/brand/kasper-logo.grid.txt`](assets/brand/kasper-logo.grid.txt). Every render is generated from it by `node assets/brand/render-logo.mjs`, so the logo is never redrawn by hand:
  - `kasper-logo.svg`: UI, docs, README.
  - `kasper-logo.ansi`: the terminal banner (truecolor half-block characters, 16 columns × 8 lines).
  - `.ico` (16/24/32/48/256) for the app, tray and `kasper.exe`. Generated in Milestone 1.
- Always scale by whole numbers with nearest-neighbour (`image-rendering: pixelated`, `shape-rendering: crispEdges`) so pixels stay sharp.
- On dark backgrounds the black outline disappears and the bunny reads as a gold silhouette; on light backgrounds the outline shows. Both are intended.
- **Wordmark:** "KASPER" in a pixel font (Silkscreen or Press Start 2P, both under the SIL Open Font License), gold on black or black on cream.
- **Bunny states** (P1, same grid with small edits): idle, thinking (eyes look up, animated dots), waiting for you (ears up, gold pulse), done (happy eyes), error (X eyes). These show in the tray icon, the command bar and the CLI banner.

### 24.2 Colour tokens

| Token | Dark (default) | Light | Use |
|---|---|---|---|
| `bg` | `#0D0D0D` | `#FFFBEA` | App background |
| `surface` | `#161616` | `#FFFFFF` | Cards, panels |
| `surface-2` | `#1F1F1F` | `#FFF3C4` | Hover, inputs |
| `border` | `#2A2A2A` | `#E8DDB5` | Dividers |
| `text` | `#F2F2F2` | `#0D0D0D` | Body text |
| `text-muted` | `#A3A3A3` | `#5C5646` | Secondary text |
| `gold` (primary) | `#F5B800` | `#F5B800` (fills only) | Primary buttons, focus ring, logo, "waiting for you" |
| `gold-hover` / `gold-pressed` | `#FFC933` / `#D99F00` | same | Button states |
| `gold-text` | `#F5B800` | `#8A6400` | Gold-coloured text and links (dark gold on light keeps contrast) |
| `on-gold` | `#0D0D0D` | `#0D0D0D` | Text on gold fills (never white on gold) |
| `success` | `#3FB950` | `#1A7F37` | Approved, done |
| `danger` | `#F85149` | `#CF222E` | Reject, destructive, errors |
| `warning` | `#FF8A3D` | `#BC4C00` | Lossy conversion, warnings. Orange, not yellow, so it is never confused with the brand gold. |
| `info` | `#58A6FF` | `#0969DA` | Links, neutral info |

Rules:
- Gold is an **accent**, not a fill for large areas: logo, primary action, focus, and anything that needs the user (Final Review badges and buttons). Large surfaces stay black/grey.
- The **Approve** button is gold with black text. **Reject** is outlined red. Destructive approvals use a red fill and require typing or holding to confirm.
- Text contrast meets WCAG AA in both themes (gold `#F5B800` on `#0D0D0D` is ≈ 11:1; never put gold text on cream or white).
- **Typography:** pixel font only for the wordmark and large headings; Inter for UI text; Cascadia Mono / JetBrains Mono for code, paths and the CLI.
- The theme follows Windows light/dark mode by default. Dark is the primary design.

### 24.3 Terminal theme
- Banner: bunny (left) + "KASPER" + version + status line (Ollama ✓, engines 5/5, Notion ✓…) on the right.
- Prompt: `kasper ›` with the chevron in gold.
- Gold for headings, IDs and anything waiting for approval; green/red/orange as in §24.2.
- Works in Windows Terminal, cmd and PowerShell 5.1/7 (virtual-terminal sequences on Windows 10+). Falls back to a plain ASCII bunny with `NO_COLOR` or a non-VT console.

---

**Recommended first implementation:** Milestone 1 (shell + Ollama) → Milestone 2 (durable runtime + Final Review with mock tools) → Milestone 3 (Document Studio). This delivers a useful, credential-free assistant first. Connect real provider credentials only after the approval gate passes its acceptance tests.
