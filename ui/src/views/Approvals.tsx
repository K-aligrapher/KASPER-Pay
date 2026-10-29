import { useCallback, useEffect, useState } from "react";
import { api, ApiError, type Approval, type WorkflowEvent } from "../api";
import { Bunny } from "../Bunny";
import { timeAgo } from "../components";

/** Final Review: every save, send, update or delete waits here (FR-042, FR-046). */
export function Approvals({ lastEvent, go }: { lastEvent: WorkflowEvent | null; go: (route: string) => void }) {
  const [items, setItems] = useState<Approval[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api.approvals().then(setItems).catch((e) => setError(e instanceof ApiError ? e.message : "Could not load approvals."));
  }, []);

  useEffect(load, [load, lastEvent]);

  if (items === null) return <div className="page"><p className="muted">Loading…</p></div>;

  const groups = Object.values(
    items.reduce<Record<string, Approval[]>>((acc, a) => {
      (acc[a.workflowId] ??= []).push(a);
      return acc;
    }, {})
  );

  return (
    <div className="page">
      <header className="page-head">
        <h1>Approvals</h1>
        <p className="muted">Nothing is saved, sent, updated or deleted until you approve it here.</p>
      </header>
      {error && <div className="error">{error}</div>}
      {groups.length === 0 && (
        <section className="empty">
          <Bunny size={96} />
          <p>All clear. Nothing is waiting for you.</p>
        </section>
      )}
      {groups.map((group) => (
        <ReviewGroup key={group[0].workflowId} approvals={group} onChanged={load} go={go} />
      ))}
    </div>
  );
}

function ReviewGroup({ approvals, onChanged, go }: { approvals: Approval[]; onChanged: () => void; go: (r: string) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const approveAll = async () => {
    setBusy(true);
    setError(null);
    try {
      for (const a of approvals) await api.decide(a.id, "approve");
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not approve.");
    } finally {
      setBusy(false);
      onChanged();
    }
  };

  return (
    <section className="review-group">
      <div className="review-group-head">
        <div>
          <h2>{approvals[0].workflowSummary}</h2>
          <span className="muted small">
            {approvals.length} action{approvals.length > 1 ? "s" : ""} to review · asked {timeAgo(approvals[0].requestedAtUtc)} ·{" "}
            <a href={`#history/${approvals[0].workflowId}`} onClick={(e) => { e.preventDefault(); go(`history/${approvals[0].workflowId}`); }}>details</a>
          </span>
        </div>
        {approvals.length > 1 && (
          <button className="btn-primary" disabled={busy} onClick={approveAll}>Approve all {approvals.length}</button>
        )}
      </div>
      {error && <div className="error">{error}</div>}
      {approvals.map((a) => <ReviewCard key={a.id} approval={a} onChanged={onChanged} />)}
    </section>
  );
}

function ReviewCard({ approval: a, onChanged }: { approval: Approval; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const act = async (decision: "approve" | "reject" | "edit") => {
    setBusy(true);
    setError(null);
    try {
      await api.decide(a.id, decision, decision === "edit" ? edits : undefined);
      setEditing(false);
      onChanged();
    } catch (e) {
      setError(e instanceof ApiError ? [e.message, ...e.details].join(" ") : "Something went wrong.");
    } finally {
      setBusy(false);
    }
  };

  const startEdit = () => {
    setEdits(Object.fromEntries(a.editable.map((f) => [f.name, f.value ?? ""])));
    setEditing(true);
  };

  return (
    <article className="review-card">
      <div className="review-title">
        <h3>{a.title}</h3>
        <code className="target" title={a.target}>{a.target}</code>
      </div>

      {a.fields.length > 0 && (
        <dl className="fields">
          {a.fields.map((f) => (
            <div key={f.label}><dt>{f.label}</dt><dd>{f.value}</dd></div>
          ))}
        </dl>
      )}

      {a.warnings.map((w) => <div key={w} className="warning">⚠ {w}</div>)}

      {a.content !== null && <pre className="content-preview">{a.content}</pre>}

      {editing && (
        <div className="edit-form">
          {a.editable.map((f) => (
            <label key={f.name}>
              <span>{f.name} <span className="muted small">{f.description}</span></span>
              {f.options ? (
                <select value={edits[f.name] ?? ""} onChange={(e) => setEdits({ ...edits, [f.name]: e.target.value })}>
                  {f.options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              ) : (
                <input value={edits[f.name] ?? ""} onChange={(e) => setEdits({ ...edits, [f.name]: e.target.value })} />
              )}
            </label>
          ))}
        </div>
      )}

      {error && <div className="error">{error}</div>}

      <div className="review-actions">
        {editing ? (
          <>
            <button className="btn-primary" disabled={busy} onClick={() => act("edit")}>Save changes</button>
            <button className="btn-ghost" disabled={busy} onClick={() => setEditing(false)}>Cancel</button>
            <span className="muted small">You'll review the updated action again.</span>
          </>
        ) : (
          <>
            <button className="btn-primary" disabled={busy} onClick={() => act("approve")}>Approve</button>
            {a.editable.length > 0 && <button className="btn-ghost" disabled={busy} onClick={startEdit}>Edit</button>}
            <button className="btn-danger" disabled={busy} onClick={() => act("reject")}>Reject</button>
            <span className="muted small expires">expires {new Date(a.expiresAtUtc).toLocaleString()}</span>
          </>
        )}
      </div>
    </article>
  );
}
