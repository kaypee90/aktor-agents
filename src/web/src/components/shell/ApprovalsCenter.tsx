"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { listPendingApprovals, type PendingApproval } from "@/lib/api";
import { ApprovalCard, APPROVALS_CHANGED } from "../workspace/SafetyPanel";

const POLL_MS = 8000;
const TOAST_MS = 20_000;

export type ApprovalsState = ReturnType<typeof useApprovals>;

/**
 * Approval requests waiting for you, wherever you are in the dashboard. Used once, by the app
 * shell, which shows the bell in its sidebar (and the count in the browser tab's title), and a
 * toast when a new request arrives; with your permission, a desktop notification too while the
 * tab is in the background. An agent waits until someone decides, so these should be hard to miss.
 */
export function useApprovals() {
  const [pending, setPending] = useState<PendingApproval[]>([]);
  const [toasts, setToasts] = useState<PendingApproval[]>([]);
  const [permission, setPermission] = useState<NotificationPermission | "unsupported">(
    () => (typeof Notification === "undefined" ? "unsupported" : Notification.permission));
  const seen = useRef<Set<string> | null>(null);

  const refresh = useCallback(async () => {
    let next: PendingApproval[];
    try {
      next = await listPendingApprovals();
    } catch {
      return; // Signed out or the API is restarting: try again next tick.
    }
    setPending(next);
    const ids = new Set(next.map((p) => p.approval.approval_id));
    // The first load only learns what's already waiting; later ones announce what's new.
    if (seen.current !== null) {
      const fresh = next.filter((p) => !seen.current!.has(p.approval.approval_id));
      if (fresh.length > 0) {
        setToasts((t) => [...t.filter((x) => ids.has(x.approval.approval_id)), ...fresh].slice(-3));
        if (typeof Notification !== "undefined" && Notification.permission === "granted" && document.hidden) {
          for (const p of fresh) {
            new Notification(`Approval ${p.approval.code} needed · ${p.workspace_name}`, {
              body: `${p.approval.agent_name} wants to run ${p.approval.tool_name}.`,
              tag: p.approval.approval_id,
            });
          }
        }
      }
    }
    seen.current = ids;
    setToasts((t) => t.filter((x) => ids.has(x.approval.approval_id)));
  }, []);

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let cancelled = false;
    const tick = async () => {
      await refresh();
      if (!cancelled) timer = setTimeout(tick, POLL_MS);
    };
    tick();
    const now = () => void refresh();
    window.addEventListener("focus", now);
    window.addEventListener(APPROVALS_CHANGED, now);
    return () => {
      cancelled = true;
      clearTimeout(timer);
      window.removeEventListener("focus", now);
      window.removeEventListener(APPROVALS_CHANGED, now);
    };
  }, [refresh]);

  // The count in the tab's title, so a background tab shows it too.
  useEffect(() => {
    const base = document.title.replace(/^\(\d+\) /, "");
    document.title = pending.length > 0 ? `(${pending.length}) ${base}` : base;
  }, [pending.length]);

  // Toasts close themselves after a while (the bell keeps the count).
  useEffect(() => {
    if (toasts.length === 0) return;
    const timer = setTimeout(() => setToasts((t) => t.slice(1)), TOAST_MS);
    return () => clearTimeout(timer);
  }, [toasts]);

  const dismiss = (p: PendingApproval) => setToasts((t) => t.filter((x) => x !== p));
  const requestPermission = () => Notification.requestPermission().then(setPermission);
  return { pending, toasts, permission, refresh, dismiss, requestPermission };
}

