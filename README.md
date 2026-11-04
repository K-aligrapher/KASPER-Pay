<p align="center"><img src="assets/brand/kasper-logo.svg" width="128" alt="KASPER pixel bunny"></p>

<h1 align="center">KASPER</h1>
<p align="center">Your local-first desktop coworker. It does the work and waits for your approval before anything is saved, sent, updated or deleted.</p>

---

**Status:** Milestones 1–7 built. What is done vs the PRD: [docs/prd-status.md](docs/prd-status.md). Docs: [architecture](docs/architecture.md) · [threat model](docs/threat-model.md) · [connect services](docs/provider-setup.md) · [engines](docs/engines.md).

| Works now | Coming next |
|---|---|
| Desktop app with tray bunny, `Ctrl+Space` hotkey, gold/black UI | Milestone 5: Slack (KASPER bot) |
| `kasper` command in cmd / PowerShell / Windows Terminal | Milestone 6: Projects + GitHub |
| Tasks in plain English: KASPER plans steps, runs them, and **stops for Final Review** before saving | Milestone 7: more connectors, MCP, scheduling |
| Approvals, History (full trace + audit log) and Settings (allowed folders) screens, live updates, notifications | |
| **Documents:** drag & drop to convert (Office, PDF, Markdown, HTML, EPUB…), merge, pick pages, rotate, compress, images → PDF | |
| **Notion:** connect in Integrations; ask *"add a Notion task to …"*; KASPER shows the task before creating it | |

## How Final Review works

1. You ask: *"write a packing list for a weekend trip and save it as packing.md"*.
2. KASPER's local model proposes a plan (write text → prepare file → save file). The server checks it: only registered tools, valid inputs, allowed folders, no invented paths.
3. Safe steps run straight away. Anything that **saves, sends, updates or deletes** is prepared in a private staging folder and waits.
4. You get a notification. The Approvals screen (or `kasper review`) shows exactly what will happen: file name, destination and content. You can **Approve**, **Edit** or **Reject**.
5. Right before acting, KASPER checks that nothing changed since you approved. If something changed, it asks again. Files are never overwritten; a copy gets a "(2)" suffix instead.

Tasks survive restarts. Temporary failures are retried a few times, and every step is recorded in History.

## Install (for daily use)

Needs .NET 10 SDK, Node.js and [Ollama](https://ollama.com) with a model (default `qwen2.5-coder:7b`).

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This publishes KASPER to `%LOCALAPPDATA%\Programs\KASPER`, adds it to your user PATH and creates a Start Menu shortcut. Open a **new** terminal and type:

```text
kasper                 # talk to KASPER (pixel bunny banner + prompt)
kasper ask "..."       # one-shot question or task (follows it and asks for approval)
kasper convert a.docx --to pdf          # convert (any to any, with engines installed)
kasper pdf merge a.pdf b.pdf -n all.pdf  # also: pages -p 1-3 | rotate -d 90 | compress -q low | images
kasper approvals       # what's waiting for you
kasper review [id]     # approve / edit / reject, with a full preview
kasper status [id]     # recent tasks, or one task's step-by-step trace
kasper cancel <id>     # stop a task
kasper open            # open the desktop window
kasper doctor          # check Ollama, database and document engines
kasper --help
```

`kasper` starts the desktop app in the background if it isn't running. Remove everything with `.\uninstall.ps1` (add `-RemoveData` to also delete your local data).

## Settings

Optional `%LOCALAPPDATA%\KASPER\settings.json`:

```json
{ "Ollama": { "Model": "qwen2.5-coder:7b", "BaseUrl": "http://127.0.0.1:11434", "TimeoutSeconds": 300, "KeepAlive": "30m" } }
```

Environment variables work too (`KASPER__Ollama__Model=...`, see [.env.example](.env.example)). Data, logs and the database live in `%LOCALAPPDATA%\KASPER`.

## Develop

```powershell
cd ui; npm install; npm run build; cd ..     # UI is served from the desktop app
dotnet build Kasper.sln
dotnet test                                    # 137 tests, no Ollama or accounts needed
.\src\Kasper.Cli\bin\Debug\net10.0\kasper.exe  # finds the Debug desktop build automatically
```

Logo: edit [assets/brand/kasper-logo.grid.txt](assets/brand/kasper-logo.grid.txt), then run `node assets/brand/render-logo.mjs` to regenerate the SVG, terminal art and `.ico`. The UI and CLI read the grid directly.

## Layout

```text
src/Kasper.Domain          entities and rules (workflow state machine, approval policy)
src/Kasper.Application     interfaces, contracts, options, prompts
src/Kasper.Infrastructure  Ollama, SQLite (EF Core), workflow engine + worker, tools, planner, security
src/Kasper.Api             local HTTP API (127.0.0.1 only, per-launch token)
src/Kasper.Desktop         WPF + WebView2 window, tray, hotkey, notifications
src/Kasper.Cli             the `kasper` command (Spectre.Console)
ui/                        React + TypeScript + Vite
tests/Kasper.UnitTests     domain, adapter and API tests
```

## Security basics

- The API listens on `127.0.0.1` only, needs a random token that changes every launch, and rejects non-loopback `Host` headers.
- The token is kept in `session.json`, which only your Windows user can read, and is never logged.
- Only `user`/`assistant` messages are accepted; KASPER's system prompt can't be replaced by callers.
- Nothing is sent to the cloud: the model runs in Ollama on your machine.

## Document engines

Merge, pick pages, rotate, compress (lossless) and images → PDF are built in. For other conversions install the free engines (`kasper doctor` lists what is missing):

```powershell
winget install TheDocumentFoundation.LibreOffice   # Office <-> PDF, PDF -> Word
winget install JohnMacFarlane.Pandoc               # Markdown, HTML, DOCX, EPUB
winget install ArtifexSoftware.GhostScript         # stronger PDF compression
```

## Connect Notion

1. Create an internal integration at notion.so/profile/integrations and copy its secret.
2. In your task database: ••• → Connections → add the integration.
3. KASPER → Integrations → paste the secret and the database link. The secret is encrypted with Windows DPAPI for your account.
