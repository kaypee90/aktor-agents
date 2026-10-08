"use client";

import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { ChatEntry, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { WorkspaceChat } from "./WorkspaceChat";

const OPEN_KEY = "aktor.workspace-chat.open";
const seenKey = (workspaceId: string) => `aktor.workspace-chat.seen.${workspaceId}`;
/** How long the preview of a new message stays next to the button. */
const PREVIEW_MS = 6000;
/** In development, Next.js's badge sits in the bottom-right corner (next.config.ts), so the widget
 * sits above it rather than under it. */
const CORNER = process.env.NODE_ENV === "development" ? "bottom-20 right-4" : "bottom-4 right-4";

/** Per-viewer settings, remembered in the browser. With storage blocked they last until the page
 * reloads. Other tabs' changes arrive through the storage event. */
const memory = new Map<string, string>();
const listeners = new Set<() => void>();
function read(key: string): string | null {
  try { return localStorage.getItem(key) ?? memory.get(key) ?? null; } catch { return memory.get(key) ?? null; }
}
function save(key: string, value: string) {
  memory.set(key, value);
  try { localStorage.setItem(key, value); } catch { /* kept in memory only */ }
  listeners.forEach((l) => l());
}
function subscribe(listener: () => void) {
  listeners.add(listener);
  window.addEventListener("storage", listener);
  return () => { listeners.delete(listener); window.removeEventListener("storage", listener); };
}

/**
 * The workspace's chat as a floating widget: a button in the corner that opens the conversation and
 * hides it again. While it's hidden, the button counts the messages that arrived since it was last
 * open, and a new one shows briefly as a preview. Open or hidden is remembered, as is the last
 * message seen in each workspace.
 */
export function WorkspaceChatWidget({ workspace, onSent, onSelectRun }: {
  workspace: WorkspaceSnapshot;
  onSent: () => void;
  onSelectRun: (runId: string) => void;
}) {
  const id = workspace.workspace_id;
  const conversation = workspace.conversation;
  const latest = conversation.at(-1)?.seq ?? 0;
  // The server render has no storage: hidden, nothing unread.
  const open = useSyncExternalStore(subscribe, () => read(OPEN_KEY) === "1", () => false);
  const stored = useSyncExternalStore(subscribe, () => read(seenKey(id)), () => null);
  // A workspace never opened here starts with nothing unread.
  const seen = stored !== null && Number.isFinite(Number(stored)) ? Number(stored) : latest;
  const [preview, setPreview] = useState<{ workspaceId: string; entry: ChatEntry } | null>(null);
  const shownLatest = useRef<{ id: string; seq: number } | null>(null);

  // Remember where reading starts, and while the chat is open, that everything is read.
  useEffect(() => {
    if (stored === null || (open && latest > seen)) save(seenKey(id), String(open ? latest : seen));
  }, [open, latest, seen, stored, id]);

  // Hidden: a newly arrived message from someone else shows as a preview for a moment.
  useEffect(() => {
    const before = shownLatest.current;
    shownLatest.current = { id, seq: latest };
    if (open || !before || before.id !== id || latest <= before.seq) return;
    const newest = conversation.filter((c) => c.seq > before.seq && c.author_kind !== "User").at(-1);
    if (!newest) return;
    // Not cancelled by the next refresh: the workspace re-renders on every live update.
    setTimeout(() => setPreview({ workspaceId: id, entry: newest }), 0);
  }, [latest, id, open, conversation]);

  // A preview fades after a few seconds; a newer one starts the clock again.
  useEffect(() => {
    if (!preview) return;
    const hide = setTimeout(() => setPreview(null), PREVIEW_MS);
    return () => clearTimeout(hide);
  }, [preview]);

  function toggle(next = !open) {
    setPreview(null);
    save(OPEN_KEY, next ? "1" : "0");
  }

  const shown = !open && preview?.workspaceId === id ? preview.entry : null;
  const unread = open ? 0 : conversation.filter((c) => c.seq > seen && c.author_kind !== "User").length;
  const pending = workspace.approvals?.filter((a) => a.status === "Pending").length ?? 0;
  const label = open ? "Hide chat"
    : `Show chat${unread ? `, ${unread} new message${unread === 1 ? "" : "s"}` : ""}${pending ? `, ${pending} waiting for approval` : ""}`;

  return (
    <div className={cx("pointer-events-none absolute z-30 flex flex-col items-end gap-3", CORNER)}>
      {open && (
        <section aria-label={`Chat with ${workspace.name}`}
          className="pointer-events-auto flex h-[min(600px,calc(100vh-14rem))] w-[min(420px,calc(100vw-2rem))] flex-col overflow-hidden rounded-2xl border border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-950">
          <header className="flex shrink-0 items-center gap-2.5 border-b border-zinc-200 px-4 py-2.5 dark:border-zinc-800">
            <Icons.Chat className="h-4 w-4 text-brand-500" />
            <div className="min-w-0 flex-1">
              <div className="text-sm font-medium text-zinc-900 dark:text-zinc-100">Chat</div>
              <div className="truncate text-[11px] text-zinc-500">{workspace.name} · a message runs the pipeline</div>
            </div>
            <button onClick={() => toggle(false)} aria-label="Hide chat" title="Hide chat"
              className="rounded-md p-1.5 text-zinc-500 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
              <Icons.ChevronDown className="h-4 w-4" />
            </button>
          </header>
          <div className="min-h-0 flex-1">
            <WorkspaceChat workspace={workspace} onSent={onSent} onSelectRun={onSelectRun} />
          </div>
        </section>
      )}

      {shown && (
        <button onClick={() => toggle(true)}
          className="pointer-events-auto w-72 rounded-2xl rounded-br-sm border border-zinc-200 bg-white px-3.5 py-2.5 text-left shadow-lg transition hover:border-brand-300 dark:border-zinc-700 dark:bg-zinc-900">
          <div className="flex items-center gap-1.5 text-[11px] text-zinc-500">
            <span className="h-1.5 w-1.5 rounded-full bg-brand-500" />
            <span className="truncate font-medium text-zinc-700 dark:text-zinc-300">{shown.author_name}</span>
            <span className="ml-auto shrink-0">{new Date(shown.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
          </div>
          <p className="mt-0.5 line-clamp-2 text-sm text-zinc-800 dark:text-zinc-200">{shown.text}</p>
        </button>
      )}

      <button onClick={() => toggle()} aria-label={label} title={label} aria-expanded={open}
        className="pointer-events-auto relative flex h-12 w-12 items-center justify-center rounded-full bg-brand-500 text-white shadow-lg transition hover:bg-brand-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-500 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-zinc-950">
        {shown && <span className="absolute inset-0 animate-ping rounded-full bg-brand-500/50" />}
        {open ? <Icons.X className="relative h-5 w-5" /> : <Icons.Chat className="relative h-5 w-5" />}
        {!open && (unread > 0 || pending > 0) && (
          <span className={cx("absolute -right-1 -top-1 flex h-5 min-w-5 items-center justify-center rounded-full px-1 text-[11px] font-semibold tabular-nums text-white ring-2 ring-white dark:ring-zinc-950",
            unread > 0 ? "bg-rose-500" : "bg-amber-500")}>
            {unread > 0 ? (unread > 99 ? "99+" : unread) : pending}
          </span>
        )}
      </button>

      {/* Screen readers hear new messages while the chat is hidden. */}
      <span className="sr-only" aria-live="polite">{shown ? `New message from ${shown.author_name}: ${shown.text}` : ""}</span>
    </div>
  );
}
