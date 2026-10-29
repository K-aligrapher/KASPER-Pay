import { useCallback, useEffect, useState } from "react";
import { api, ApiError, type WorkflowDetail, type WorkflowEvent, type WorkflowSummary } from "../api";
import { Bunny } from "../Bunny";
import { StatusPill, duration, isActive, timeAgo } from "../components";

/** Every workflow with its plan, steps, results, approvals and audit trail (FR-058). */
export function History({ selected, lastEvent, go }: { selected: string | null; lastEvent: WorkflowEvent | null; go: (r: string) => void }) {
  const [list, setList] = useState<WorkflowSummary[] | null>(null);

  useEffect(() => {
    api.workflows().then(setList).catch(() => setList([]));
  }, [lastEvent]);

  return (
    <div className="history">
      <aside className="history-list">
        <h1>History</h1>
        {list?.length === 0 && <p className="muted">No workflows yet. Ask KASPER to do something!</p>}
        {list?.map((w) => (
          <button key={w.id} className={w.id === selected ? "history-item active" : "history-item"} onClick={() => go(`history/${w.id}`)}>
            <span className="history-item-title">{w.summary}</span>
            <span className="history-item-meta">
              <StatusPill status={w.status} /> <span className="muted small">{timeAgo(w.createdAtUtc)}</span>
            </span>
          </button>
        ))}
      </aside>
      <section className="history-detail">
        {selected ? <Detail id={selected} lastEvent={lastEvent} go={go} /> : (
          <div className="empty"><Bunny size={96} /><p>Pick a workflow to see exactly what happened.</p></div>
        )}
      </section>
    </div>
  );
}

function Detail({ id, lastEvent, go }: { id: string; lastEvent: WorkflowEvent | null; go: (r: string) => void }) {
  const [d, setD] = useState<WorkflowDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api.workflow(id).then(setD).catch((e) => setError(e instanceof ApiError ? e.message : "Could not load."));
  }, [id]);

  useEffect(load, [load]);
  useEffect(() => { if (lastEvent?.workflowId === id) load(); }, [lastEvent, id, load]);

  if (error) return <div className="error">{error}</div>;
  if (!d) return <p className="muted">Loading…</p>;
  const w = d.workflow;

  const act = async (fn: () => Promise<void>) => {
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Failed."); }
  };

  return (
    <div className="detail">
      <header className="detail-head">
        <div>
          <h2>{w.summary}</h2>
          <p className="muted small">“{w.requestText}” · {new Date(w.createdAtUtc).toLocaleString()}</p>
        </div>
        <div className="detail-actions">
          <StatusPill status={w.status} />
          {w.status === "WaitingForApproval" && <button className="btn-primary btn-sm" onClick={() => go("approvals")}>Review</button>}
          {isActive(w.status) && <button className="btn-danger btn-sm" onClick={() => act(() => api.cancel(id))}>Cancel</button>}
          {w.status === "Failed" && <button className="btn-ghost btn-sm" onClick={() => act(() => api.retry(id))}>Retry</button>}
        </div>
      </header>

      {w.failureSummary && <div className={w.status === "Failed" ? "error" : "notice"}>{w.failureSummary}</div>}

      <h3>Steps</h3>
      <ol className="steps">
        {d.steps.map((s) => (
          <li key={s.id} className={`step step--${s.status}`}>
            <div className="step-head">
              <span className="step-num">{s.sequence}</span>
              <span className="step-purpose">{s.purpose}</span>
              <code className="muted small">{s.toolId}</code>
              {s.requiresApproval && <span className="tag-gold">needs approval</span>}
              <StatusPill status={s.status} />
            </div>
            <div className="step-meta muted small">
              {s.attemptCount > 0 && <>attempts: {s.attemptCount} · </>}
              {duration(s.startedAtUtc, s.completedAtUtc)}
              {s.errorSummary && <span className="step-error"> · {s.errorCode}: {s.errorSummary}</span>}
            </div>
            <details>
              <summary>Input / output</summary>
              <pre className="json">{JSON.stringify({ input: s.input, output: s.output }, null, 2)}</pre>
            </details>
          </li>
        ))}
      </ol>

      {d.approvals.length > 0 && (
        <>
          <h3>Final Review</h3>
          <ul className="plain-list">
            {d.approvals.map((a) => (
              <li key={a.id}>
                <StatusPill status={a.status} /> {a.title}
                <span className="muted small"> · {a.decidedBy ? `${a.decidedBy}, ` : ""}{timeAgo(a.decidedAtUtc ?? a.requestedAtUtc)}{a.note ? ` · ${a.note}` : ""}</span>
              </li>
            ))}
          </ul>
        </>
      )}

      {d.artifacts.length > 0 && (
        <>
          <h3>Files</h3>
          <ul className="plain-list">
            {d.artifacts.map((a) => (
              <li key={a.id}>
                <code>{a.fileName}</code> <span className="muted small">{a.sizeBytes} bytes</span>
                {a.committedPath ? <span className="ok small"> · saved to {a.committedPath}</span> : a.staged ? <span className="muted small"> · in staging (not saved)</span> : <span className="muted small"> · discarded</span>}
              </li>
            ))}
          </ul>
        </>
      )}

      <details className="audit">
        <summary>Audit log ({d.audit.length})</summary>
        <table>
          <tbody>
            {d.audit.map((e, i) => (
              <tr key={i}>
                <td className="muted small">{new Date(e.timestampUtc).toLocaleTimeString()}</td>
                <td>{e.eventType}</td>
                <td className="muted small">{e.outcome}</td>
                <td className="muted small">{e.actor}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  );
}
