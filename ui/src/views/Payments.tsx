import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AgGridReact } from "ag-grid-react";
import {
  AllCommunityModule, ModuleRegistry, colorSchemeDark, themeQuartz,
  type ColDef, type GridApi, type ICellRendererParams, type ValueFormatterParams,
} from "ag-grid-community";
import { AllEnterpriseModule, LicenseManager } from "ag-grid-enterprise";
import { AgCharts } from "ag-charts-react";
import type { AgCartesianChartOptions } from "ag-charts-community";
import { api2 as api, ApiError, type ChainStatus, type Guardrails, type Payment, type WorkflowEvent } from "../api";
import { Bunny } from "../Bunny";
import { timeAgo } from "../components";

// AG Grid Enterprise (set filters, side bar, status bar totals, Excel export) switches on only when a
// license key is present at build time. Without one, everything still works with the community grid.
const AG_KEY = (import.meta as unknown as { env: Record<string, string | undefined> }).env.VITE_AG_GRID_LICENSE;
const ENTERPRISE = !!AG_KEY;
ModuleRegistry.registerModules(ENTERPRISE ? [AllCommunityModule, AllEnterpriseModule] : [AllCommunityModule]);
if (AG_KEY) LicenseManager.setLicenseKey(AG_KEY);

const gridTheme = themeQuartz.withPart(colorSchemeDark).withParams({
  backgroundColor: "#161616",
  foregroundColor: "#f2f2f2",
  headerBackgroundColor: "#1f1f1f",
  borderColor: "#2a2a2a",
  accentColor: "#f5b800",
  oddRowBackgroundColor: "#131313",
  rowHoverColor: "#26220f",
  fontFamily: "Inter, system-ui, sans-serif",
  fontSize: 13,
});

const SPEND = new Set(["Approved", "CheckoutCreated", "PayerApproved", "Captured"]);
const money = (n: number, c = "USD") => `${c === "USD" ? "$" : ""}${n.toFixed(2)}`;
const isPaid = (p: Payment) => p.invoiceState === "PAID" || p.invoiceState === "MARKED_AS_PAID";
const isOpenInvoice = (p: Payment) => p.kind === "invoice" && p.status === "Sent" && !isPaid(p) && p.invoiceState !== "CANCELLED";
const shortHash = (h: string | null) => (h ? `${h.slice(0, 8)}…${h.slice(-6)}` : "");

