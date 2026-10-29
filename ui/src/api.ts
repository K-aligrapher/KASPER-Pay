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
};

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
