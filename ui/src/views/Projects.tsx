import { useCallback, useEffect, useState } from "react";
import { api2, ApiError, type Project, type ProjectTask } from "../api";
import { Bunny } from "../Bunny";
import { timeAgo } from "../components";

const EMPTY: Partial<Project> = { name: "", description: "", status: "Active", folderPath: "", notionQuery: "", slackChannel: "", gitHubRepo: "", notes: "" };

/** Projects (FR-300…306): links to folder, Notion, Slack and GitHub, notes, and AI briefings. */
export function Projects({ selected, go }: { selected: string | null; go: (r: string) => void }) {
  const [list, setList] = useState<Project[]>([]);
  const [draft, setDraft] = useState<Partial<Project> | null>(null);
  const [channels, setChannels] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => { api2.projects().then(setList).catch(() => {}); }, []);
  useEffect(load, [load]);
  useEffect(() => { api2.slack().then((s) => setChannels(s.channels)).catch(() => {}); }, []);

  const current = list.find((p) => p.id === selected) ?? null;

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Something went wrong."); } finally { setBusy(false); }
  };

  return (
    <div className="history">
      <aside className="history-list">
        <h1>Projects</h1>
        <button className="btn-primary btn-sm" onClick={() => { setDraft({ ...EMPTY }); go("projects"); }}>+ New project</button>
        {list.map((p) => (
          <button key={p.id} className={p.id === selected ? "history-item active" : "history-item"} onClick={() => { setDraft(null); go(`projects/${p.id}`); }}>
            <span className="history-item-title">{p.name}</span>
            <span className="muted small">{p.status}{p.gitHubRepo ? ` · ${p.gitHubRepo}` : ""}</span>
          </button>
        ))}
      </aside>
      <section className="history-detail">
        {error && <div className="error">{error}</div>}
        {draft ? (
          <ProjectForm draft={draft} channels={channels} busy={busy} onChange={setDraft} onCancel={() => setDraft(null)}
            onSave={() => run(async () => { const saved = await api2.saveProject(draft); setDraft(null); go(`projects/${saved.id}`); })} />
        ) : current ? (
          <div className="detail">
            <header className="detail-head">
              <div>
                <h2>{current.name}</h2>
                <p className="muted small">{current.description}</p>
              </div>
              <div className="detail-actions">
                <button className="btn-primary btn-sm" disabled={busy} onClick={() => run(() => api2.briefing(current.id))}>{busy ? "Briefing…" : "Briefing"}</button>
                <button className="btn-ghost btn-sm" onClick={() => setDraft({ ...current })}>Edit</button>
                <button className="btn-danger btn-sm" disabled={busy} onClick={() => run(async () => { await api2.deleteProject(current.id); go("projects"); })}>Delete</button>
              </div>
            </header>
            <dl className="fields">
              <div><dt>Folder</dt><dd>{current.folderPath ?? "—"}</dd></div>
              <div><dt>GitHub</dt><dd>{current.gitHubRepo ? <a href={`https://github.com/${current.gitHubRepo}`} target="_blank" rel="noreferrer">{current.gitHubRepo}</a> : "—"}</dd></div>
              <div><dt>Slack</dt><dd>{current.slackChannel ?? "—"}</dd></div>
              <div><dt>Notion search</dt><dd>{current.notionQuery ?? current.name}</dd></div>
            </dl>
            <h3>Briefing {current.lastBriefingAtUtc && <span className="muted small">· {timeAgo(current.lastBriefingAtUtc)}</span>}</h3>
            {busy && <p className="muted"><Bunny size={24} thinking /> Gathering files, GitHub, Slack and Notion, then summarising… (up to a minute)</p>}
            {current.lastBriefing ? <pre className="content-preview briefing">{current.lastBriefing}</pre> : <p className="muted">Click Briefing for a summary of what changed, blockers and next actions.</p>}
            <h3>Tasks</h3>
            <TaskList project={current} go={go} />
            <h3>Notes</h3>
            <NotesEditor project={current} onSave={(notes) => run(() => api2.saveProject({ ...current, notes }))} />
          </div>
        ) : (
          <div className="empty"><Bunny size={96} /><p>Create a project to link its folder, GitHub repo, Slack channel and Notion pages.</p></div>
        )}
      </section>
    </div>
  );
}

