"use client";

import { useEffect, useRef, useState } from "react";
import { API_BASE, postWorkspaceMessage } from "@/lib/api";
import type { ChatEntry, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { Icons } from "@/components/ui/icons";
import { MentionTextarea, stageMentionables } from "@/components/ui/MentionTextarea";
import { BotIcon } from "../BotIcon";
import { ApprovalCard } from "./SafetyPanel";

const HOOK_PATH = /(\/api\/hooks\/[\w-]+\/[\w-]+\/[a-f0-9]+)/;

function CopyableUrl({ path }: { path: string }) {
  const url = `${API_BASE}${path}`;
  const [copied, setCopied] = useState(false);
  return (
    <span className="mt-1 flex items-center gap-1">
      <code className="truncate rounded bg-zinc-200 px-1.5 py-0.5 text-[11px] dark:bg-zinc-800">{url}</code>
      <button
        type="button"
        onClick={() => navigator.clipboard.writeText(url).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })}
        className="shrink-0 rounded border border-zinc-300 px-1.5 text-[10px] hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
      >
        {copied ? "copied" : "copy"}
      </button>
    </span>
  );
}

function Message({ entry, onSelectRun }: { entry: ChatEntry; onSelectRun: (runId: string) => void }) {
  if (entry.author_kind === "User") {
    return (
      <div className="flex justify-end">
        <div className="max-w-[75%] whitespace-pre-wrap rounded-2xl rounded-br-sm bg-brand-500 px-3 py-2 text-sm text-white">{entry.text}</div>
      </div>
    );
  }

  // A run's own notices (started, queued, finished) link to the run; its result reads as a message.
  const runId = entry.author_id.startsWith("run-") ? entry.author_id : null;
  if (entry.author_kind === "System" && runId && entry.text.includes("\n")) {
    return (
      <div className="flex gap-2">
        <span className="mt-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-zinc-300 text-brand-600 dark:border-zinc-700 dark:text-brand-300">
          <Icons.Play className="h-3.5 w-3.5" />
        </span>
        <div className="min-w-0 max-w-[85%]">
          <div className="text-[11px] text-zinc-500">
            <button onClick={() => onSelectRun(runId)} className="font-medium hover:underline">{entry.author_name}</button>
            {" · "}{new Date(entry.at).toLocaleTimeString()}
          </div>
          <div className={`whitespace-pre-wrap rounded-2xl rounded-tl-sm border px-3 py-2 text-sm ${entry.urgency === "warning"
            ? "border-amber-300 bg-amber-50 dark:border-amber-800 dark:bg-amber-950/60" : "border-zinc-200 bg-white dark:border-zinc-700 dark:bg-zinc-900"}`}>{entry.text}</div>
        </div>
      </div>
    );
  }

  if (entry.author_kind === "System") {
    const hook = entry.text.match(HOOK_PATH);
    return (
      <div className={`mx-auto max-w-[85%] rounded-md px-3 py-1.5 text-center text-[11px] ${
        entry.urgency === "warning" ? "bg-amber-50 text-amber-800 dark:bg-amber-950 dark:text-amber-200" : "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300"}`}>
        {runId ? <button onClick={() => onSelectRun(runId)} className="hover:underline">{entry.text}</button>
          : <div>{hook ? entry.text.split("Point your service at:")[0] + "Point your service at this URL (POST; keep it secret):" : entry.text}</div>}
        {hook && <CopyableUrl path={hook[1]} />}
      </div>
    );
  }

  const tone =
    entry.urgency === "urgent" ? "border-rose-300 bg-rose-50 dark:border-rose-800 dark:bg-rose-950/60"
    : entry.urgency === "warning" ? "border-amber-300 bg-amber-50 dark:border-amber-800 dark:bg-amber-950/60"
    : "border-zinc-200 bg-white dark:border-zinc-700 dark:bg-zinc-900";
  // Alerts from watches, and messages from agents of workspaces made before pipelines.
  return (
    <div className="flex gap-2">
      <span className="mt-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-zinc-300 text-indigo-600 dark:border-zinc-700 dark:text-indigo-300" title={entry.author_id}>
        <BotIcon className="h-4 w-4" />
      </span>
      <div className="max-w-[80%]">
        <div className="text-[11px] text-zinc-500">
          <span className="font-medium">{entry.author_name}</span>
          {" · "}{new Date(entry.at).toLocaleTimeString()}
          {entry.urgency !== "info" && <span className="ml-1 uppercase">{entry.urgency}</span>}
        </div>
        <div className={`whitespace-pre-wrap rounded-2xl rounded-tl-sm border px-3 py-2 text-sm ${tone}`}>{entry.text}</div>
      </div>
    </div>
  );
}

/** The workspace's conversation: run notices and results, watch alerts and approvals. A message
 * starts a run of the pipeline with it as the input. */
export function WorkspaceChat({ workspace, onSent, onSelectRun }: {
  workspace: WorkspaceSnapshot;
  onSent: () => void;
  onSelectRun: (runId: string) => void;
}) {
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  const archived = workspace.status === "Archived";
  const pending = workspace.approvals?.filter((a) => a.status === "Pending") ?? [];

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: "end" });
  }, [workspace.conversation.length]);

  async function send(e: React.FormEvent) {
    e.preventDefault();
    const body = text.trim();
    if (!body) return;
    setSending(true);
    setError(null);
    try {
      // A client id makes a retried send (double click, flaky network) arrive only once.
      await postWorkspaceMessage(workspace.workspace_id, body, crypto.randomUUID());
      setText("");
      onSent();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to send");
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="flex h-full flex-col">
      {pending.length > 0 && (
        <div className="max-h-[40%] space-y-1.5 overflow-y-auto border-b border-amber-200 bg-amber-50/50 p-2 dark:border-amber-900 dark:bg-amber-950/20">
          {pending.map((a) => <ApprovalCard key={a.approval_id} workspaceId={workspace.workspace_id} approval={a} onDecided={onSent} compact />)}
        </div>
      )}
      <div className="flex-1 space-y-3 overflow-y-auto p-4">
        {workspace.conversation.map((c) => <Message key={c.seq} entry={c} onSelectRun={onSelectRun} />)}
        <div ref={bottom} />
      </div>
      <form onSubmit={send} className="border-t border-zinc-200 p-3 dark:border-zinc-800">
        {error && <div className="mb-2 text-xs text-rose-600">{error}</div>}
        <div className="flex gap-2">
          <MentionTextarea
            value={text}
            onValueChange={setText}
            mentionables={stageMentionables(workspace.pipeline?.stages ?? [])}
            above
            wrapperClassName="flex-1"
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(e); } }}
            rows={2}
            disabled={archived}
            placeholder={archived ? "This workspace is archived." : "Send a task to run the pipeline on… (Enter to send, @ to mention a stage)"}
            className="block w-full resize-none rounded border border-zinc-300 bg-white px-2 py-1.5 text-sm dark:border-zinc-700 dark:bg-zinc-900"
          />
          <button type="submit" disabled={sending || archived || !text.trim()} className="rounded bg-brand-500 px-4 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">
            {sending ? "…" : "Send"}
          </button>
        </div>
      </form>
    </div>
  );
}
