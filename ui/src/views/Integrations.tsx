import { useCallback, useEffect, useState } from "react";
import { api, ApiError, type Integration } from "../api";

export function Integrations() {
  const [items, setItems] = useState<Integration[]>([]);
  const [token, setToken] = useState("");
  const [database, setDatabase] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const load = useCallback(() => { api.integrations().then(setItems).catch(() => {}); }, []);
  useEffect(load, [load]);

  const notion = items.find((i) => i.name === "notion");

  const run = async (fn: () => Promise<string>) => {
    setBusy(true);
    setMessage(null);
    try {
      setMessage({ ok: true, text: await fn() });
      load();
    } catch (e) {
      setMessage({ ok: false, text: e instanceof ApiError ? e.message : "Something went wrong." });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>Integrations</h1>
        <p className="muted">KASPER only sees what you share with it, and asks before creating or changing anything.</p>
      </header>

      <section className="card integration">
        <div className="integration-head">
          <h2>Notion</h2>
          <span className={notion?.connected ? "pill pill--Succeeded" : "pill"}>{notion?.connected ? "Connected" : "Not connected"}</span>
        </div>
        {notion?.connected ? (
          <>
            <p>{notion.detail}. Try: <em>“add a Notion task to review the Q3 report”</em>.</p>
            <div className="review-actions">
              <button className="btn-ghost" disabled={busy} onClick={() => run(async () => `Notion is working (${(await api.testNotion()).database}).`)}>Test</button>
              <button className="btn-danger" disabled={busy} onClick={() => run(async () => { await api.disconnectNotion(); return "Disconnected. The token was deleted from this PC."; })}>Disconnect</button>
            </div>
          </>
        ) : (
          <>
            <ol className="steps-howto">
              <li>Open <a href="https://www.notion.so/profile/integrations" target="_blank" rel="noreferrer">notion.so/profile/integrations</a> → <b>New integration</b> (internal). Copy the <b>secret</b>.</li>
              <li>In Notion, open your task database → <b>•••</b> → <b>Connections</b> → add your integration.</li>
              <li>Copy the database link (<b>Share → Copy link</b>) and paste both below.</li>
            </ol>
            <form className="edit-form" onSubmit={(e) => { e.preventDefault(); run(async () => { const r = await api.connectNotion(token, database); setToken(""); return `Connected to ${r.workspace}. Tasks will go to “${r.database}”.`; }); }}>
              <label><span>Integration secret</span><input type="password" value={token} autoComplete="off" onChange={(e) => setToken(e.target.value)} placeholder="ntn_…" /></label>
              <label><span>Task database link</span><input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder="https://www.notion.so/…" /></label>
              <div><button className="btn-primary" disabled={busy || !token || !database}>{busy ? "Checking…" : "Connect"}</button></div>
            </form>
            <p className="muted small">The secret is encrypted for your Windows account (DPAPI) and never shown again or logged.</p>
          </>
        )}
        {message && <div className={message.ok ? "notice" : "error"}>{message.text}</div>}
      </section>

      <section className="card integration">
        <div className="integration-head"><h2>Slack</h2><span className="pill">Milestone 5</span></div>
        <p className="muted">Read chosen channels and post as a KASPER bot, always after your approval.</p>
      </section>
    </div>
  );
}
