import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type ChatMessage, type WorkflowEvent, type WorkflowSummary } from "../api";
import { Bunny } from "../Bunny";
import { StatusPill } from "../components";

type Item = { role: "user" | "assistant"; content: string; workflow?: WorkflowSummary };

export function Home({ lastEvent, go }: { lastEvent: WorkflowEvent | null; go: (route: string) => void }) {
  const [items, setItems] = useState<Item[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    window.kasper = { focusCommand: () => inputRef.current?.focus() };
    inputRef.current?.focus();
  }, []);

  useEffect(() => endRef.current?.scrollIntoView({ behavior: "smooth" }), [items, busy]);

  // Keep workflow cards live.
  useEffect(() => {
    if (!lastEvent) return;
    setItems((prev) =>
      prev.map((it) =>
        it.workflow?.id === lastEvent.workflowId
          ? { ...it, workflow: { ...it.workflow, status: lastEvent.status, pendingApprovals: lastEvent.pendingApprovals } }
          : it
      )
    );
  }, [lastEvent]);

  const send = useCallback(async () => {
    const text = input.trim();
    if (!text || busy) return;
    const next: Item[] = [...items, { role: "user", content: text }];
    setItems(next);
    setInput("");
    setError(null);
    setBusy(true);
    try {
      const history: ChatMessage[] = next.slice(-12).map(({ role, content }) => ({ role, content }));
      const res = await api.assistant(history);
      setItems([...next, { role: "assistant", content: res.reply ?? "", workflow: res.workflow ?? undefined }]);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Something went wrong.");
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  }, [input, busy, items]);

  return (
    <div className="home">
      <div className="conversation">
        {items.length === 0 && (
          <div className="welcome">
            <Bunny size={128} />
            <h1>Hi, I'm KASPER.</h1>
            <p>Ask me anything, or give me a task like <em>"write a packing list for a 3-day trip and save it as packing.md"</em>. I prepare everything first and wait for your approval before saving or sending.</p>
            <p className="hint">Press <kbd>Ctrl</kbd> + <kbd>Space</kbd> anywhere to open me. Type <code>kasper</code> in a terminal to talk to me there.</p>
          </div>
        )}
        {items.map((m, i) => (
          <div key={i} className={`msg msg--${m.role}`}>
            {m.role === "assistant" && <Bunny size={24} />}
            <div className="msg-body">
              {m.content && <div className="bubble">{m.content}</div>}
              {m.workflow && <WorkflowCard wf={m.workflow} go={go} />}
            </div>
          </div>
        ))}
        {busy && (
          <div className="msg msg--assistant">
            <Bunny size={24} thinking />
            <div className="bubble thinking">thinking<span>.</span><span>.</span><span>.</span></div>
          </div>
        )}
        {error && <div className="error">{error}</div>}
        <div ref={endRef} />
      </div>
      <form className="command" onSubmit={(e) => { e.preventDefault(); send(); }}>
        <span className="prompt">kasper ›</span>
        <textarea
          ref={inputRef}
          rows={1}
          value={input}
          placeholder="Ask or tell KASPER what to do…"
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              send();
            }
          }}
        />
        <button className="btn-primary" disabled={busy || !input.trim()}>Send</button>
      </form>
    </div>
  );
}

function WorkflowCard({ wf, go }: { wf: WorkflowSummary; go: (route: string) => void }) {
  return (
    <div className={`wf-card wf-card--${wf.status}`}>
      <div className="wf-card-head">
        <span className="wf-card-title">{wf.summary}</span>
        <StatusPill status={wf.status} />
      </div>
      <div className="wf-card-actions">
        {wf.status === "WaitingForApproval" && (
          <button className="btn-primary btn-sm" onClick={() => go("approvals")}>Review now</button>
        )}
        <button className="btn-ghost btn-sm" onClick={() => go(`history/${wf.id}`)}>Details</button>
      </div>
    </div>
  );
}
