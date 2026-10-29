<p align="center"><img src="assets/brand/kasper-logo.svg" width="128" alt="KASPER pixel bunny"></p>

<h1 align="center">KASPER</h1>
<p align="center">Your local-first desktop coworker. It does the work and waits for your approval before anything is saved, sent, updated or deleted.</p>

---

**Status:** Milestone 1 (foundation) done. See [KASPER_PRD.md](KASPER_PRD.md) for the full plan.

| Works now | Coming next |
|---|---|
| Desktop app with tray bunny, `Ctrl+Space` hotkey, gold/black UI | Milestone 2: workflows + **Final Review** approvals |
| `kasper` command in cmd / PowerShell / Windows Terminal | Milestone 3: Document Studio (convert anything, PDF toolkit) |
| Chat with your local Ollama model (window + terminal) | Milestone 4: Notion · 5: Slack (bot) · 6: Projects + GitHub |
| SQLite database, local API locked to your user | Milestone 7: more connectors, MCP, scheduling |

## Install (for daily use)

Needs .NET 10 SDK, Node.js and [Ollama](https://ollama.com) with a model (default `qwen2.5-coder:7b`).

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This publishes KASPER to `%LOCALAPPDATA%\Programs\KASPER`, adds it to your user PATH and creates a Start Menu shortcut. Open a **new** terminal and type:

```text
kasper                 # talk to KASPER (pixel bunny banner + prompt)
kasper ask "..."       # one-shot question
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
dotnet test                                    # 37 tests, no Ollama or accounts needed
.\src\Kasper.Cli\bin\Debug\net10.0\kasper.exe  # finds the Debug desktop build automatically
```

Logo: edit [assets/brand/kasper-logo.grid.txt](assets/brand/kasper-logo.grid.txt), then run `node assets/brand/render-logo.mjs` to regenerate the SVG, terminal art and `.ico`. The UI and CLI read the grid directly.

## Layout

```text
src/Kasper.Domain          entities and rules (workflow state machine, approval policy)
src/Kasper.Application     interfaces, contracts, options, prompts
src/Kasper.Infrastructure  Ollama, SQLite (EF Core), session/token security
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
