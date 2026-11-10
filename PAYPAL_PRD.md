# PRD: KASPER Pay

Working document for the PayPal AI Hackathon 2026 (Devpost deadline: Nov 12, 2026, 12 PM PST).
This file is the source of truth for what gets built in this repository. It is written for the AI coding agent working in this repo and for the owner.

**Repo:** `KASPER-Pay` (copy of KASPER, own remote: `https://github.com/K-aligrapher/KASPER-Pay`)
**License:** MIT (LICENSE file in repo root)

---

## 1. Summary

KASPER Pay adds PayPal payments to KASPER, a local-first AI coworker. The user asks for a purchase or an invoice in plain language. The local model (Qwen via Ollama) proposes the action. Deterministic code checks it against spending rules. The user approves it in Final Review. The user then approves again inside PayPal. Only after that does KASPER capture the payment and record it.

**Pitch:** An AI coworker that can shop and bill for you, but never spends money without your approval.

**Non-negotiable principle:** the LLM never decides amounts, never calls PayPal directly, and never sees credentials. Code does.

---

## 2. Goals

- A working PayPal sandbox purchase flow with two approvals (KASPER, then PayPal).
- Spending guardrails enforced in code.
- A second PayPal capability: Invoicing v2 (sandbox permission confirmed on the owner's account).
- A strong entry for: Best Use of Agentic Commerce, Best Use of PayPal + AI, Most Creative, Best Overall.

### Out of scope
- Real money / live PayPal environment (sandbox only).
- Live product search or scraping (catalog is seeded).
- Webhooks (stretch goal only).
- Multiple currencies (USD only).
- Mobile or cloud deployment.

---

## 3. User stories

1. "Buy me a mechanical keyboard under $100." KASPER proposes one catalog product with reasoning. The user approves in KASPER, PayPal checkout opens, the user approves in PayPal, KASPER captures, and the payment appears in History.
2. "Buy a $900 monitor." Blocked by the per-transaction limit. KASPER explains why. Nothing is created in PayPal.
3. "Invoice alex@example.com $200 for logo design." KASPER drafts the invoice, the user approves it in Final Review, and it is sent through PayPal Invoicing v2.
4. If the user rejects in KASPER, or cancels in PayPal, nothing is charged and the state is recorded.
5. If the amount, item, or currency changes after approval, the approval is invalid and the action is blocked.

---

## 4. Functional requirements

| ID | Requirement |
|---|---|
| F1 | Backend obtains a PayPal OAuth access token via client credentials, caches it, and refreshes it before expiry. Sandbox base URL only by default. |
| F2 | Seeded catalog of 5 to 10 products, all USD. Prices come only from the catalog. |
| F3 | Planner intents `commerce.purchase` and `commerce.invoice`. The model outputs product IDs, recipient email and free-text description. It never outputs totals. |
| F4 | Validation before any proposal is staged: product exists, amount derived from catalog, currency is USD, recipient email is well-formed. Otherwise reject with a reason. |
| F5 | Guardrails (code, config-driven): per-transaction limit, daily spend limit, merchant/recipient allow-list, duplicate-purchase detection within a time window. A violation produces state `Blocked` with the rule name. |
| F6 | Every payment or invoice goes through the existing Final Review flow. The approval is bound to a fingerprint (hash of action type, item IDs, amounts, currency, recipient). Any change invalidates the approval. |
| F7 | On approval of a purchase: create a PayPal Orders v2 order (intent CAPTURE), store the order ID, and open the approver link in the default browser. |
| F8 | Poll the order status. When `APPROVED`, call capture. Verify the capture response: status `COMPLETED`, amount and currency equal the intent. Only then mark `Captured`. |
| F9 | Purchase states: `Proposed` → `Approved` → `CheckoutCreated` → `PayerApproved` → `Captured`. Terminal side states: `Rejected`, `Blocked`, `Cancelled`, `Expired`, `Failed`. Transitions are enforced in code; invalid transitions throw. |
| F10 | Invoicing v2: create a draft invoice, get it approved in Final Review, then send it. Store the invoice ID and send status. |
| F11 | Persistence in SQLite: payment intents, state transitions with timestamps, PayPal IDs, guardrail results, and an audit log. History survives restarts. |
| F12 | UI (in `ui/`): payment review card (item, merchant, total, currency, model reasoning, "nothing charged yet" note), blocked-action card with rule name, History list, spending-limit settings. |
| F13 | Idempotency: capturing the same order twice must not create a second capture. Clicking Approve twice must create only one PayPal order. |

---

## 5. Non-functional requirements

- **Secrets:** PayPal Client ID and Secret are never sent to the model, prompts, frontend, logs, or the repo. They come from .NET user secrets or environment variables.
- **Local-first boundary:** the model and app data stay local. Only the data needed for a PayPal order or invoice (item name, amount, currency, recipient email, description) leaves the machine. The README must state this exactly.
- **No commits of secrets:** `.env`, user-secret files, and `appsettings.*.json` with values are never committed.
- **Restart safety:** no payment step is lost if the app restarts mid-flow. Pending intents are resumable or marked `Expired`.
- **Logging:** log PayPal order IDs and states. Never log tokens, secrets, or full request/response bodies containing personal data.

---

## 6. Configuration (names are fixed; values are set by the owner)

User secrets (set in the API project with `dotnet user-secrets`):

| Key | Value |
|---|---|
| `PayPal:ClientId` | Sandbox app Client ID |
| `PayPal:ClientSecret` | Sandbox app Secret |
| `PayPal:Environment` | `sandbox` |

Guardrail settings (non-secret, in app config, defaults for the demo):

| Key | Default |
|---|---|
| `Payments:Currency` | `USD` |
| `Payments:PerTransactionLimit` | `100.00` |
| `Payments:DailyLimit` | `250.00` |
| `Payments:DuplicateWindowMinutes` | `10` |

Sandbox buyer account: the owner logs in with it during the demo. Its credentials are never stored in the repo.

---

## 7. Architecture

Existing projects (verify names against the real `.sln` before creating files):

| Project | Addition |
|---|---|
| `Kasper.Domain` | `PaymentIntent`, `PaymentStatus`, `InvoiceDraft`, `SpendingPolicy`, state transition rules |
| `Kasper.Application` | `IPaymentProvider`, `IInvoiceProvider`, `ICatalog`, guardrail evaluation, fingerprinting, proposal validation |
| `Kasper.Infrastructure` | `PayPalTokenProvider`, `PayPalClient`, `PayPalOrdersService`, `PayPalInvoicingService`, seeded catalog, EF Core migrations |
| `Kasper.Api` | `/api/commerce/*`, `/api/payments/*`, `/api/invoices/*` endpoints |
| `Kasper.Cli` | Optional payment commands (stretch) |
| `ui/` | Payment review card, blocked card, History, settings |

Planner: new intents `commerce.purchase` and `commerce.invoice` added to the existing planner. Output is a strict JSON schema, validated by code, with one retry on invalid output.

### Flow (purchase)

```
User message
  → planner (Qwen) → {productId, reason}
  → validate + catalog price + guardrails → PaymentIntent (Proposed) or Blocked
  → Final Review (fingerprint) → Approved | Rejected
  → PayPal create order → CheckoutCreated (open approve link)
  → user approves in PayPal → poll → PayerApproved
  → capture → verify → Captured → History
```

### API (proposed)

```
POST /api/commerce/propose
GET  /api/payments
GET  /api/payments/{id}
POST /api/payments/{id}/approve
POST /api/payments/{id}/reject
POST /api/payments/{id}/checkout        (create PayPal order, return approve URL)
GET  /api/payments/{id}/status          (poll PayPal, capture when APPROVED)
POST /api/invoices/propose
POST /api/invoices/{id}/approve
POST /api/invoices/{id}/send
GET  /api/settings/guardrails
PUT  /api/settings/guardrails
```

Endpoints must bind to localhost, like the existing `LocalOnlyAuthMiddleware`.

---

## 8. Milestones

| # | Milestone | Done when |
|---|---|---|
| M0 | PayPal access (owner) | Sandbox app and buyer account exist; secrets set in user secrets |
| M1 | PayPal client | Token fetch cached; create and get a sandbox order from a test |
| M2 | Catalog and pricing | Catalog loads; totals computed in code; model cannot change amounts |
| M3 | AI proposal | Chat produces a valid proposal; bad JSON is rejected with a retry |
| M4 | Final Review integration | Approve/Reject work; a changed fingerprint invalidates approval |
| M5 | Checkout and capture | End-to-end sandbox purchase appears in the sandbox account |
| M6 | History and audit | Transactions survive restart and show in History |
| M6.5 | Guardrails and invoicing | Over-limit purchase is blocked on screen; invoice sends in sandbox |
| M7 | Hardening | Success, reject, cancel, double-click, and restart cases pass twice in a row |
| M8 | Submission | Public repo, MIT license, `.env.example`, README, demo video under 3 minutes, Devpost submitted |

Priority if time runs short: M0 → M1 → M4 → M5 → M2 → M3 → M6.5 guardrails → M8. Cut invoicing (M6.5 second half) before cutting guardrails.

---

## 9. Ownership

### Owner (only the owner can do these)
1. PayPal sandbox app and buyer account, and setting user secrets (M0). Confirmed: sandbox access, Invoicing API and PayPal payments permissions visible on the account.
2. Devpost registration, and checking the rules on whether one project can win multiple prizes.
3. Running the app on the owner's machine (Ollama, desktop app, sandbox checkout in the browser) and reporting failures.
4. Logging in as the buyer during checkout tests and the demo.
5. Recording and uploading the demo video; submitting on Devpost.
6. Making the repo public and confirming the license at submission.

### Coding agent (in this repo)
1. Read the existing code first and map this PRD onto the real classes, names, and planner. Report any mismatch before changing core files.
2. Implement F1–F13, backend first, then UI.
3. Write unit tests for guardrails, fingerprinting, state transitions, and validation. Write sandbox integration tests that read credentials from user secrets and skip when they are absent.
4. Update `README.md` (what it is, architecture diagram, PayPal usage, AI usage, safety model, the data boundary, setup in 5 steps), add `.env.example`, and draft the demo script and Devpost text.
5. Commit only when the owner asks. Never push without being asked. Never force-push.
6. Never write secrets into files, tests, logs, or commit messages.

---

## 10. Acceptance criteria (demo)

- A purchase completes in sandbox and appears in both the PayPal sandbox account and KASPER History.
- An over-limit request is blocked on screen with the rule name.
- An invoice is sent and visible in the PayPal sandbox.
- A changed amount after approval is rejected.
- Three demo recordings in a row finish without a crash.
- README states exactly what data goes to PayPal.
- Repo is public with an MIT LICENSE.

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| Sandbox signup or permissions blocked | Already confirmed on owner's account; keep the Discord support path |
| Qwen returns malformed JSON | Strict schema validation, one retry, then ask the user |
| Return from PayPal to the desktop app is unreliable | Use polling of order status; no return URL dependency |
| Currency handling in sandbox | USD only |
| Time runs out | Cut invoicing first; never cut guardrails or the two-approval flow |
| Existing KASPER README says nothing leaves the machine | Rewrite the data-boundary section in M8 |

---

## 12. Open items to verify before coding

- Exact project and class names in `KASPER.sln` (the agent verifies, not assumes).
- How Final Review and its fingerprint work today, so payments reuse it.
- Which planner entry point handles new intents (the recent commit "better planner hints" touched this).
- Devpost rule on multiple prizes per project.
- Whether PayPal's sandbox accepts the Orders v2 flow from a localhost app without a return URL (polling covers this either way).
