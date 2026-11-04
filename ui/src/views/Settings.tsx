import { useCallback, useEffect, useState } from "react";
import { api, api2, ApiError, type Folder, type Ready, type Schedule } from "../api";

export function Settings({ ready }: { ready: Ready | null }) {
  const [folders, setFolders] = useState<Folder[]>([]);
  const [path, setPath] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => { api.folders().then(setFolders).catch(() => {}); }, []);
  useEffect(load, [load]);

  const add = async () => {
    setError(null);
    try {
      await api.addFolder(path.trim().replace(/^"|"$/g, ""));
      setPath("");
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not add folder.");
    }
  };

  const remove = async (id: number) => {
    try { await api.removeFolder(id); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Could not remove."); }
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>Settings</h1>
      </header>

      <section className="card">
        <h2>Folders KASPER may save into</h2>
        <p className="muted">KASPER can only save files into these folders, and only after you approve. It never overwrites a file.</p>
        <ul className="folder-list">
          {folders.map((f) => (
            <li key={f.id}>
              <code>{f.path}</code>
              {f.isDefault && <span className="tag-gold">default</span>}
              {!f.exists && <span className="warning-text">missing</span>}
              {!f.isDefault && <button className="btn-ghost btn-sm" onClick={() => remove(f.id)}>Remove</button>}
            </li>
          ))}
        </ul>
        <form className="inline-form" onSubmit={(e) => { e.preventDefault(); add(); }}>
          <input value={path} placeholder="C:\Users\you\Documents\Reports" onChange={(e) => setPath(e.target.value)} />
          <button className="btn-primary" disabled={!path.trim()}>Add folder</button>
        </form>
        {error && <div className="error">{error}</div>}
      </section>

      <Schedules />
      <McpServers />

      <section className="card">
        <h2>Status</h2>
        <ul className="plain-list">
          {ready?.components.map((c) => (
            <li key={c.name}><span className={c.ok ? "dot dot--ok" : "dot dot--bad"} /> {c.name} <span className="muted small">— {c.detail}</span></li>
          ))}
        </ul>
        <ModelPicker />
      </section>
    </div>
  );
}

function Schedules() {
  const [items, setItems] = useState<Schedule[]>([]);
  const [draft, setDraft] = useState({ name: "", request: "", timeOfDay: "09:00", days: "weekdays" });
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => { api2.schedules().then(setItems).catch(() => {}); }, []);
  useEffect(load, [load]);

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Failed."); }
  };

  return (
    <section className="card">
      <h2>Scheduled requests</h2>
      <p className="muted">KASPER runs these while it's open. Anything that would save, send or update still waits for your approval.</p>
      <ul className="folder-list">
        {items.map((s) => (
          <li key={s.id}>
            <input type="checkbox" checked={s.enabled} onChange={(e) => act(() => api2.toggleSchedule(s.id, e.target.checked))} />
            <b>{s.name}</b> <span className="muted small">{s.days} at {s.timeOfDay} — “{s.request}”{s.lastRunUtc ? ` · last run ${new Date(s.lastRunUtc).toLocaleString()}` : ""}</span>
            <button className="btn-ghost btn-sm" onClick={() => act(() => api2.deleteSchedule(s.id))}>Remove</button>
          </li>
        ))}
      </ul>
      <form className="inline-form" onSubmit={(e) => { e.preventDefault(); act(async () => { await api2.addSchedule(draft); setDraft({ ...draft, name: "", request: "" }); }); }}>
        <input style={{ maxWidth: 160 }} value={draft.name} placeholder="Name" onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        <input value={draft.request} placeholder="e.g. summarise #team and post it to #updates" onChange={(e) => setDraft({ ...draft, request: e.target.value })} />
        <input style={{ maxWidth: 90 }} type="time" value={draft.timeOfDay} onChange={(e) => setDraft({ ...draft, timeOfDay: e.target.value })} />
        <select value={draft.days} onChange={(e) => setDraft({ ...draft, days: e.target.value })}>
          <option value="daily">Daily</option><option value="weekdays">Weekdays</option><option value="Mon">Mondays</option><option value="Fri">Fridays</option>
        </select>
        <button className="btn-primary" disabled={!draft.name || !draft.request}>Add</button>
      </form>
      {error && <div className="error">{error}</div>}
    </section>
  );
}

function McpServers() {
  const [data, setData] = useState<Awaited<ReturnType<typeof api2.mcp>> | null>(null);
  useEffect(() => { api2.mcp().then(setData).catch(() => {}); }, []);
  return (
    <section className="card">
      <h2>MCP tool servers</h2>
      <p className="muted small">Add trusted servers in <code>%LOCALAPPDATA%\KASPER\settings.json</code> under <code>Mcp.Servers</code> (see README). Only tools you list in <code>AllowedTools</code> are exposed, and they ask for approval unless listed in <code>ReadOnlyTools</code>.</p>
      {data && data.servers.length === 0 && <p className="muted">No MCP servers configured.</p>}
      <ul className="plain-list">
        {data?.servers.map((s) => (
          <li key={s.name}><span className={s.running ? "dot dot--ok" : "dot dot--bad"} /> {s.name} <span className="muted small">— {s.running ? `${s.tools} tool(s)` : s.error}</span></li>
        ))}
        {data?.tools.map((t) => <li key={t.id} className="small"><code>{t.id}</code> {t.requiresApproval && <span className="tag-gold">needs approval</span>}</li>)}
      </ul>
    </section>
  );
}

/** AI model picker: any model installed in Ollama. Applies immediately. */
function ModelPicker() {
  const [state, setState] = useState<{ current: string; installed: string[] } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  useEffect(() => { api2.model().then(setState).catch(() => {}); }, []);
  if (!state) return null;
  return (
    <div className="model-picker">
      <label>AI model{" "}
        <select value={state.current} onChange={async (e) => {
          const model = e.target.value;
          setMsg(null);
          try { await api2.setModel(model); setState({ ...state, current: model }); setMsg(`Now using ${model}. The first reply may take a moment while it loads.`); }
          catch (err) { setMsg(err instanceof ApiError ? err.message : "Could not change the model."); }
        }}>
          {!state.installed.includes(state.current) && <option value={state.current}>{state.current} (not installed)</option>}
          {state.installed.map((m) => <option key={m} value={m}>{m}</option>)}
        </select>
      </label>
      {msg && <p className="muted small">{msg}</p>}
    </div>
  );
}