/** Money in and out: totals, a 14-day chart, a filterable grid, receipts you can verify, and your spending rules. */
export function Payments({ lastEvent, go }: { lastEvent: WorkflowEvent | null; go: (r: string) => void }) {
  const [list, setList] = useState<Payment[] | null>(null);
  const [limits, setLimits] = useState<Guardrails | null>(null);
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<Payment | null>(null);

  const load = useCallback(() => {
    api.payments().then(setList).catch(() => setList([]));
    api.guardrails().then(setLimits).catch(() => {});
  }, []);
  useEffect(load, [load, lastEvent]);

  const refresh = async () => {
    setBusy(true);
    try { setList(await api.refreshPayments()); } catch { /* keep the old list */ } finally { setBusy(false); }
  };

  const rows = list ?? [];
  const stats = useMemo(() => {
    const today = new Date().toISOString().slice(0, 10);
    const weekAgo = Date.now() - 7 * 86400000;
    const purchases = rows.filter((p) => p.kind === "purchase");
    const invoices = rows.filter((p) => p.kind === "invoice" && p.status === "Sent");
    return {
      spentToday: purchases.filter((p) => SPEND.has(p.status) && p.createdAtUtc.slice(0, 10) === today).reduce((s, p) => s + p.amount, 0),
      spentTotal: purchases.filter((p) => p.status === "Captured").reduce((s, p) => s + p.amount, 0),
      invoiced: invoices.reduce((s, p) => s + p.amount, 0),
      collected: invoices.filter(isPaid).reduce((s, p) => s + p.amount, 0),
      outstanding: invoices.filter(isOpenInvoice).reduce((s, p) => s + p.amount, 0),
      openCount: invoices.filter(isOpenInvoice).length,
      blocked: rows.filter((p) => p.status === "Blocked").length,
      auto: rows.filter((p) => p.autoApproved && SPEND.has(p.status) && new Date(p.createdAtUtc).getTime() >= weekAgo),
    };
  }, [rows]);

  const dailyPct = limits ? Math.min(100, (stats.spentToday / limits.dailyLimit) * 100) : 0;

  return (
    <div className="page">
      <header className="page-head pay-head">
        <div>
          <h1>Payments</h1>
          <p className="muted">Everything KASPER bought or billed with PayPal. Sandbox only, so no real money moves.</p>
        </div>
        <button className="btn-ghost" onClick={refresh} disabled={busy}>{busy ? "Checking PayPal…" : "Refresh invoice status"}</button>
      </header>

      <div className="pay-tiles">
        <div className="tile">
          <span className="tile-label">Spent today</span>
          <span className="tile-big">{money(stats.spentToday)}</span>
          {limits && <>
            <div className="tile-bar"><div style={{ width: `${dailyPct}%` }} className={dailyPct > 80 ? "hot" : ""} /></div>
            <span className="tile-sub">of {money(limits.dailyLimit)} daily limit</span>
          </>}
        </div>
        <div className="tile">
          <span className="tile-label">Bought so far</span>
          <span className="tile-big">{money(stats.spentTotal)}</span>
          <span className="tile-sub">{rows.filter((p) => p.kind === "purchase" && p.status === "Captured").length} completed purchases</span>
        </div>
        <div className="tile">
          <span className="tile-label">Invoiced</span>
          <span className="tile-big">{money(stats.invoiced)}</span>
          <span className="tile-sub"><b className="ok">{money(stats.collected)}</b> paid · <b className={stats.outstanding > 0 ? "warn" : ""}>{money(stats.outstanding)}</b> waiting ({stats.openCount})</span>
        </div>
        <div className="tile">
          <span className="tile-label">Safety net</span>
          <span className="tile-big">{stats.blocked}</span>
          <span className="tile-sub">requests blocked by your rules{stats.auto.length > 0 ? ` · ${stats.auto.length} auto-approved this week` : ""}</span>
        </div>
      </div>

      <div className="pay-split">
        <ActivityChart rows={rows} />
        <ReceiptsCard key={rows.length} />
      </div>

      <section className="card pay-grid-card">
        <PaymentsGrid rows={rows} loading={list === null} onSelect={setSelected} go={go} />
      </section>

      {selected && <Detail p={selected} onClose={() => setSelected(null)} go={go} />}

      {limits && <LimitsCard limits={limits} onSaved={setLimits} />}
    </div>
  );
}

function ActivityChart({ rows }: { rows: Payment[] }) {
  const data = useMemo(() => {
    const days = Array.from({ length: 14 }, (_, i) => {
      const d = new Date(Date.now() - (13 - i) * 86400000);
      return { key: d.toISOString().slice(0, 10), label: d.toLocaleDateString(undefined, { month: "short", day: "numeric" }), spent: 0, invoiced: 0 };
    });
    for (const p of rows) {
      const day = days.find((d) => d.key === p.createdAtUtc.slice(0, 10));
      if (!day) continue;
      if (p.kind === "purchase" && SPEND.has(p.status)) day.spent += p.amount;
      if (p.kind === "invoice" && p.status === "Sent") day.invoiced += p.amount;
    }
    return days;
  }, [rows]);

  const chartOptions: AgCartesianChartOptions = {
    data,
    theme: "ag-default-dark",
    background: { fill: "transparent" },
    series: [
      { type: "bar", xKey: "label", yKey: "spent", yName: "Spent", stacked: true, fill: "#f5b800" },
      { type: "bar", xKey: "label", yKey: "invoiced", yName: "Invoiced", stacked: true, fill: "#58a6ff" },
    ],
    axes: {
      category: { position: "bottom" },
      number: { position: "left", label: { formatter: (p) => `$${p.value}` } },
    },
    legend: { position: "bottom" },
  };
  const empty = data.every((d) => d.spent === 0 && d.invoiced === 0);
  return (
    <section className="card">
      <h2>Last 14 days</h2>
      <p className="muted small">What KASPER spent and what you billed.</p>
      {empty ? (
        <div className="pay-empty"><Bunny size={48} /><p className="muted">No money has moved yet. Try “buy me a wireless mouse under $30”.</p></div>
      ) : (
        <div style={{ height: 230 }}>
          <AgCharts options={chartOptions} />
        </div>
      )}
    </section>
  );
}

