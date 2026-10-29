import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type ChatMessage, type Ready } from "./api";
import { Bunny } from "./Bunny";

type View = "home" | "documents" | "projects" | "approvals" | "workflows" | "integrations" | "settings";

const NAV: { id: View; label: string; milestone?: string; blurb?: string }[] = [
  { id: "home", label: "Home" },
  { id: "documents", label: "Documents", milestone: "Milestone 3", blurb: "Convert any document and use the PDF toolkit: merge, split, compress, OCR and more." },
  { id: "projects", label: "Projects", milestone: "Milestone 6", blurb: "Link folders, Notion, Slack and GitHub per project, with briefings and tasks." },
  { id: "approvals", label: "Approvals", milestone: "Milestone 2", blurb: "Final Review: every save, send, update or delete waits here for you." },
  { id: "workflows", label: "History", milestone: "Milestone 2", blurb: "Every workflow with its plan, steps, results and approvals." },
  { id: "integrations", label: "Integrations", milestone: "Milestone 4", blurb: "Notion first, then Slack (as a KASPER bot), then GitHub." },
  { id: "settings", label: "Settings", milestone: "Milestone 2", blurb: "Model, folders KASPER may use, and approval preferences." },
];

export function App() {
  const [view, setView] = useState<View>("home");
  const [ready, setReady] = useState<Ready | null>(null);

  useEffect(() => {
    const load = () => api.ready().then(setReady).catch(() => setReady(null));
    load();
    const id = setInterval(load, 15000);
    return () => clearInterval(id);
  }, []);

  const current = NAV.find((n) => n.id === view)!;

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <Bunny size={32} />
          <span className="wordmark">KASPER</span>
        </div>
        <nav>
          {NAV.map((n) => (
            <button key={n.id} className={n.id === view ? "nav-item active" : "nav-item"} onClick={() => setView(n.id)}>
              {n.label}
              {n.milestone && <span className="soon">soon</span>}
            </button>
          ))}
        </nav>
        <StatusPanel ready={ready} />
      </aside>
      <main className="main">
        {view === "home" ? (
          <Home />
        ) : (
          <section className="placeholder">
            <Bunny size={96} />
            <h1>{current.label}</h1>
            <p>{current.blurb}</p>
            <span className="badge">Coming in {current.milestone}</span>
          </section>
        )}
      </main>
    </div>
  );
}

function StatusPanel({ ready }: { ready: Ready | null }) {
  if (!ready) return <div className="status"><span className="dot dot--bad" /> Not connected</div>;
  return (
    <div className="status">
      {ready.components.map((c) => (
        <div key={c.name} title={c.detail} className="status-row">
          <span className={c.ok ? "dot dot--ok" : "dot dot--bad"} />
          {c.name}
        </div>
      ))}
    </div>
  );
}

function Home() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    window.kasper = { focusCommand: () => inputRef.current?.focus() };
    inputRef.current?.focus();
  }, []);

  useEffect(() => endRef.current?.scrollIntoView({ behavior: "smooth" }), [messages, busy]);

  const send = useCallback(async () => {
    const text = input.trim();
    if (!text || busy) return;
    const next: ChatMessage[] = [...messages, { role: "user", content: text }];
    setMessages(next);
    setInput("");
    setError(null);
    setBusy(true);
    try {
      const res = await api.chat(next.slice(-20));
      setMessages([...next, { role: "assistant", content: res.content }]);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Something went wrong.");
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  }, [input, busy, messages]);

  return (
    <div className="home">
      <div className="conversation">
        {messages.length === 0 && (
          <div className="welcome">
            <Bunny size={128} />
            <h1>Hi, I'm KASPER.</h1>
            <p>Your desktop coworker. Ask me anything. Soon I'll convert documents, run your projects and update Notion and Slack, and I'll always wait for your approval before anything is saved or sent.</p>
            <p className="hint">Press <kbd>Ctrl</kbd> + <kbd>Space</kbd> anywhere to open me. Type <code>kasper</code> in a terminal to talk to me there.</p>
          </div>
        )}
        {messages.map((m, i) => (
          <div key={i} className={`msg msg--${m.role}`}>
            {m.role === "assistant" && <Bunny size={24} />}
            <div className="bubble">{m.content}</div>
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
      <form
        className="command"
        onSubmit={(e) => {
          e.preventDefault();
          send();
        }}
      >
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