/** Local task list (FR-304). Ticking boxes here applies directly; sending to Notion asks first. */
function TaskList({ project, go }: { project: Project; go: (r: string) => void }) {
  const [tasks, setTasks] = useState<ProjectTask[]>([]);
  const [title, setTitle] = useState("");
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => { api2.tasks(project.id).then(setTasks).catch(() => {}); }, [project.id]);
  useEffect(load, [load]);
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Something went wrong."); }
  };
  const open = tasks.filter((t) => !t.done).length;
  return (
    <div className="task-list">
      {error && <div className="error">{error}</div>}
      <form className="task-add" onSubmit={(e) => { e.preventDefault(); if (title.trim()) act(async () => { await api2.addTask(project.id, title); setTitle(""); }); }}>
        <input value={title} placeholder="Add a task…" maxLength={300} onChange={(e) => setTitle(e.target.value)} />
        <button className="btn-primary btn-sm" disabled={!title.trim()}>Add</button>
      </form>
      {tasks.length === 0 && <p className="muted small">No tasks yet. You can also ask KASPER: “add a task to {project.name}: …”.</p>}
      <ul>
        {tasks.map((t) => (
          <li key={t.id} className={t.done ? "done" : ""}>
            <label>
              <input type="checkbox" checked={t.done} onChange={() => act(() => api2.updateTask(project.id, t.id, { done: !t.done }))} />
              <span>{t.title}</span>
            </label>
            <span className="task-actions">
              {t.notionWorkflowId
                ? <button className="btn-ghost btn-sm" onClick={() => go(`history/${t.notionWorkflowId}`)}>In Notion ↗</button>
                : <button className="btn-ghost btn-sm" title="Create it in your Notion task database (asks you first)" onClick={() => act(() => api2.taskToNotion(project.id, t.id))}>→ Notion</button>}
              <button className="btn-ghost btn-sm" title="Delete" onClick={() => act(() => api2.deleteTask(project.id, t.id))}>✕</button>
            </span>
          </li>
        ))}
      </ul>
      {tasks.length > 0 && <p className="muted small">{open} open · {tasks.length - open} done</p>}
    </div>
  );
}

function NotesEditor({ project, onSave }: { project: Project; onSave: (n: string) => void }) {
  const [notes, setNotes] = useState(project.notes);
  useEffect(() => setNotes(project.notes), [project.id, project.notes]);
  return (
    <div className="edit-form">
      <textarea className="notes" rows={8} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="Markdown notes, stored locally…" />
      <div><button className="btn-ghost btn-sm" disabled={notes === project.notes} onClick={() => onSave(notes)}>Save notes</button></div>
    </div>
  );
}

function ProjectForm({ draft, channels, busy, onChange, onSave, onCancel }: {
  draft: Partial<Project>; channels: string[]; busy: boolean; onChange: (p: Partial<Project>) => void; onSave: () => void; onCancel: () => void;
}) {
  const field = (key: keyof Project, label: string, placeholder = "") => (
    <label><span>{label}</span><input value={(draft[key] as string) ?? ""} placeholder={placeholder} onChange={(e) => onChange({ ...draft, [key]: e.target.value })} /></label>
  );
  return (
    <form className="edit-form card" onSubmit={(e) => { e.preventDefault(); onSave(); }}>
      <h2>{draft.id ? "Edit project" : "New project"}</h2>
      {field("name", "Name", "KASPER")}
      {field("description", "Description")}
      {field("folderPath", "Local folder (added to allowed folders)", "C:\\Users\\you\\Projects\\…")}
      {field("gitHubRepo", "GitHub repo", "owner/name")}
      <label><span>Slack channel</span>
        <select value={draft.slackChannel ?? ""} onChange={(e) => onChange({ ...draft, slackChannel: e.target.value })}>
          <option value="">— none —</option>
          {channels.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
      </label>
      {field("notionQuery", "Notion search words (default: project name)")}
      <div className="review-actions">
        <button className="btn-primary" disabled={busy || !draft.name?.trim()}>Save</button>
        <button type="button" className="btn-ghost" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
