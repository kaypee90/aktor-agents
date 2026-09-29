"use client";

import { useState } from "react";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { WorkspaceChat } from "./WorkspaceChat";

const OPEN_KEY = "aktor:workspaceChatOpen";

/** Widget width in px; the team view keeps its agents clear of it. */
export const CHAT_WIDGET_WIDTH = 360;

export function readChatOpen(): boolean {
  try {
    return localStorage.getItem(OPEN_KEY) !== "false";
  } catch {
    return true;
  }
}

export function rememberChatOpen(open: boolean) {
  try {
    localStorage.setItem(OPEN_KEY, String(open));
  } catch {
    // Storage unavailable: the widget just opens by default next time.
  }
}

/** The workspace chat as a floating widget over the team view: open to talk to the coordinator,
 * minimise to watch the agents work. Counts replies that arrive while it's minimised. */
export function WorkspaceChatWidget({ workspace, open, onOpenChange, onSent, onSelectAgent }: {
  workspace: WorkspaceSnapshot;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSent: () => void;
  onSelectAgent: (id: string) => void;
}) {
  const lastSeq = workspace.conversation.at(-1)?.seq ?? 0;
  // Everything up to here was on screen when the chat was minimised.
  const [seenSeq, setSeenSeq] = useState(lastSeq);

  const unread = workspace.conversation.filter((c) => c.seq > seenSeq && c.author_kind === "Agent").length;
  const approvals = workspace.approvals?.filter((a) => a.status === "Pending").length ?? 0;

  const toggle = (next: boolean) => {
    if (!next) setSeenSeq(lastSeq);
    onOpenChange(next);
  };

  if (!open) {
    return (
      <button
        onClick={() => toggle(true)}
        className="absolute bottom-4 right-4 z-10 flex items-center gap-2 rounded-full bg-blue-600 px-4 py-2.5 text-sm font-medium text-white shadow-lg hover:bg-blue-700"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden="true">
          <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
        </svg>
        Chat
        {unread > 0 && <span className="rounded-full bg-white px-1.5 text-[11px] font-semibold text-blue-700">{unread}</span>}
        {approvals > 0 && <span className="rounded-full bg-amber-400 px-1.5 text-[11px] font-semibold text-amber-950" title="Waiting for your approval">{approvals}</span>}
      </button>
    );
  }

  return (
    <div style={{ width: `min(${CHAT_WIDGET_WIDTH}px, calc(100% - 2rem))` }}
      className="absolute bottom-4 right-4 z-10 flex h-[min(560px,calc(100%-2rem))] flex-col overflow-hidden rounded-xl border border-neutral-200 bg-white shadow-2xl dark:border-neutral-700 dark:bg-neutral-950">
      <div className="flex items-center justify-between border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold">Chat</div>
          <div className="truncate text-[11px] text-neutral-500">Talk to the coordinator of {workspace.name}</div>
        </div>
        <button onClick={() => toggle(false)} className="rounded px-2 py-1 text-neutral-500 hover:bg-neutral-100 dark:hover:bg-neutral-800" title="Minimise">
          ▾
        </button>
      </div>
      <div className="min-h-0 flex-1">
        <WorkspaceChat workspace={workspace} onSent={onSent} onSelectAgent={onSelectAgent} />
      </div>
    </div>
  );
}
