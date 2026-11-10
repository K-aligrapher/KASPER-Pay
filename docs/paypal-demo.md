# KASPER Pay: demo script and Devpost text

## Demo script (under 3 minutes)

Before recording: Ollama running, sandbox credentials set, sandbox buyer account ready, Payments screen open once. Do a full dry run first.

1. **(0:00)** Say: "KASPER is a local AI coworker. Now it can shop and bill with PayPal, but never spends without my approval."
2. **(0:15)** Type: *buy me a mechanical keyboard under $100*. Show the Final Review card: item, merchant, total, currency, the model's reason, and the note "Nothing is charged yet". Click **Approve**.
3. **(0:55)** The PayPal sandbox page opens. Log in as the buyer and approve. Return to KASPER: the Payments screen shows **Captured** with the PayPal order id.
4. **(1:30)** Type: *buy a $900 monitor*. Show the **Blocked** card: `per_transaction_limit`, nothing created in PayPal.
5. **(1:50)** Type: *invoice alex@example.com $200 for logo design*. Approve in Final Review. Show the invoice in the PayPal sandbox.
6. **(2:25)** Show Payments history and spending limits. Close: "The model proposes. Code checks. You approve twice. Only then does money move."

## Devpost text

**Inspiration.** AI agents that can pay are useful and scary. We wanted one that is useful because it is *constrained*.

**What it does.** KASPER Pay adds PayPal to KASPER, a local-first AI coworker. Ask for a purchase or an invoice in plain language. A local model (Qwen via Ollama) proposes the action. Code validates it against the catalog and your spending rules. You approve it in KASPER's Final Review, then approve again in PayPal. Only then does KASPER capture the payment and record it.

**How we built it.** .NET 10 + SQLite + a React UI. PayPal Orders v2 (create, poll, capture, verify) and Invoicing v2 (draft, send). The model only names a catalog product id; amounts come from code and are bound into an approval fingerprint, so any change after approval invalidates it. Guardrails (per-purchase and daily limits, duplicate window, allow-lists) run in code before and after approval. Idempotency keys make double clicks and restarts safe.

**Challenges.** Keeping the model out of the money path; getting capture to be idempotent and verified; reliable return from PayPal to a desktop app (we poll, no return URL needed).

**What we learned.** Autonomy and trust come from hard, boring checks outside the model.

**What's next.** Webhooks, more currencies, live product search, team approval flows.

**Data boundary.** The model, chat and history stay on the machine. Only the item name, SKU, amount, currency and (for invoices) recipient email and description go to PayPal. Credentials never reach the model, UI or repo. Sandbox only.

**Built with.** .NET, ASP.NET Core, EF Core/SQLite, React, Ollama (Qwen), PayPal Orders v2, PayPal Invoicing v2.
