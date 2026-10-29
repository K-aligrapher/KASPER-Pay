import type { WorkflowStatus } from "./api";

const LABELS: Record<string, string> = {
  Pending: "Queued",
  Running: "Working",
  WaitingForApproval: "Needs your approval",
  Succeeded: "Done",
  Failed: "Failed",
  Cancelled: "Cancelled",
  Approved: "Approved",
  Rejected: "Rejected",
  Invalidated: "Replaced",
  Expired: "Expired",
};

export function StatusPill({ status }: { status: WorkflowStatus | string }) {
  return <span className={`pill pill--${status}`}>{LABELS[status] ?? status}</span>;
}

export function timeAgo(iso: string | null): string {
  if (!iso) return "";
  const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 45) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return new Date(iso).toLocaleDateString();
}

export function duration(from: string | null, to: string | null): string {
  if (!from || !to) return "";
  const ms = new Date(to).getTime() - new Date(from).getTime();
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

export const isActive = (s: string) => s === "Pending" || s === "Running" || s === "WaitingForApproval";
