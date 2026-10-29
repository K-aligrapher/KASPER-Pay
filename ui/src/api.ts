// Thin client for the local KASPER API. The desktop shell injects the per-launch token
// into window.__KASPER__ before the page loads; it never reaches localStorage or the URL.

declare global {
  interface Window {
    __KASPER__?: { token: string };
    kasper?: { focusCommand: () => void };
  }
}

export type ChatMessage = { role: "user" | "assistant"; content: string };
export type ChatResponse = { content: string; model: string; durationSeconds: number };
export type ComponentStatus = { name: string; ok: boolean; detail: string };
export type Ready = { ready: boolean; components: ComponentStatus[] };

export class ApiError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const token = window.__KASPER__?.token;
  if (!token) throw new ApiError("no_session", "Open KASPER from the desktop app to connect.");
  const res = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, ...init?.headers },
  });
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw new ApiError(body?.code ?? `http_${res.status}`, body?.message ?? `Request failed (${res.status}).`);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

export const api = {
  ready: () => call<Ready>("/ready"),
  chat: (messages: ChatMessage[]) =>
    call<ChatResponse>("/api/ai/chat", { method: "POST", body: JSON.stringify({ messages }) }),
};
