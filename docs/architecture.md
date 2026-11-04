# KASPER architecture

One Windows desktop process (modular monolith, PRD §11):

```text
Kasper.Desktop (WPF)  ── tray, Ctrl+Space hotkey, notifications, splash, WebView2 window
   └─ hosts Kasper.Api (ASP.NET Core on 127.0.0.1:<random>, per-launch bearer token)
         ├─ endpoints: assistant, workflows, approvals, documents, integrations, projects, schedules, mcp, SSE events
         └─ Kasper.Infrastructure
               ├─ Runtime: WorkflowEngine + WorkflowWorker (background), WorkflowService, AssistantService,
               │           ProjectService, ScheduleRunner
               ├─ Planning: OllamaPlanner (JSON plan → PlanValidator → one repair round)
               ├─ Tools: files.*, ai.generate_text, documents.convert, pdf.*, notion.*, slack.*, github.*, mcp.*
               ├─ Documents: DocumentConverter (engine routing), PdfOps (PDFsharp), engines via ProcessRunner
               ├─ Integrations: Notion, Slack, GitHub clients; DPAPI secret stores
               ├─ Mcp: McpHub (stdio JSON-RPC client, allowlisted tools)
               ├─ Persistence: EF Core + SQLite (%LOCALAPPDATA%\KASPER\kasper.db), migrations
               └─ Observability: JSON-lines file log, audit events
Kasper.Cli (kasper.exe) ── thin HTTP client of the same API (reads session.json)
ui/ (React + Vite)       ── served by the API, loaded in WebView2
```

Dependency direction: Domain → nothing; Application → Domain; Infrastructure → Application; Api → Application/Infrastructure;
Desktop → everything. Enforced by `ArchitectureTests`.

## A request, end to end

1. Command bar / `kasper ask` → `POST /api/assistant`.
2. `OllamaPlanner` asks the local model for a JSON plan, listing only available tools and allowed references
   (`folder:N`, `#channel`, `repo:owner/name`). `PlanValidator` rejects unknown tools, invented parameters, forward
   references, unknown folders/channels/repos, unsafe file names and secrets supplied by the model.
3. `WorkflowService` stores the workflow and steps; the worker picks it up.
4. `WorkflowEngine` runs steps in order. Reads and staging writes run immediately. Any step whose effect class needs
   approval (save outside staging, external write, destructive) creates a **Final Review** bound to a SHA-256 hash of
   the tool, its resolved input and the content of any file involved. Later approval steps that are already resolvable
   are reviewed together.
5. After approval the engine recomputes the hash right before acting; if anything changed, it asks again.
6. Retries: transient and rate-limit errors (and timeouts of idempotent tools) with exponential backoff + jitter.
   External writes reconcile before retrying (Notion/Slack check for the item first; files compare hashes).
7. Restart: running steps of idempotent tools replay; others fail with "check before retrying".

Document Studio jobs and schedules use the same pipeline; Document Studio builds its plan on the server (no model).
