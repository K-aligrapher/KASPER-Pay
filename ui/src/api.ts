// Thin client for the local KASPER API. The desktop shell injects the per-launch token
// into window.__KASPER__ before the page loads; it never reaches localStorage or the URL.

declare global {
  interface Window {
    __KASPER__?: { token: string };
    kasper?: { focusCommand: () => void };
  }
}

export type ChatMessage = { role: "user" | "assistant"; content: string };
export type ComponentStatus = { name: string; ok: boolean; detail: string };
export type Ready = { ready: boolean; components: ComponentStatus[] };

export type WorkflowStatus = "Pending" | "Running" | "WaitingForApproval" | "Succeeded" | "Failed" | "Cancelled";

export type WorkflowSummary = {
  id: string;
  summary: string;
  requestText: string;
  status: WorkflowStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
  completedAtUtc: string | null;
  failureCode: string | null;
  failureSummary: string | null;
  pendingApprovals: number;
  stepCount: number;
};

export type Step = {
  id: string;
  sequence: number;
  key: string;
  toolId: string;
  purpose: string;
  effect: string;
  requiresApproval: boolean;
  status: string;
  input: unknown;
  output: unknown;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  attemptCount: number;
  errorCode: string | null;
  errorSummary: string | null;
};

export type EditableField = { name: string; description: string; type: string; value: string | null; options: { value: string; label: string }[] | null };

export type Approval = {
  id: string;
  workflowId: string;
  stepId: string;
  workflowSummary: string;
  title: string;
  target: string;
  status: string;
  requestedAtUtc: string;
  expiresAtUtc: string;
  decidedAtUtc: string | null;
  decidedBy: string | null;
  note: string | null;
  fields: { label: string; value: string }[];
  content: string | null;
  warnings: string[];
  editable: EditableField[];
  previewArtifactId: string | null;
};

export type WorkflowDetail = {
  workflow: WorkflowSummary;
  steps: Step[];
  approvals: Approval[];
  artifacts: { id: string; fileName: string; sizeBytes: number; sha256: string; committedPath: string | null; staged: boolean }[];
  audit: { timestampUtc: string; actor: string; eventType: string; target: string | null; outcome: string }[];
};

export type AssistantResponse = { kind: "reply" | "workflow"; reply: string | null; workflow: WorkflowSummary | null };
export type Folder = { id: number; ref: string; path: string; isDefault: boolean; exists: boolean };
export type WorkflowEvent = { workflowId: string; status: WorkflowStatus; summary: string; pendingApprovals: number };

export class ApiError extends Error {
  constructor(public code: string, message: string, public details: string[] = []) {
    super(message);
  }
}

