import { useCallback, useEffect, useState } from "react";
import { api, subscribeEvents, type Ready, type WorkflowEvent } from "./api";
import { Bunny } from "./Bunny";
import { Approvals } from "./views/Approvals";
import { History } from "./views/History";
import { Home } from "./views/Home";
import { Settings } from "./views/Settings";
import { Documents } from "./views/Documents";
import { Integrations } from "./views/Integrations";
import { Projects } from "./views/Projects";

type View = "home" | "documents" | "projects" | "approvals" | "history" | "integrations" | "settings";

const NAV: { id: View; label: string; milestone?: string; blurb?: string }[] = [
  { id: "home", label: "Home" },
  { id: "approvals", label: "Approvals" },
  { id: "history", label: "History" },
  { id: "documents", label: "Documents" },
  { id: "projects", label: "Projects" },
  { id: "integrations", label: "Integrations" },
  { id: "settings", label: "Settings" },
];

function parseHash(): { view: View; param: string | null } {
  const [view, param] = location.hash.replace(/^#/, "").split("/");
  return NAV.some((n) => n.id === view) ? { view: view as View, param: param ?? null } : { view: "home", param: null };
}

export function App() {
  const [route, setRoute] = useState(parseHash);
  const [ready, setReady] = useState<Ready | null>(null);
  const [pending, setPending] = useState(0);
  const [lastEvent, setLastEvent] = useState<WorkflowEvent | null>(null);

  const go = useCallback((r: string) => { location.hash = r; }, []);

  useEffect(() => {
    const onHash = () => setRoute(parseHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const refreshPending = useCallback(() => api.approvals().then((a) => setPending(a.length)).catch(() => {}), []);

  useEffect(() => {
    const load = () => api.ready().then(setReady).catch(() => setReady(null));
    load();
    refreshPending();
    const id = setInterval(load, 15000);
    const unsubscribe = subscribeEvents((e) => {
      setLastEvent(e);
      refreshPending();
    });
    return () => {
      clearInterval(id);
      unsubscribe();
    };
  }, [refreshPending]);

  const current = NAV.find((n) => n.id === route.view)!;

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <Bunny size={32} />
          <span className="wordmark">KASPER</span>
        </div>
        <nav>
          {NAV.map((n) => (
            <button key={n.id} className={n.id === route.view ? "nav-item active" : "nav-item"} onClick={() => go(n.id)}>
              {n.label}
              {n.id === "approvals" && pending > 0 && <span className="badge-count">{pending}</span>}
              {n.milestone && <span className="soon">soon</span>}
            </button>
          ))}
        </nav>
        <StatusPanel ready={ready} />
      </aside>
      <main className="main">
        {/* Home stays mounted so the conversation survives switching views. */}
        <div className="view" hidden={route.view !== "home"}>
          <Home lastEvent={lastEvent} go={go} />
        </div>
        {route.view === "approvals" && <div className="view"><Approvals lastEvent={lastEvent} go={go} /></div>}
        {route.view === "history" && <div className="view"><History selected={route.param} lastEvent={lastEvent} go={go} /></div>}
        {route.view === "documents" && <div className="view"><Documents go={go} preload={route.param} /></div>}
        {route.view === "projects" && <div className="view"><Projects selected={route.param} go={go} /></div>}
        {route.view === "integrations" && <div className="view"><Integrations /></div>}
        {route.view === "settings" && <div className="view"><Settings ready={ready} /></div>}
        {current.milestone && (
          <div className="view">
            <section className="placeholder">
              <Bunny size={96} />
              <h1>{current.label}</h1>
              <p>{current.blurb}</p>
              <span className="badge">Coming in {current.milestone}</span>
            </section>
          </div>
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