function ReceiptsCard() {
  const [status, setStatus] = useState<ChainStatus | null>(null);
  const [busy, setBusy] = useState(false);

  const verify = async () => {
    setBusy(true);
    try { setStatus(await api.verifyReceipts()); } finally { setBusy(false); }
  };
  useEffect(() => { api.verifyReceipts().then(setStatus).catch(() => {}); }, []);

  const download = async () => {
    const receipts = await api.receipts();
    const blob = new Blob([JSON.stringify({ exportedAt: new Date().toISOString(), chain: status, receipts }, null, 2)], { type: "application/json" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = "kasper-receipts.json";
    a.click();
    URL.revokeObjectURL(a.href);
  };

  return (
    <section className="card">
      <h2>Tamper-evident receipts</h2>
      <p className="muted small">Every completed payment and sent invoice is sealed with a hash that includes the one before it. Change an old record and every later seal breaks.</p>
      <div className={status ? (status.intact ? "chain ok" : "chain bad") : "chain"}>
        {!status ? "Checking…" : status.count === 0 ? "No receipts yet" :
          status.intact ? `Chain intact · ${status.count} receipt${status.count === 1 ? "" : "s"}` : `Chain BROKEN at receipt #${status.brokenAtSeq}`}
      </div>
      {status?.head && <p className="small muted mono">head {shortHash(status.head)}</p>}
      <div className="pay-actions">
        <button className="btn-ghost btn-sm" onClick={verify} disabled={busy}>{busy ? "Verifying…" : "Verify now"}</button>
        <button className="btn-ghost btn-sm" onClick={download} disabled={!status || status.count === 0}>Download proof</button>
      </div>
    </section>
  );
}

const CHIPS = [
  { id: "all", label: "All" },
  { id: "purchase", label: "Purchases" },
  { id: "invoice", label: "Invoices" },
  { id: "open", label: "Waiting to be paid" },
  { id: "blocked", label: "Blocked" },
] as const;

function StatusCell({ data }: ICellRendererParams<Payment>) {
  if (!data) return null;
  const paid = isPaid(data);
  const label = data.kind === "invoice" && data.status === "Sent" ? (paid ? "Paid" : data.invoiceState === "CANCELLED" ? "Cancelled" : "Waiting") : data.status === "CheckoutCreated" ? "At PayPal" : data.status === "PayerApproved" ? "Approved" : data.status;
  const tone = paid || data.status === "Captured" ? "ok" : ["Blocked", "Failed", "Rejected"].includes(data.status) ? "bad" : ["Expired", "Cancelled"].includes(data.status) ? "mute" : data.status === "Sent" ? "warn" : "info";
  return <span className={`pay-pill pay-pill--${tone}`}>{label}</span>;
}

function PaymentsGrid({ rows, loading, onSelect, go }: { rows: Payment[]; loading: boolean; onSelect: (p: Payment | null) => void; go: (r: string) => void }) {
  const apiRef = useRef<GridApi<Payment> | null>(null);
  const [chip, setChip] = useState<(typeof CHIPS)[number]["id"]>("all");
  const [quick, setQuick] = useState("");
  const [grouped, setGrouped] = useState(false);

  const filtered = useMemo(() => rows.filter((p) =>
    chip === "all" ? true : chip === "open" ? isOpenInvoice(p) : chip === "blocked" ? p.status === "Blocked" : p.kind === chip), [rows, chip]);

  const columns = useMemo<ColDef<Payment>[]>(() => [
    { field: "createdAtUtc", headerName: "When", width: 130, sort: "desc", valueFormatter: (p: ValueFormatterParams<Payment, string>) => timeAgo(p.value ?? null),
      tooltipValueGetter: (p) => (p.data ? new Date(p.data.createdAtUtc).toLocaleString() : "") },
    { field: "kind", headerName: "Type", width: 105, ...(ENTERPRISE ? { filter: "agSetColumnFilter" } : {}),
      valueFormatter: (p) => (p.value === "invoice" ? "Invoice" : "Purchase") },
    { field: "item", headerName: "What", flex: 2, minWidth: 200, tooltipField: "item" },
    { headerName: "Who", flex: 1, minWidth: 150, valueGetter: (p) => (p.data?.kind === "invoice" ? p.data.recipient : p.data?.merchant) ?? "" },
    { field: "amount", headerName: "Amount", width: 120, type: "rightAligned", aggFunc: "sum", valueFormatter: (p) => (p.value == null ? "" : money(p.value as number)) },
    { field: "status", headerName: "Status", width: 135, cellRenderer: StatusCell, ...(ENTERPRISE ? { filter: "agSetColumnFilter", enableRowGroup: true } : {}) },
    { headerName: "Receipt", width: 110, valueGetter: (p) => (p.data?.receiptSeq ? `#${p.data.receiptSeq}` : ""), tooltipValueGetter: (p) => shortHash(p.data?.receiptHash ?? null) },
    { headerName: "Notes", flex: 1.4, minWidth: 180, valueGetter: (p) => {
        const d = p.data;
        if (!d) return "";
        return d.status === "Blocked" ? `Blocked: ${d.blockedRule}` : d.autoApproved ? "Auto-approved (mandate)" : d.failureSummary ?? "";
      } },
  ], []);

  useEffect(() => { apiRef.current?.setGridOption("quickFilterText", quick); }, [quick]);
  useEffect(() => {
    if (!ENTERPRISE || !apiRef.current) return;
    apiRef.current.setRowGroupColumns(grouped ? ["status"] : []);
  }, [grouped]);

  return (
    <>
      <div className="pay-toolbar">
        <div className="chips">
          {CHIPS.map((c) => <button key={c.id} className={c.id === chip ? "chip active" : "chip"} onClick={() => setChip(c.id)}>{c.label}</button>)}
        </div>
        <div className="pay-tools">
          <input placeholder="Search…" value={quick} onChange={(e) => setQuick(e.target.value)} />
          {ENTERPRISE && <button className="btn-ghost btn-sm" onClick={() => setGrouped((g) => !g)}>{grouped ? "Ungroup" : "Group by status"}</button>}
          <button className="btn-ghost btn-sm" onClick={() => (ENTERPRISE ? apiRef.current?.exportDataAsExcel() : apiRef.current?.exportDataAsCsv())}>
            {ENTERPRISE ? "Export Excel" : "Export CSV"}
          </button>
        </div>
      </div>
      <div className="pay-grid">
        <AgGridReact<Payment>
          theme={gridTheme}
          rowData={filtered}
          columnDefs={columns}
          loading={loading}
          defaultColDef={{ sortable: true, filter: true, resizable: true }}
          getRowId={(p) => p.data.id}
          rowSelection={{ mode: "singleRow", checkboxes: false, enableClickSelection: true }}
          onGridReady={(e) => { apiRef.current = e.api; }}
          onRowClicked={(e) => onSelect(e.data ?? null)}
          onRowDoubleClicked={(e) => e.data?.workflowId && go(`history/${e.data.workflowId}`)}
          tooltipShowDelay={400}
          animateRows
          overlayNoRowsTemplate="<span style='color:#a3a3a3'>Nothing here yet. Ask KASPER to buy something or send an invoice.</span>"
          {...(ENTERPRISE ? {
            sideBar: { toolPanels: ["columns", "filters"], defaultToolPanel: "" },
            statusBar: { statusPanels: [{ statusPanel: "agTotalAndFilteredRowCountComponent", align: "left" }, { statusPanel: "agAggregationComponent", align: "right" }] },
            cellSelection: true,
            rowGroupPanelShow: "never" as const,
            groupDefaultExpanded: 1,
          } : {})}
        />
      </div>
      <p className="muted small">{ENTERPRISE ? "Click a row for details · double-click to open the full trace · select cells to see totals in the footer." : "Click a row for details · double-click to open the full trace."}</p>
    </>
  );
}

function Detail({ p, onClose, go }: { p: Payment; onClose: () => void; go: (r: string) => void }) {
  const f = (label: string, value: string | null | undefined) => (value ? <div key={label}><dt>{label}</dt><dd>{value}</dd></div> : null);
  return (
    <section className="card">
      <div className="pay-head">
        <h2>{p.kind === "invoice" ? `Invoice to ${p.recipient}` : p.item}</h2>
        <div className="pay-actions">
          {p.workflowId && <button className="btn-ghost btn-sm" onClick={() => go(`history/${p.workflowId}`)}>Open full trace</button>}
          <button className="btn-ghost btn-sm" onClick={onClose}>Close</button>
        </div>
      </div>
      {p.status === "Blocked" && <div className="pay-alert">Blocked by <code>{p.blockedRule}</code>: {p.blockedMessage} Nothing was created in PayPal.</div>}
      {p.autoApproved && <div className="pay-note">Pre-approved by your spending mandate. PayPal still asked you to approve the payment.</div>}
      <dl className="fields">
        {f("Amount", `${money(p.amount, p.currency)} ${p.currency}`)}
        {f("Status", p.kind === "invoice" && p.invoiceState ? `${p.status} · PayPal says ${p.invoiceState.toLowerCase().replace(/_/g, " ")}` : p.status)}
        {f("Merchant", p.kind === "purchase" ? p.merchant : "PayPal Invoicing")}
        {f("Why this item", p.reason)}
        {f("PayPal order", p.payPalOrderId)}
        {f("PayPal capture", p.payPalCaptureId)}
        {f("PayPal invoice", p.payPalInvoiceId)}
        {f("Receipt", p.receiptSeq ? `#${p.receiptSeq} · ${shortHash(p.receiptHash)}` : null)}
        {f("Problem", p.failureSummary)}
        {f("Created", new Date(p.createdAtUtc).toLocaleString())}
      </dl>
    </section>
  );
}

function LimitsCard({ limits, onSaved }: { limits: Guardrails; onSaved: (g: Guardrails) => void }) {
  const [g, setG] = useState(limits);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  useEffect(() => setG(limits), [limits]);
  const num = (v: string) => (v === "" ? 0 : Number(v));

  const save = async () => {
    try { onSaved(await api.setGuardrails(g)); setMsg({ ok: true, text: "Saved. New limits apply right away." }); }
    catch (e) { setMsg({ ok: false, text: e instanceof ApiError ? e.message : "Could not save." }); }
  };

  const field = (label: string, hint: string, key: keyof Guardrails, step = 1) => (
    <label className="pay-field">
      <span>{label}</span>
      <input type="number" min={0} step={step} value={g[key] as number} onChange={(e) => setG({ ...g, [key]: num(e.target.value) })} />
      <small className="muted">{hint}</small>
    </label>
  );

  return (
    <section className="card">
      <h2>Spending rules</h2>
      <p className="muted small">Enforced in code, before and after you approve. The AI can't change or bypass them.</p>
      <div className="pay-fields">
        {field("Per purchase (USD)", "Anything higher is blocked", "perTransactionLimit")}
        {field("Per day (USD)", "Total KASPER may spend per day", "dailyLimit")}
        {field("Duplicate window (min)", "Blocks the same buy twice in a row", "duplicateWindowMinutes")}
      </div>
      <h2 className="pay-sub">Spending mandate</h2>
      <p className="muted small">Let KASPER skip its own review card for small purchases. PayPal still asks you to approve every payment. Set to 0 to turn off.</p>
      <div className="pay-fields">
        {field("Auto-approve under (USD)", "Purchases up to this skip the review card", "autoApproveUnder", 0.5)}
        {field("Weekly auto-approve cap (USD)", "Most KASPER may auto-approve in 7 days", "autoApproveWeeklyCap", 1)}
      </div>
      <div className="pay-actions">
        <button className="btn-primary" onClick={save}>Save rules</button>
        {msg && <span className={msg.ok ? "ok small" : "small"} style={msg.ok ? undefined : { color: "var(--danger)" }}>{msg.text}</span>}
      </div>
    </section>
  );
}