/** The sidebar's approvals button: the count, and a panel to decide each request. */
export function ApprovalsBell({ state, collapsed }: { state: ApprovalsState; collapsed: boolean }) {
  const { pending, permission, refresh, requestPermission } = state;
  const [open, setOpen] = useState(false);
  const count = pending.length;
  return (
      <div className="relative">
        <button
          onClick={() => setOpen((o) => !o)}
          title={collapsed ? `Approvals (${count})` : undefined}
          aria-label={`Approvals waiting: ${count}`}
          aria-expanded={open}
          className={cx(
            "flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm transition-colors",
            count > 0
              ? "bg-amber-50 font-medium text-amber-900 ring-1 ring-amber-300 hover:bg-amber-100 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-700"
              : "text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-900/70",
            collapsed && "justify-center",
          )}
        >
          <span className="relative">
            <BellIcon className={cx("h-4 w-4 shrink-0", count > 0 ? "text-amber-600 dark:text-amber-400" : "text-zinc-400")} />
            {count > 0 && (
              <span className="absolute -right-2 -top-2 flex h-4 min-w-4 items-center justify-center rounded-full bg-rose-600 px-1 text-[10px] font-bold text-white">
                {count}
              </span>
            )}
          </span>
          {!collapsed && (count > 0 ? `${count} approval${count === 1 ? "" : "s"} waiting` : "Approvals")}
          {count > 0 && <span className="ml-auto h-2 w-2 animate-pulse rounded-full bg-amber-500" />}
        </button>

        {open && (
          <div className="absolute bottom-full left-0 z-50 mb-2 w-[min(420px,calc(100vw-2rem))] overflow-hidden rounded-xl border border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-900">
            <div className="flex items-center justify-between border-b border-zinc-200 px-4 py-2.5 dark:border-zinc-800">
              <span className="text-sm font-semibold">Approvals waiting</span>
              <button onClick={() => setOpen(false)} className="rounded p-1 text-zinc-400 hover:bg-zinc-100 dark:hover:bg-zinc-800" aria-label="Close">
                <Icons.X className="h-4 w-4" />
              </button>
            </div>
            <div className="max-h-[60vh] space-y-2 overflow-y-auto p-3">
              {count === 0 && <p className="py-4 text-center text-xs text-zinc-500">Nothing is waiting for you.</p>}
              {pending.map((p) => (
                <div key={p.approval.approval_id} className="space-y-1">
                  <Link href={`/workspaces?id=${p.workspace_id}`} onClick={() => setOpen(false)}
                    className="text-[11px] font-medium text-zinc-500 hover:text-brand-600 hover:underline">
                    {p.workspace_name} →
                  </Link>
                  <ApprovalCard workspaceId={p.workspace_id} approval={p.approval} onDecided={refresh} />
                </div>
              ))}
            </div>
            {permission === "default" && (
              <button
                onClick={requestPermission}
                className="block w-full border-t border-zinc-200 px-4 py-2.5 text-left text-xs text-brand-600 hover:bg-zinc-50 dark:border-zinc-800 dark:text-brand-400 dark:hover:bg-zinc-800"
              >
                Notify me on this computer when an approval is needed, even in another tab
              </button>
            )}
          </div>
        )}
      </div>
  );
}

/** New requests pop up wherever you are, with Approve and Reject right there. */
export function ApprovalToasts({ state }: { state: ApprovalsState }) {
  const { toasts, refresh, dismiss } = state;
  return (
      <div className="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-[min(400px,calc(100vw-2rem))] flex-col gap-2" aria-live="assertive">
        {toasts.map((p) => (
          <div key={p.approval.approval_id} className="pointer-events-auto overflow-hidden rounded-xl border border-amber-300 bg-white shadow-2xl dark:border-amber-700 dark:bg-zinc-900">
            <div className="flex items-center gap-2 bg-amber-100 px-3 py-2 text-xs font-semibold text-amber-900 dark:bg-amber-500/15 dark:text-amber-200">
              <BellIcon className="h-4 w-4" />
              Approval {p.approval.code} needed · {p.workspace_name}
              <button onClick={() => dismiss(p)} className="ml-auto rounded p-0.5 hover:bg-amber-200 dark:hover:bg-amber-500/20" aria-label="Dismiss">
                <Icons.X className="h-3.5 w-3.5" />
              </button>
            </div>
            <div className="p-2">
              <ApprovalCard workspaceId={p.workspace_id} approval={p.approval} onDecided={refresh} compact />
              <Link href={p.study_id ? `/studies?id=${encodeURIComponent(p.study_id)}` : `/workspaces?id=${p.workspace_id}`}
                className="mt-1.5 inline-block text-[11px] text-zinc-500 hover:text-brand-600 hover:underline">
                {p.study_id ? "Open the study" : "Open the workspace"}
              </Link>
            </div>
          </div>
        ))}
      </div>
  );
}

function BellIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden="true">
      <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
      <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
    </svg>
  );
}
