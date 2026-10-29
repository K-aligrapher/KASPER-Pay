import { useCallback, useEffect, useState } from "react";
import { api, ApiError, type Folder, type Ready } from "../api";

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

      <section className="card">
        <h2>Status</h2>
        <ul className="plain-list">
          {ready?.components.map((c) => (
            <li key={c.name}><span className={c.ok ? "dot dot--ok" : "dot dot--bad"} /> {c.name} <span className="muted small">— {c.detail}</span></li>
          ))}
        </ul>
        <p className="muted small">Change the model in <code>%LOCALAPPDATA%\KASPER\settings.json</code> (see README).</p>
      </section>
    </div>
  );
}
