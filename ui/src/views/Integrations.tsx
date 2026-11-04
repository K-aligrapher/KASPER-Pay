import { useCallback, useEffect, useState } from "react";
import { api, api2, ApiError, type Integration } from "../api";

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

      <SlackCard />
      <GitHubCard />
    </div>
  );
}

function useAction() {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const run = async (fn: () => Promise<string>, after?: () => void) => {
    setBusy(true);
    setMessage(null);
    try { setMessage({ ok: true, text: await fn() }); after?.(); }
    catch (e) { setMessage({ ok: false, text: e instanceof ApiError ? e.message : "Something went wrong." }); }
    finally { setBusy(false); }
  };
  return { busy, message, run };
}

function SlackCard() {
  const [state, setState] = useState<{ connected: boolean; team: string; channels: string[] } | null>(null);
  const [token, setToken] = useState("");
  const [channels, setChannels] = useState("");
  const { busy, message, run } = useAction();
  const load = useCallback(() => { api2.slack().then(setState).catch(() => {}); }, []);
  useEffect(load, [load]);

  return (
    <section className="card integration">
      <div className="integration-head">
        <h2>Slack</h2>
        <span className={state?.connected ? "pill pill--Succeeded" : "pill"}>{state?.connected ? "Connected" : "Not connected"}</span>
      </div>
      {state?.connected ? (
        <>
          <p>KASPER bot in <b>{state.team}</b> may use: {state.channels.join(", ")}. Try: <em>“summarise #{state.channels[0]?.slice(1)} and post the summary there”</em>.</p>
          <div><button className="btn-danger" disabled={busy} onClick={() => run(async () => { await api2.disconnectSlack(); return "Disconnected. The token was deleted."; }, load)}>Disconnect</button></div>
        </>
      ) : (
        <>
          <ol className="steps-howto">
            <li>Go to <a href="https://api.slack.com/apps" target="_blank" rel="noreferrer">api.slack.com/apps</a> → <b>Create New App</b> → From scratch.</li>
            <li><b>OAuth &amp; Permissions</b> → Bot Token Scopes: <code>channels:read</code>, <code>channels:history</code>, <code>groups:read</code>, <code>groups:history</code>, <code>chat:write</code>. Install to workspace, copy the <b>Bot User OAuth Token</b> (xoxb-…).</li>
            <li>In each channel KASPER may use, type <code>/invite @your-app</code>.</li>
          </ol>
          <form className="edit-form" onSubmit={(e) => { e.preventDefault(); run(async () => { const r = await api2.connectSlack(token, channels); setToken(""); return `Connected to ${r.team}: ${r.channels.join(", ")}`; }, load); }}>
            <label><span>Bot token</span><input type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} placeholder="xoxb-…" /></label>
            <label><span>Allowed channels</span><input value={channels} onChange={(e) => setChannels(e.target.value)} placeholder="#team, #updates" /></label>
            <div><button className="btn-primary" disabled={busy || !token || !channels}>{busy ? "Checking…" : "Connect"}</button></div>
          </form>
          <p className="muted small">KASPER posts as the bot, never as you, and only after you approve each message. The token is encrypted for your Windows account.</p>
        </>
      )}
      {message && <div className={message.ok ? "notice" : "error"}>{message.text}</div>}
    </section>
  );
}

function GitHubCard() {
  const [state, setState] = useState<{ connected: boolean; login: string | null } | null>(null);
  const [token, setToken] = useState("");
  const { busy, message, run } = useAction();
  const load = useCallback(() => { api2.github().then(setState).catch(() => {}); }, []);
  useEffect(load, [load]);

  return (
    <section className="card integration">
      <div className="integration-head">
        <h2>GitHub <span className="muted small">read-only</span></h2>
        <span className={state?.connected ? "pill pill--Succeeded" : "pill"}>{state?.connected ? `@${state.login}` : "Public repos only"}</span>
      </div>
      <p className="muted">Link repos to Projects to see open PRs, issues and commits in briefings. Public repos work without a token (60 requests/hour).</p>
      {state?.connected ? (
        <div><button className="btn-danger" disabled={busy} onClick={() => run(async () => { await api2.disconnectGitHub(); return "Token removed."; }, load)}>Remove token</button></div>
      ) : (
        <form className="inline-form" onSubmit={(e) => { e.preventDefault(); run(async () => { const r = await api2.connectGitHub(token); setToken(""); return `Signed in as ${r.login}.`; }, load); }}>
          <input type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} placeholder="Fine-grained token with read-only access (optional)" />
          <button className="btn-primary" disabled={busy || !token}>Add token</button>
        </form>
      )}
      {message && <div className={message.ok ? "notice" : "error"}>{message.text}</div>}
    </section>
  );
}
