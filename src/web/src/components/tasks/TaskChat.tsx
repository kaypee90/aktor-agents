"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiErrorMessage, continueTask, followUpTask, getTaskChat, taskFileSource, uploadTaskAttachments, type FileSource } from "@/lib/api";
import type { AgentListItem, ResourceBudget, RuntimeEvent, TaskChatEntry, TaskChatFile } from "@/lib/types";
import { AttachButton, DropZone, PendingFiles, useAttachments } from "../files/Attachments";
import { FileViewer, fileTypeLabel, formatSize } from "../files/FilePreview";
import { Markdown } from "../files/Markdown";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** Events worth showing as the team's steps, with how to show them. */
const STEP_STYLE: Record<string, { dot: string; label: string }> = {
  AgentSpawned: { dot: "bg-violet-500", label: "Team" },
  AgentToolCalled: { dot: "bg-amber-500", label: "Tool" },
  AgentMessageSent: { dot: "bg-fuchsia-500", label: "Message" },
  ArtifactCreated: { dot: "bg-teal-500", label: "File" },
  AgentCompleted: { dot: "bg-emerald-500", label: "Done" },
  AgentFailed: { dot: "bg-rose-500", label: "Problem" },
  TaskReopened: { dot: "bg-sky-500", label: "Follow-up" },
};

const FILE_KIND: Record<string, string> = {
  DOCX: "Word document", DOC: "Word document", PDF: "PDF", XLSX: "Spreadsheet", XLS: "Spreadsheet", CSV: "CSV data",
  PPTX: "Presentation", PPT: "Presentation", MD: "Markdown", TXT: "Text", JSON: "JSON", PNG: "Image", JPG: "Image", GIF: "Image",
  SVG: "Image", HTML: "Web page",
};

const FILE_TONE: Record<string, string> = {
  DOCX: "bg-blue-500", PDF: "bg-rose-500", XLSX: "bg-emerald-600", CSV: "bg-emerald-600", PPTX: "bg-orange-500", MD: "bg-zinc-600",
};

const SUGGESTIONS = [
  "Turn this into a Word document",
  "Make it a PDF report",
  "Build a slide deck from this",
  "Put the numbers in a spreadsheet",
  "Summarize it in 5 bullet points",
];

/** The steps of one round: what the team did between a message from the user and the answer. */
function stepsBetween(events: RuntimeEvent[], from: string, to: string | null) {
  return events.filter((e) => STEP_STYLE[e.type] && e.timestamp >= from && (to === null || e.timestamp <= to) &&
    !(e.type === "ArtifactCreated" && e.agent_id === "user"));
}

function FileCard({ file, onOpen, active }: { file: TaskChatFile; onOpen: () => void; active: boolean }) {
  const label = fileTypeLabel(file.file_name);
  return (
    <button onClick={onOpen}
      className={cx("flex w-full max-w-xs items-center gap-3 rounded-xl border bg-white p-2.5 text-left transition hover:border-brand-300 hover:shadow-sm dark:bg-zinc-900 dark:hover:border-brand-800",
        active ? "border-brand-400 ring-2 ring-brand-200 dark:border-brand-700 dark:ring-brand-900" : "border-zinc-200 dark:border-zinc-700")}>
      <span className={cx("flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-[10px] font-bold text-white", FILE_TONE[label] ?? "bg-zinc-400 dark:bg-zinc-600")}>
        {label}
      </span>
      <span className="min-w-0">
        <span className="block truncate text-sm font-medium text-zinc-900 dark:text-zinc-100">{file.file_name}</span>
        <span className="block truncate text-xs text-zinc-500">{FILE_KIND[label] ?? "File"} · {formatSize(file.size_bytes)}</span>
      </span>
    </button>
  );
}

