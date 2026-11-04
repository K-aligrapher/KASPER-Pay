# KASPER — PRD v2.1 status audit (2026-11-04)

✅ done · 🟡 partly done · ❌ not done · ⏸ deferred by owner decision

## Milestones (§20)

| Milestone | Status | Notes |
|---|---|---|
| 1 Foundation & shell | ✅ | WPF + WebView2, tray, Ctrl+Space, notifications, splash, loopback API + token, `kasper` CLI, Ollama, SQLite |
| 2 Runtime & Final Review | ✅ | Durable workflows, approvals bound to action hash, retries, cancel, restart recovery, SSE, History/Approvals |
| 3 Document Studio | ✅ | Convert (router over LibreOffice/Pandoc/PDFsharp), merge, pages, rotate, compress, images→PDF, watermark/page numbers, protect/unlock, OCR, Send To |
| 4 Notion | ✅ | Search, create task (approved, reconcile-before-retry); real-workspace test still to be done by owner (needs your token) |
| 5 Slack | ✅ | Bot token, channel allowlist, read channel, post after approval (no duplicate on retry) |
| 6 Projects & GitHub | ✅ | Projects with folder/GitHub/Slack/Notion links, notes, AI briefing with sources; GitHub read-only summary |
| 7 Connectors, MCP, hardening | 🟡 | MCP ✅, schedules ✅, docs ✅, structured log ✅, architecture tests ✅; installer is `install.ps1` (not MSIX/Velopack); extra connectors ⏸ |

## Functional requirements

| Req | Status | Notes |
|---|---|---|
| FR-100 tray app, start at login (opt-in) | 🟡 | Tray ✅; start-at-login option not built |
| FR-101 hotkey command bar | ✅ | Ctrl+Space opens window and focuses the command bar (not a separate floating bar) |
| FR-102 drag & drop | ✅ | Documents screen; Explorer Send To |
| FR-103 notifications → open view | ✅ | |
| FR-104 Send To | ✅ | `kasper drop` shortcut |
| FR-105 views | ✅ | Home, Approvals, History, Documents, Projects, Integrations, Settings |
| FR-106 light/dark | ✅ | follows Windows |
| FR-107 responsive, progress, cancel | ✅ | |
| FR-001 model config | 🟡 | URL/model ✅; per-task model overrides ❌ |
| FR-002 diagnostics | ✅ | `/ready`, `kasper doctor`, connect-time checks, Notion test |
| FR-003/004/005/006/007 adapters, enable/disable, scoping, MCP, secrets | ✅ | DPAPI for all tokens |
| FR-008 more connectors | ⏸ | owner: later |
| FR-010…016 planning | ✅ | FR-015 fact/interpretation separation is basic (untrusted markers, cited sources) 🟡 |
| FR-017 deterministic quick actions | ✅ | Document Studio jobs bypass the model |
| FR-018 semantic search | ❌ | P2 |
| FR-020…030 tool runtime | ✅ | FR-029 tool health: engines + integrations only 🟡 |
| FR-040…049 lifecycle & Final Review | ✅ | approve/edit/reject, batched reviews, expiry |
| FR-04A per-tool approval policy | ❌ | P2; current policy = ask before save/send/update/delete (owner decision) |
| FR-200/201 convert + routing | ✅ | |
| FR-202 PDF toolkit | 🟡 | all listed except **PDF → images** and image watermarks |
| FR-203 OCR | ✅ | needs Ghostscript + Tesseract |
| FR-204 metadata / flatten / repair | ❌ | P1 |
| FR-205 compare / redact / sign | ❌ | P2 |
| FR-206 staging, never overwrite | ✅ | default save folder is Documents\KASPER (not "next to source": sources are uploads) |
| FR-207 conversion trace | 🟡 | hashes, duration, attempts recorded; engine version not recorded |
| FR-208 lossy warning | ✅ | |
| FR-209 sandboxed engines | 🟡 | child processes + timeouts + kill tree; no memory cap or network isolation |
| FR-210 engine detection | ✅ | hints shown; no one-click install |
| FR-211 preview pane | 🟡 | text preview in review; PDF page rendering ❌ |
| FR-212 presets | ❌ | P1 |
| FR-300/302/303/306 projects, briefing, notes, folder allowlist | ✅ | notes are local only (Notion sync ❌) |
| FR-301 project dashboard | 🟡 | briefing-based; no live PR/task widgets |
| FR-304 project task list | ❌ | tasks go to Notion via chat; no local task list |
| FR-305 scheduled briefings | 🟡 | generic schedules work for any request; not a project-specific option |
| FR-400 Notion | 🟡 | search + create ✅; update page / append ❌ |
| FR-401 Slack | 🟡 | read + post ✅; file upload and thread replies ❌ |
| FR-402 GitHub | 🟡 | repos, PRs, issues, commits ✅; changed files per PR ❌ |
| FR-403/404 sources, no duplicates | ✅ | |
| FR-405 Slack thread → Notion tasks flow | 🟡 | possible through the planner (read → generate → create, each task reviewed); no dedicated guided flow |
| FR-500…507 CLI | ✅ | protect/unlock/ocr/stamp are in the UI only |
| FR-508 tab completion | ❌ | P1 |

## Non-functional

| Area | Status | Notes |
|---|---|---|
| Security NFR-001…012 | ✅ | NFR-009 webhooks: no inbound events exist; NFR-011 Recycle Bin: no delete tools exist |
| Reliability NFR-020…026 | ✅ | |
| Observability NFR-030…033 | 🟡 | JSON-lines log + audit trail; correlation ID not on every log line |
| Performance NFR-040…045 | ✅ | cold start ≈ 2 s |
| Maintainability NFR-050…054 | ✅ | 122 tests; docs in /docs |

## Acceptance criteria (§19)

| # | Status |
|---|---|
| 1 installs and runs (tray, hotkey, notifications) | ✅ |
| 2 Ollama adapter, works offline minus AI | ✅ |
| 3 conversions + PDF toolkit | ✅ (PDF → images missing) |
| 4 staged, committed only after approval, sources untouched | ✅ |
| 5 workflows inspectable | ✅ |
| 6 only registered, validated tools | ✅ |
| 7 survives restart | ✅ |
| 8 bounded retries, engines killed | ✅ |
| 9 approval invalidated on change | ✅ |
| 10 DPAPI secrets, not logged | ✅ |
| 11 tests for approval/rejection/failure/duplicates/paths | ✅ |
| 12 Notion creates exactly one task in a test workspace | 🟡 verified with mocked Notion; needs one real run by the owner |
| 13 `kasper` in a new terminal, shared history | ✅ |
| 14 bunny logo + theme everywhere | ✅ |

## Remaining backlog (suggested order)
1. PDF → images, PDF repair (QPDF), metadata (FR-202/204)
2. Notion update/append, Slack thread replies + file upload (FR-400/401)
3. Local project task list synced with Notion (FR-304)
4. Better PDF → Word engine (pdf2docx) and PDF page preview (FR-211)
5. Start at login, tab completion, per-task model override, presets
