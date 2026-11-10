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

## KASPER Pay (PayPal AI Hackathon 2026)

KASPER can now **buy from a demo catalog and send invoices through PayPal, but never spends money without your approval.**
Ask *"buy me a mechanical keyboard under $100"* or *"invoice alex@example.com $200 for logo design"*.

```text
You ask → local model (Qwen via Ollama) proposes a product id / invoice details
        → code checks the catalog price and your spending rules (Blocked if broken)
        → Final Review in KASPER (approval is bound to a fingerprint of item, amount, currency, recipient)
        → PayPal Orders v2 order is created and the PayPal page opens
        → you approve again inside PayPal → KASPER polls, captures once, verifies amount and currency
        → recorded in Payments / History
```

**Safety model**
- The model only names a catalog product id (or invoice details). It never decides prices or totals, never calls PayPal, never sees credentials.
- Guardrails run in code, both when the request is made and again just before PayPal is called: per-purchase limit, daily limit, duplicate-purchase window, optional merchant and recipient allow-lists, USD only. A broken rule shows as **Blocked** with its name, and nothing is created in PayPal.
- Change the amount, item, currency or recipient after approving and the approval is invalid. A mismatching capture is marked Failed.
- Replays are safe: one payment row per step, PayPal request ids on create and capture, and an invoice is sent at most once.
- Sandbox only. No real money.

**Data boundary (exact).** Everything stays on this machine (model, chat, files, history, database) **except**, when you approve a purchase or invoice, what PayPal needs: item name, SKU, amount, currency and, for invoices, the recipient email and description. Your PayPal Client ID and Secret are read from user secrets or environment variables, are only sent to PayPal in the auth header, and are never given to the model, the UI, logs or the repo.

**Setup in 5 steps**
1. Install .NET 10 SDK, Node.js and [Ollama](https://ollama.com) (`ollama pull qwen3:8b`).
2. Create a PayPal sandbox app and a sandbox buyer account at developer.paypal.com.
3. Set the app credentials (see `.env.example`):
   ```powershell
   dotnet user-secrets set "PayPal:ClientId" "<sandbox client id>" --project src/Kasper.Api
   dotnet user-secrets set "PayPal:ClientSecret" "<sandbox secret>" --project src/Kasper.Api
   dotnet user-secrets set "PayPal:Environment" "sandbox" --project src/Kasper.Api
   ```
4. Build and run: `cd ui; npm ci; npm run build; cd ..; dotnet run --project src/Kasper.Desktop` (or use `install.ps1`).
5. Ask KASPER to buy something, approve it, then log in as the sandbox buyer in the PayPal page that opens.

Limits are editable on the **Payments** screen. More: [demo script and Devpost text](docs/paypal-demo.md).

## How Final Review works

1. You ask: *"write a packing list for a weekend trip and save it as packing.md"*.
2. KASPER's local model proposes a plan (write text → prepare file → save file). The server checks it: only registered tools, valid inputs, allowed folders, no invented paths.
3. Safe steps run straight away. Anything that **saves, sends, updates or deletes** is prepared in a private staging folder and waits.
4. You get a notification. The Approvals screen (or `kasper review`) shows exactly what will happen: file name, destination and content. You can **Approve**, **Edit** or **Reject**.
5. Right before acting, KASPER checks that nothing changed since you approved. If something changed, it asks again. Files are never overwritten; a copy gets a "(2)" suffix instead.

Tasks survive restarts. Temporary failures are retried a few times, and every step is recorded in History.

## Install (for daily use)

Needs .NET 10 SDK, Node.js and [Ollama](https://ollama.com) with a model (default `qwen3:8b`).

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
{ "Ollama": { "Model": "qwen3:8b", "BaseUrl": "http://127.0.0.1:11434", "TimeoutSeconds": 300, "KeepAlive": "30m" } }
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
- The AI model runs in Ollama on your machine and never sees credentials. Only what a PayPal order or invoice needs leaves the machine (see "KASPER Pay" above).

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