function Steps({ steps, roles }: { steps: RuntimeEvent[]; roles: Record<string, string> }) {
  return (
    <ol className="space-y-1.5 border-l border-zinc-200 pl-4 dark:border-zinc-800">
      {steps.map((e) => (
        <li key={e.event_id} className="relative text-xs text-zinc-600 dark:text-zinc-400">
          <span className={cx("absolute -left-[21px] top-1.5 h-2 w-2 rounded-full", STEP_STYLE[e.type].dot)} />
          {e.agent_id && roles[e.agent_id] && <span className="font-medium text-zinc-800 dark:text-zinc-200">{roles[e.agent_id]}: </span>}
          {e.summary}
          <span className="ml-1.5 text-zinc-400">{new Date(e.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}</span>
        </li>
      ))}
    </ol>
  );
}

function UserMessage({ entry, onOpenFile, openId }: { entry: TaskChatEntry; onOpenFile: (f: TaskChatFile) => void; openId: string | null }) {
  return (
    <div className="flex flex-col items-end gap-2">
      {entry.files?.length > 0 && (
        <div className="flex flex-wrap justify-end gap-2">
          {entry.files.map((f) => <FileCard key={f.artifact_id} file={f} active={openId === f.artifact_id} onOpen={() => onOpenFile(f)} />)}
        </div>
      )}
      <div className="max-w-[85%] whitespace-pre-wrap rounded-3xl bg-zinc-100 px-4 py-2.5 text-[15px] text-zinc-900 dark:bg-zinc-800 dark:text-zinc-100">
        {entry.text}
      </div>
    </div>
  );
}

function AgentMessage({ entry, steps, roles, onOpenFile, openId, onShowAgents }: {
  entry: TaskChatEntry; steps: RuntimeEvent[]; roles: Record<string, string>;
  onOpenFile: (f: TaskChatFile) => void; openId: string | null; onShowAgents: () => void;
}) {
  const [showSteps, setShowSteps] = useState(false);
  const [copied, setCopied] = useState(false);
  const status = entry.status ?? "completed";
  const teamSize = new Set(steps.map((s) => s.agent_id).filter(Boolean)).size;

  return (
    <div className="flex gap-3">
      <span className="mt-0.5 shrink-0"><Icons.Logo className="h-7 w-7" /></span>
      <div className="min-w-0 flex-1 space-y-3">
        {status !== "completed" && (
          <span className={cx("inline-block rounded-full px-2 py-0.5 text-[11px] font-medium",
            status === "partial" ? "bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300" : "bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300")}>
            {status === "partial" ? "Partly done" : status === "failed" ? "Didn't finish" : "Stopped"}
          </span>
        )}
        <Markdown text={entry.text} />
        {entry.remaining_work?.length > 0 && (
          <div className="rounded-xl border border-amber-200 bg-amber-50/60 p-3 dark:border-amber-900 dark:bg-amber-950/30">
            <div className="text-xs font-semibold text-amber-800 dark:text-amber-300">Not finished yet</div>
            <ul className="mt-1 list-disc space-y-0.5 pl-5 text-sm text-amber-900 dark:text-amber-200">
              {entry.remaining_work.map((r, i) => <li key={i}>{r}</li>)}
            </ul>
          </div>
        )}
        {entry.files?.length > 0 && (
          <div className="grid gap-2 sm:grid-cols-2">
            {entry.files.map((f) => <FileCard key={f.artifact_id} file={f} active={openId === f.artifact_id} onOpen={() => onOpenFile(f)} />)}
          </div>
        )}
        <div className="flex flex-wrap items-center gap-1 text-xs text-zinc-500">
          <button onClick={async () => { await navigator.clipboard.writeText(entry.text).catch(() => {}); setCopied(true); setTimeout(() => setCopied(false), 1500); }}
            className="inline-flex items-center gap-1 rounded-md px-2 py-1 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
            {copied ? <Icons.Check className="h-3.5 w-3.5" /> : <Icons.Copy className="h-3.5 w-3.5" />} {copied ? "Copied" : "Copy"}
          </button>
          {steps.length > 0 && (
            <button onClick={() => setShowSteps((v) => !v)}
              className="inline-flex items-center gap-1 rounded-md px-2 py-1 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
              <Icons.ChevronDown className={cx("h-3.5 w-3.5 transition-transform", showSteps && "rotate-180")} />
              {showSteps ? "Hide work" : `Show work · ${steps.length} steps${teamSize > 1 ? ` · ${teamSize} agents` : ""}`}
            </button>
          )}
          <button onClick={onShowAgents} className="inline-flex items-center gap-1 rounded-md px-2 py-1 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
            <Icons.Graph className="h-3.5 w-3.5" /> Agents
          </button>
          <span className="ml-auto text-zinc-400">{new Date(entry.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
        </div>
        {showSteps && <Steps steps={steps} roles={roles} />}
      </div>
    </div>
  );
}

type BudgetDraft = { cost: string; tokens: string; minutes: string; toolCalls: string };

const draftOf = (b: ResourceBudget, factor = 1): BudgetDraft => ({
  cost: String(Math.round(b.max_cost_usd * factor * 100) / 100),
  tokens: String(Math.round(b.max_tokens * factor)),
  minutes: String(Math.max(1, Math.round((b.max_duration_seconds * factor) / 60))),
  toolCalls: String(Math.round(b.max_tool_calls * factor)),
});

/**
 * Shown when the last answer stopped before the work was done (out of budget or time, or stopped
 * by you): set a budget for the rest and carry on. The team keeps everything it already did.
 */
function ContinueCard({ base, ceiling, status, onContinue }: {
  base: ResourceBudget; ceiling?: ResourceBudget; status: string; onContinue: (budget: Partial<ResourceBudget>, note: string) => Promise<void>;
}) {
  const [draft, setDraft] = useState<BudgetDraft>(() => draftOf(base));
  const [factor, setFactor] = useState(1);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const num = (v: string) => (v.trim() === "" || Number.isNaN(Number(v)) ? undefined : Number(v));

  const field = (key: keyof BudgetDraft, label: string, max?: number, unit?: string) => (
    <label className="block">
      <span className="text-[11px] text-zinc-500">{label}</span>
      <span className="mt-0.5 flex items-center rounded-lg border border-zinc-200 bg-white focus-within:border-brand-400 dark:border-zinc-700 dark:bg-zinc-950">
        {unit === "$" && <span className="pl-2 text-sm text-zinc-400">$</span>}
        <input inputMode="decimal" value={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: e.target.value })}
          className="w-full min-w-0 bg-transparent px-2 py-1.5 text-sm outline-none" />
        {unit && unit !== "$" && <span className="pr-2 text-xs text-zinc-400">{unit}</span>}
      </span>
      {max !== undefined && <span className="text-[10px] text-zinc-400">max {max.toLocaleString()}</span>}
    </label>
  );

  async function go() {
    setBusy(true);
    setError(null);
    try {
      const minutes = num(draft.minutes);
      await onContinue({
        max_cost_usd: num(draft.cost),
        max_tokens: num(draft.tokens),
        max_duration_seconds: minutes === undefined ? undefined : minutes * 60,
        max_tool_calls: num(draft.toolCalls),
      }, note);
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div className="ml-10 rounded-2xl border border-zinc-200 bg-white p-4 shadow-sm dark:border-zinc-800 dark:bg-zinc-900">
      <div className="flex items-start gap-3">
        <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-brand-50 text-brand-600 dark:bg-brand-950 dark:text-brand-300">
          <Icons.Play className="h-4 w-4" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">
            {status === "terminated" ? "Pick up where you stopped it" : "Continue with more budget"}
          </div>
          <p className="mt-0.5 text-xs text-zinc-500">
            The team carries on from where it stopped, with everything it already did and found. This budget is for the rest of the
            work, on top of what has been spent.
          </p>
        </div>
      </div>

      <div className="mt-3 flex flex-wrap gap-1.5">
        {[1, 2, 5].map((f) => (
          <button key={f} type="button" onClick={() => { setFactor(f); setDraft(draftOf(base, f)); }}
            className={cx("rounded-full border px-3 py-1 text-xs", factor === f
              ? "border-brand-400 bg-brand-50 text-brand-700 dark:border-brand-700 dark:bg-brand-950 dark:text-brand-300"
              : "border-zinc-200 text-zinc-600 hover:border-zinc-300 dark:border-zinc-700 dark:text-zinc-400")}>
            {f === 1 ? "Same again" : `${f}× the budget`}
          </button>
        ))}
      </div>

      <div className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-4">
        {field("cost", "Cost", ceiling?.max_cost_usd, "$")}
        {field("tokens", "Tokens", ceiling?.max_tokens)}
        {field("minutes", "Time", ceiling ? Math.floor(ceiling.max_duration_seconds / 60) : undefined, "min")}
        {field("toolCalls", "Tool calls", ceiling?.max_tool_calls)}
      </div>

      <textarea value={note} onChange={(e) => setNote(e.target.value)} rows={1} placeholder="Anything to change? (optional)"
        className="mt-3 block w-full resize-none rounded-lg border border-zinc-200 bg-white px-3 py-2 text-sm outline-none focus:border-brand-400 dark:border-zinc-700 dark:bg-zinc-950" />

      {error && <div className="mt-2 text-xs text-rose-600">{error}</div>}
      <div className="mt-3 flex items-center justify-end gap-2">
        <span className="mr-auto text-[11px] text-zinc-400">Limits above the server maximum are capped.</span>
        <button type="button" onClick={go} disabled={busy}
          className="inline-flex items-center gap-1.5 rounded-full bg-zinc-900 px-4 py-2 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-white">
          {busy ? <span className="h-3 w-3 animate-spin rounded-full border-2 border-current border-t-transparent" /> : <Icons.Play className="h-3.5 w-3.5" />}
          Continue
        </button>
      </div>
    </div>
  );
}

/** The answer being worked on: who is busy and the latest steps, with a way to stop. */
function Working({ steps, roles, active, onStop }: { steps: RuntimeEvent[]; roles: Record<string, string>; active: number; onStop: () => void }) {
  const [all, setAll] = useState(false);
  const shown = all ? steps : steps.slice(-4);
  return (
    <div className="flex gap-3">
      <span className="mt-0.5 shrink-0"><Icons.Logo className="h-7 w-7 animate-pulse" /></span>
      <div className="min-w-0 flex-1 rounded-2xl border border-zinc-200 bg-white p-4 dark:border-zinc-800 dark:bg-zinc-900">
        <div className="flex items-center gap-2 text-sm font-medium text-zinc-800 dark:text-zinc-200">
          <span className="flex gap-1">
            {[0, 150, 300].map((d) => <span key={d} className="h-1.5 w-1.5 animate-bounce rounded-full bg-brand-500" style={{ animationDelay: `${d}ms` }} />)}
          </span>
          Working on it{active > 1 ? ` · ${active} agents busy` : ""}
          <button onClick={onStop} className="ml-auto inline-flex items-center gap-1 rounded-md border border-zinc-200 px-2 py-0.5 text-xs font-normal text-zinc-600 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-400 dark:hover:bg-zinc-800">
            <Icons.Stop className="h-3 w-3" /> Stop
          </button>
        </div>
        {shown.length > 0 && <div className="mt-3"><Steps steps={shown} roles={roles} /></div>}
        {steps.length > 4 && (
          <button onClick={() => setAll((v) => !v)} className="mt-2 text-xs text-brand-600 hover:underline dark:text-brand-400">
            {all ? "Show fewer steps" : `Show all ${steps.length} steps`}
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * A task as a conversation (the default view): the goal and each follow-up, the team's answer for
 * each with the files it made, a live view of the work in progress, and a composer that takes
 * instructions and files. Files open beside the conversation, the way they would look opened.
 * A follow-up on a finished task reopens it with everything the team already knows.
 */
export function TaskChat({ taskId, running, events, agents, budget, ceiling, onStop, onShowAgents, workspaceId }: {
  taskId: string;
  running: boolean;
  /** Set for a run of a workspace's pipeline: it takes no follow-ups, so the composer links back there. */
  workspaceId?: string | null;
  events: RuntimeEvent[];
  agents: AgentListItem[];
  /** The task's budget: what a continue offers by default. */
  budget?: ResourceBudget | null;
  ceiling?: ResourceBudget;
  onStop: () => void;
  onShowAgents: (agentId?: string) => void;
}) {
  const [chat, setChat] = useState<TaskChatEntry[] | null>(null);
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<{ id: string; source: FileSource } | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  const box = useRef<HTMLTextAreaElement>(null);

  const upload = useCallback(async (file: File) => (await uploadTaskAttachments(taskId, [file]))[0].artifact_id, [taskId]);
  const files = useAttachments(upload);

  // Follow-ups, the root's reports and new files: each reloads the conversation.
  const rootId = agents.find((a) => a.parent_agent_id === null)?.agent_id;
  const version = events.filter((e) => e.type === "TaskFollowUp" || e.type === "ArtifactCreated" ||
    (e.agent_id === rootId && ["AgentCompleted", "AgentFailed", "AgentTerminated", "TaskReopened"].includes(e.type))).length;

  useEffect(() => {
    let cancelled = false;
    // Events are saved by a background writer that can trail the live stream: load again shortly.
    const load = () => getTaskChat(taskId).then((c) => !cancelled && setChat(c)).catch(() => { /* keep what we have */ });
    load();
    const retry = setTimeout(load, 1500);
    return () => {
      cancelled = true;
      clearTimeout(retry);
    };
  }, [taskId, version, running]);

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: "end", behavior: "smooth" });
  }, [chat?.length, running]);

  const roles = useMemo(() => Object.fromEntries(agents.map((a) => [a.agent_id, a.role])), [agents]);
  const active = agents.filter((a) => !["Completed", "Failed", "Terminated", "TimedOut"].includes(a.status)).length;

  async function send(body: string) {
    const trimmed = body.trim();
    if ((!trimmed && files.ids.length === 0) || sending || files.uploading) return;
    setSending(true);
    setError(null);
    try {
      const entry = await followUpTask(taskId, trimmed, files.ids);
      setChat((c) => (c && !c.some((x) => x.id === entry.id) ? [...c, entry] : c));
      setText("");
      files.clear();
      if (box.current) box.current.style.height = "";
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setSending(false);
    }
  }

  const openFile = (f: TaskChatFile) =>
    setOpen((o) => (o?.id === f.artifact_id ? null : { id: f.artifact_id, source: taskFileSource(taskId, f.artifact_id) }));

  // Each answer's steps run from the message before it to the answer itself.
  const entries = chat ?? [];
  const lastUserAt = [...entries].reverse().find((e) => e.author === "user")?.at ?? null;
  const waiting = running && entries.length > 0 && entries[entries.length - 1].author === "user";
  const canSend = (text.trim().length > 0 || files.ids.length > 0) && !sending && !files.uploading;
  // The last answer stopped before the work was done: offer to carry on with more budget.
  const last = entries[entries.length - 1];
  const unfinished = !running && last?.author === "agent" && (last.status !== "completed" || last.remaining_work?.length > 0);

  async function carryOn(next: Partial<ResourceBudget>, note: string) {
    const entry = await continueTask(taskId, next, note);
    setChat((c) => (c && !c.some((x) => x.id === entry.id) ? [...c, entry] : c));
  }

  return (
    <div className="flex h-full min-h-0">
      <DropZone onFiles={files.add} className="flex min-w-0 flex-1 flex-col">
        <div className="min-h-0 flex-1 overflow-y-auto">
          <div className="mx-auto max-w-3xl space-y-8 px-4 py-8">
            {chat === null ? (
              <div className="space-y-3">{[70, 90, 50].map((w, i) => <div key={i} className="h-4 animate-pulse rounded bg-zinc-100 dark:bg-zinc-900" style={{ width: `${w}%` }} />)}</div>
            ) : (
              entries.map((entry, i) => entry.author === "user"
                ? <UserMessage key={entry.id} entry={entry} onOpenFile={openFile} openId={open?.id ?? null} />
                : <AgentMessage key={entry.id} entry={entry} roles={roles} openId={open?.id ?? null} onOpenFile={openFile}
                    onShowAgents={() => onShowAgents()}
                    steps={stepsBetween(events, entries[i - 1]?.at ?? entry.at, entry.at)} />)
            )}
            {waiting && lastUserAt && <Working steps={stepsBetween(events, lastUserAt, null)} roles={roles} active={active} onStop={onStop} />}
            {unfinished && budget && <ContinueCard base={budget} ceiling={ceiling} status={last.status ?? "partial"} onContinue={carryOn} />}
            {!running && !unfinished && chat !== null && entries.length > 1 && (
              <div className="flex flex-wrap gap-2 pl-10">
                {SUGGESTIONS.map((s) => (
                  <button key={s} onClick={() => { setText(s); box.current?.focus(); }}
                    className="rounded-full border border-zinc-200 px-3 py-1 text-xs text-zinc-600 hover:border-brand-300 hover:text-brand-700 dark:border-zinc-700 dark:text-zinc-400 dark:hover:border-brand-800 dark:hover:text-brand-300">
                    {s}
                  </button>
                ))}
              </div>
            )}
            <div ref={bottom} />
          </div>
        </div>

        {workspaceId ? (
          <div className="px-4 pb-4">
            <div className="mx-auto flex max-w-3xl items-center gap-3 rounded-2xl border border-zinc-200 bg-zinc-50 px-4 py-3 text-sm text-zinc-600 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-400">
              <Icons.Workspaces className="h-4 w-4 shrink-0" />
              <span className="flex-1">This is a run of a workspace&apos;s pipeline. To go again with a new input, run the pipeline from its workspace.</span>
              <Link href={`/workspaces?id=${workspaceId}`} className="shrink-0 font-medium text-brand-600 hover:underline dark:text-brand-400">Open the workspace</Link>
            </div>
          </div>
        ) : (
        <div className="px-4 pb-4">
          <form onSubmit={(e) => { e.preventDefault(); send(text); }}
            className="mx-auto max-w-3xl rounded-3xl border border-zinc-200 bg-white p-2 shadow-sm focus-within:border-zinc-300 focus-within:shadow-md dark:border-zinc-700 dark:bg-zinc-900 dark:focus-within:border-zinc-600">
            {(error || files.pending.length > 0) && (
              <div className="space-y-2 px-2 pt-1">
                {error && <div className="text-xs text-rose-600">{error}</div>}
                <PendingFiles pending={files.pending} onRemove={files.remove}
                  onOpen={(p) => p.id && setOpen({ id: p.id, source: taskFileSource(taskId, p.id) })} />
              </div>
            )}
            <textarea
              ref={box}
              value={text}
              rows={1}
              onChange={(e) => {
                setText(e.target.value);
                e.target.style.height = "auto";
                e.target.style.height = `${Math.min(e.target.scrollHeight, 220)}px`;
              }}
              onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(text); } }}
              onPaste={(e) => { const pasted = Array.from(e.clipboardData.files); if (pasted.length > 0) { e.preventDefault(); files.add(pasted); } }}
              placeholder={running ? "Add an instruction for the team…" : "Ask a follow-up, or ask for a document, deck or spreadsheet…"}
              className="block max-h-56 w-full resize-none bg-transparent px-3 py-2 text-[15px] text-zinc-900 outline-none placeholder:text-zinc-400 dark:text-zinc-100"
            />
            <div className="flex items-center gap-1">
              <AttachButton onFiles={files.add} />
              <span className="ml-1 hidden text-[11px] text-zinc-400 sm:inline">Enter to send · Shift+Enter for a new line · drop or paste files</span>
              <button type="submit" disabled={!canSend} aria-label="Send"
                className="ml-auto flex h-9 w-9 items-center justify-center rounded-full bg-zinc-900 text-white transition hover:bg-zinc-700 disabled:bg-zinc-200 disabled:text-zinc-400 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-white dark:disabled:bg-zinc-800 dark:disabled:text-zinc-600">
                {sending || files.uploading ? <span className="h-3 w-3 animate-spin rounded-full border-2 border-current border-t-transparent" /> : <Icons.ArrowUp className="h-4 w-4" />}
              </button>
            </div>
          </form>
        </div>
        )}
      </DropZone>

      {open && (
        <aside className="fixed inset-0 z-40 flex flex-col border-l border-zinc-200 bg-white lg:static lg:z-auto lg:w-[48%] lg:max-w-3xl dark:border-zinc-800 dark:bg-zinc-900">
          <FileViewer key={open.id} source={open.source} onClose={() => setOpen(null)} />
        </aside>
      )}
    </div>
  );
}