function token(): string {
  const t = window.__KASPER__?.token;
  if (!t) throw new ApiError("no_session", "Open KASPER from the desktop app to connect.");
  return t;
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token()}`, ...init?.headers },
  });
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw new ApiError(body?.code ?? `http_${res.status}`, body?.message ?? `Request failed (${res.status}).`, body?.details ?? []);
  }
  if (res.status === 204 || res.status === 202) return undefined as T;
  return res.json();
}

const post = <T>(path: string, body?: unknown) =>
  call<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });

export const api = {
  ready: () => call<Ready>("/ready"),
  assistant: (messages: ChatMessage[]) => post<AssistantResponse>("/api/assistant", { messages }),
  workflows: (take = 100) => call<WorkflowSummary[]>(`/api/workflows?take=${take}`),
  workflow: (id: string) => call<WorkflowDetail>(`/api/workflows/${id}`),
  cancel: (id: string) => post<void>(`/api/workflows/${id}/cancel`),
  retry: (id: string) => post<void>(`/api/workflows/${id}/retry`),
  approvals: () => call<Approval[]>("/api/approvals"),
  decide: (id: string, decision: "approve" | "reject" | "edit", edits?: Record<string, string>) =>
    post<void>(`/api/approvals/${id}/decision`, { decision, edits }),
  folders: () => call<Folder[]>("/api/settings/folders"),
  addFolder: (path: string) => post<Folder>("/api/settings/folders", { path }),
  removeFolder: (id: number) => call<void>(`/api/settings/folders/${id}`, { method: "DELETE" }),
  engines: () => call<{ engines: { name: string; installed: boolean; install: string }[] }>("/api/documents/capabilities"),
  targets: (ext: string) => call<{ to: string; lossy: boolean }[]>(`/api/documents/targets?ext=${encodeURIComponent(ext)}`),
  upload: async (files: File[]) => {
    const form = new FormData();
    files.forEach((f) => form.append("files", f, f.name));
    const res = await fetch("/api/documents/upload", { method: "POST", body: form, headers: { Authorization: `Bearer ${token()}` } });
    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new ApiError(body?.code ?? "upload_failed", body?.message ?? "Upload failed.");
    }
    return (await res.json()) as Upload[];
  },
  job: (job: DocumentJob) => post<WorkflowSummary>("/api/documents/jobs", job),
  integrations: () => call<Integration[]>("/api/integrations"),
  connectNotion: (token: string, database: string) => post<{ workspace: string; database: string }>("/api/integrations/notion", { token, database }),
  testNotion: () => post<{ database: string }>("/api/integrations/notion/test"),
  disconnectNotion: () => call<void>("/api/integrations/notion", { method: "DELETE" }),
};

export type Project = {
  id: string; name: string; description: string; status: string; folderPath: string | null; notionQuery: string | null;
  slackChannel: string | null; gitHubRepo: string | null; notes: string; lastBriefing: string | null; lastBriefingAtUtc: string | null; updatedAtUtc: string;
};
export type Schedule = { id: string; name: string; request: string; timeOfDay: string; days: string; enabled: boolean; lastRunUtc: string | null; lastWorkflowId: string | null };

export const api2 = {
  slack: () => call<{ connected: boolean; team: string; channels: string[] }>("/api/integrations/slack"),
  connectSlack: (token: string, channels: string) => post<{ team: string; channels: string[] }>("/api/integrations/slack", { token, channels }),
  disconnectSlack: () => call<void>("/api/integrations/slack", { method: "DELETE" }),
  github: () => call<{ connected: boolean; login: string | null }>("/api/integrations/github"),
  connectGitHub: (token: string) => post<{ login: string }>("/api/integrations/github", { token }),
  disconnectGitHub: () => call<void>("/api/integrations/github", { method: "DELETE" }),
  projects: () => call<Project[]>("/api/projects"),
  saveProject: (p: Partial<Project>) =>
    p.id ? call<Project>(`/api/projects/${p.id}`, { method: "PUT", body: JSON.stringify(p) }) : post<Project>("/api/projects", p),
  deleteProject: (id: string) => call<void>(`/api/projects/${id}`, { method: "DELETE" }),
  briefing: (id: string) => post<Project>(`/api/projects/${id}/briefing`),
  schedules: () => call<Schedule[]>("/api/schedules"),
  addSchedule: (s: Partial<Schedule>) => post<Schedule>("/api/schedules", { enabled: true, days: "daily", ...s }),
  toggleSchedule: (id: string, enabled: boolean) => call<Schedule>(`/api/schedules/${id}/enabled?enabled=${enabled}`, { method: "PUT" }),
  deleteSchedule: (id: string) => call<void>(`/api/schedules/${id}`, { method: "DELETE" }),
  mcp: () => call<{ servers: { name: string; running: boolean; tools: number; error: string | null }[]; tools: { id: string; description: string; requiresApproval: boolean }[] }>("/api/mcp"),
  upload: (token: string) => call<Upload>(`/api/documents/uploads/${token}`),
  model: () => call<{ current: string; installed: string[] }>("/api/settings/model"),
  setModel: (model: string) => call<{ current: string }>("/api/settings/model", { method: "PUT", body: JSON.stringify({ model }) }),
  tasks: (projectId: string) => call<ProjectTask[]>(`/api/projects/${projectId}/tasks`),
  addTask: (projectId: string, title: string) => post<ProjectTask>(`/api/projects/${projectId}/tasks`, { title }),
  updateTask: (projectId: string, id: string, edit: { title?: string; done?: boolean }) =>
    call<ProjectTask>(`/api/projects/${projectId}/tasks/${id}`, { method: "PUT", body: JSON.stringify(edit) }),
  deleteTask: (projectId: string, id: string) => call<void>(`/api/projects/${projectId}/tasks/${id}`, { method: "DELETE" }),
  taskToNotion: (projectId: string, id: string) => post<ProjectTask>(`/api/projects/${projectId}/tasks/${id}/notion`),
  payments: () => call<Payment[]>("/api/payments"),
  guardrails: () => call<Guardrails>("/api/settings/guardrails"),
  setGuardrails: (g: Guardrails) => call<Guardrails>("/api/settings/guardrails", { method: "PUT", body: JSON.stringify(g) }),
  /** Object URL for a staged PDF/image (for Final Review), or null if it can't be previewed. */
  previewUrl: async (artifactId: string) => {
    const res = await fetch(`/api/artifacts/${artifactId}/preview`, { headers: { Authorization: `Bearer ${token()}` } });
    return res.ok ? URL.createObjectURL(await res.blob()) : null;
  },
};

export type Payment = {
  id: string; workflowId: string | null; kind: "purchase" | "invoice"; status: string; item: string; merchant: string;
  recipient: string | null; amount: number; currency: string; reason: string | null; blockedRule: string | null;
  blockedMessage: string | null; payPalOrderId: string | null; payPalCaptureId: string | null; payPalInvoiceId: string | null;
  failureSummary: string | null; createdAtUtc: string; updatedAtUtc: string;
};
export type Guardrails = { perTransactionLimit: number; dailyLimit: number; duplicateWindowMinutes: number; currency: string };

export type ProjectTask = { id: string; projectId: string; title: string; done: boolean; createdAtUtc: string; doneAtUtc: string | null; notionWorkflowId: string | null };

export type Upload = { upload: string; fileName: string; sizeBytes: number; targets: string[] };
export type DocumentJob = {
  operation: string; uploads: string[]; to?: string; pages?: string; degrees?: number; quality?: string; fileName?: string;
  watermark?: string; pageNumbers?: boolean; password?: string; language?: string;
  format?: string; dpi?: number; title?: string; author?: string; subject?: string; keywords?: string;
};
export type Integration = { name: string; connected: boolean; detail: string };

/**
 * Live workflow updates (Server-Sent Events over fetch, so the token stays in a header).
 * Reconnects automatically. Returns an unsubscribe function.
 */
export function subscribeEvents(onEvent: (e: WorkflowEvent) => void): () => void {
  let stopped = false;
  let controller: AbortController | null = null;

  const run = async () => {
    while (!stopped) {
      controller = new AbortController();
      try {
        const res = await fetch("/api/events", { headers: { Authorization: `Bearer ${token()}` }, signal: controller.signal });
        const reader = res.body!.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let idx;
          while ((idx = buffer.indexOf("\n\n")) >= 0) {
            const chunk = buffer.slice(0, idx);
            buffer = buffer.slice(idx + 2);
            const data = chunk.split("\n").find((l) => l.startsWith("data: "));
            if (data) onEvent(JSON.parse(data.slice(6)));
          }
        }
      } catch {
        /* reconnect below */
      }
      if (!stopped) await new Promise((r) => setTimeout(r, 2000));
    }
  };
  run();
  return () => {
    stopped = true;
    controller?.abort();
  };
}
