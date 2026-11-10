import { useEffect, useState } from "react";
import { api2 as api, ApiError, type Guardrails, type Payment, type WorkflowEvent } from "../api";
import { StatusPill, timeAgo } from "../components";

const money = (n: number, c: string) => `${n.toFixed(2)} ${c}`;

/** Payments and invoices KASPER prepared, what happened to each, and the spending limits. */
export function Payments({ lastEvent }: { lastEvent: WorkflowEvent | null }) {
  const [list, setList] = useState<Payment[] | null>(null);

  useEffect(() => {
    api.payments().then(setList).catch(() => setList([]));
  }, [lastEvent]);

  return (
    <div>
      <h1>Payments</h1>
      <p className="muted">PayPal sandbox only: no real money moves. KASPER never pays without your approval here and again in PayPal.</p>
      <LimitsCard />
      <section className="card">
        <h2>History</h2>
        {list?.length === 0 && <p className="muted">Nothing yet. Try: “buy me a mechanical keyboard under $100”.</p>}
        {list?.map((p) => <Row key={p.id} p={p} />)}
      </section>
    </div>
  );
}

function Row({ p }: { p: Payment }) {
  const blocked = p.status === "Blocked";
  return (
    <div className={blocked ? "card blocked" : "card"} style={blocked ? { borderColor: "#c0392b" } : undefined}>
      <strong>{p.kind === "invoice" ? `Invoice to ${p.recipient}` : p.item}</strong> · {money(p.amount, p.currency)}{" "}
      <StatusPill status={p.status} /> <span className="muted small">{timeAgo(p.createdAtUtc)}</span>
      {blocked && <p>Blocked by <code>{p.blockedRule}</code>: {p.blockedMessage}. Nothing was created in PayPal.</p>}
      {p.failureSummary && <p className="muted small">{p.failureSummary}</p>}
      {p.reason && <p className="muted small">Why: {p.reason}</p>}
      <p className="muted small">
        {p.kind === "invoice" ? p.item : p.merchant}
        {p.payPalOrderId && ` · order ${p.payPalOrderId}`}
        {p.payPalInvoiceId && ` · invoice ${p.payPalInvoiceId}`}
      </p>
    </div>
  );
}

function LimitsCard() {
  const [g, setG] = useState<Guardrails | null>(null);
  const [msg, setMsg] = useState("");

  useEffect(() => { api.guardrails().then(setG).catch(() => {}); }, []);
  if (!g) return null;

  const save = async () => {
    try { setG(await api.setGuardrails(g)); setMsg("Saved."); }
    catch (e) { setMsg(e instanceof ApiError ? e.message : "Could not save."); }
  };
  const num = (v: string) => (v === "" ? 0 : Number(v));

  return (
    <section className="card">
      <h2>Spending limits</h2>
      <label>Per purchase ({g.currency}) <input type="number" min="1" value={g.perTransactionLimit} onChange={(e) => setG({ ...g, perTransactionLimit: num(e.target.value) })} /></label>{" "}
      <label>Per day ({g.currency}) <input type="number" min="1" value={g.dailyLimit} onChange={(e) => setG({ ...g, dailyLimit: num(e.target.value) })} /></label>{" "}
      <label>Duplicate window (min) <input type="number" min="0" value={g.duplicateWindowMinutes} onChange={(e) => setG({ ...g, duplicateWindowMinutes: num(e.target.value) })} /></label>{" "}
      <button className="btn-primary" onClick={save}>Save</button> <span className="muted small">{msg}</span>
    </section>
  );
}
