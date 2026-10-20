"use client";

import { useEffect, useRef, useState } from "react";
import { apiErrorMessage, designSolution, type DesignTurn, type SolutionDesignProposal } from "@/lib/api";
import type { BuilderSkill, Json } from "@/lib/solutionPackage";
import { cx } from "@/components/ui";
import { MentionTextarea, mentionClass, type Mentionable } from "@/components/ui/MentionTextarea";
import { Icons } from "@/components/ui/icons";

type Obj = { [key: string]: Json };
type Draft = { manifest: Obj; skills: BuilderSkill[] };

/** A message in the conversation. An assistant message may carry a proposal to apply. */
type Message =
  | { role: "user"; content: string }
  | {
      role: "assistant";
      content: string;
      proposal?: SolutionDesignProposal;
      /** The draft it was proposed against, to warn if the forms changed since. */
      base?: string;
      state?: "applied" | "discarded";
      /** What the draft was before applying, for undo. */
      before?: Draft;
      error?: boolean;
    };

// The same handles the server finds (Mentions.Find).
const HANDLE = /(?<![\w.@])@([A-Za-z0-9][A-Za-z0-9._:/-]*[A-Za-z0-9]|[A-Za-z0-9])/g;

/** A sent message, with the things it @mentions shown as what they are. */
function WithMentions({ text, mentionables }: { text: string; mentionables: Mentionable[] }) {
  const parts: React.ReactNode[] = [];
  let last = 0;
  for (const m of text.matchAll(HANDLE)) {
    const known = mentionables.find((x) => x.handle.toLowerCase() === m[1].toLowerCase());
    if (!known || m.index === undefined) continue;
    parts.push(text.slice(last, m.index));
    parts.push(<span key={m.index} title={known.detail ? `${known.label} · ${known.detail}` : known.label}
      className={cx("rounded px-1 font-medium", mentionClass(known.kind))}>{m[0]}</span>);
    last = m.index + m[0].length;
  }
  parts.push(text.slice(last));
  return <>{parts}</>;
}

const SUGGESTIONS = [
  "Design a helpdesk: customers report issues, an agent triages them by urgency, and an assistant answers status questions.",
  "Add a Vendor entity with name, phone and hourly rate, and let the workspace assign vendors to requests.",
  "Add an eval for each workspace.",
  "Send a Slack message when an urgent request comes in.",
];

const fingerprint = (d: Draft) => JSON.stringify(d);

// In development, Next's badge sits in the bottom-right corner (next.config.ts): stay left of it.
const CORNER = process.env.NODE_ENV === "development" ? "right-20" : "right-5";

/**
 * The builder's conversation (docs/solutions.md#designing-in-a-conversation): describe the solution,
 * or a change to it, in plain language. Each answer comes with the changes it would make, already
 * checked against the server; nothing changes until you apply them, and an applied change can be
 * undone. The forms stay the source of truth: the designer always works on the draft as it is now.
 */
export function DesignChat({ draft, storageKey, mentionables, onApply }: {
  draft: Draft;
  /** What a message can @mention: the solution's stages, workspaces, skills, knowledge and connectors, and the organization's. */
  mentionables: Mentionable[];
  /** Where the conversation is kept in this browser (per draft). */
  storageKey: string;
  onApply: (next: Draft) => void;
}) {
  const [open, setOpen] = useState(false);
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);
  const textarea = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    try {
      const raw = localStorage.getItem(storageKey);
      setMessages(raw ? JSON.parse(raw) : []);
      setOpen(localStorage.getItem(`${storageKey}:open`) === "1");
    } catch { /* storage blocked: start fresh */ }
  }, [storageKey]);

  useEffect(() => {
    try { localStorage.setItem(storageKey, JSON.stringify(messages.slice(-40))); } catch { /* best effort */ }
    scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: "smooth" });
  }, [messages, storageKey]);

  useEffect(() => {
    try { localStorage.setItem(`${storageKey}:open`, open ? "1" : "0"); } catch { /* best effort */ }
    if (open) setTimeout(() => textarea.current?.focus(), 50);
  }, [open, storageKey]);

  async function send(text: string) {
    const request = text.trim();
    if (!request || busy) return;
    // What the designer remembers: the conversation's words, not old drafts (it always gets the current one).
    const history: DesignTurn[] = messages.map((m) => ({ role: m.role, content: m.content }));
    setMessages((m) => [...m, { role: "user", content: request }]);
    setInput("");
    setBusy(true);
    try {
      const proposal = await designSolution({ manifest: draft.manifest, skills: draft.skills, history, request });
      const content = proposal.reply || proposal.summary || (proposal.errors.length > 0 ? "I couldn't make that change." : "Done.");
      setMessages((m) => [...m, { role: "assistant", content, proposal, base: fingerprint(draft) }]);
    } catch (e) {
      setMessages((m) => [...m, { role: "assistant", content: apiErrorMessage(e), error: true }]);
    } finally {
      setBusy(false);
    }
  }

  function apply(index: number) {
    const message = messages[index];
    if (message.role !== "assistant" || !message.proposal?.manifest) return;
    if (message.base && message.base !== fingerprint(draft) &&
        !window.confirm("The solution changed since this was proposed. Applying it replaces those changes. Apply anyway?")) return;
    const next: Draft = {
      manifest: message.proposal.manifest,
      skills: (message.proposal.skills ?? []).map((s) => ({ name: s.name, description: s.description, instructions: s.instructions, workspace: s.workspace ?? null })),
    };
    const before = structuredClone(draft);
    onApply(next);
    setMessages((m) => m.map((x, i) => (i === index && x.role === "assistant" ? { ...x, state: "applied", before } : x)));
  }

  function undo(index: number) {
    const message = messages[index];
    if (message.role !== "assistant" || !message.before) return;
    onApply(message.before);
    setMessages((m) => m.map((x, i) => (i === index && x.role === "assistant" ? { ...x, state: undefined, before: undefined } : x)));
  }

  function discard(index: number) {
    setMessages((m) => m.map((x, i) => (i === index && x.role === "assistant" ? { ...x, state: "discarded" } : x)));
  }

  if (!open) {
    return (
      <button onClick={() => setOpen(true)}
        className={cx("fixed bottom-5 z-40 flex items-center gap-2", CORNER, " rounded-full bg-brand-600 px-4 py-2.5 text-sm font-medium text-white shadow-lg shadow-brand-600/25 transition hover:bg-brand-700 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-400 focus-visible:ring-offset-2")}>
        <Icons.Sparkles className="h-4 w-4" /> Design with AI
      </button>
    );
  }

  const lastProposal = messages.reduce((n, m, i) => (m.role === "assistant" && m.proposal?.can_apply ? i : n), -1);
  return (
    <section aria-label="Design with AI"
      className={cx("fixed inset-x-3 bottom-3 z-40 flex max-h-[min(42rem,calc(100vh-1.5rem))] flex-col overflow-hidden rounded-2xl border border-zinc-200 bg-white shadow-2xl sm:inset-x-auto sm:bottom-5 sm:w-[26rem] dark:border-zinc-800 dark:bg-zinc-900",
        CORNER === "right-5" ? "sm:right-5" : "sm:right-20")}>
      <header className="flex items-center gap-2 border-b border-zinc-200 px-4 py-3 dark:border-zinc-800">
        <Icons.Sparkles className="h-4 w-4 text-brand-600 dark:text-brand-400" />
        <div className="min-w-0 flex-1">
          <div className="text-sm font-semibold text-zinc-900 dark:text-zinc-50">Design with AI</div>
          <div className="truncate text-[11px] text-zinc-500">Describe it; review each change before it&apos;s applied.</div>
        </div>
        {messages.length > 0 && (
          <button onClick={() => window.confirm("Clear this conversation? The solution keeps every change you applied.") && setMessages([])}
            className="rounded-md px-2 py-1 text-xs text-zinc-500 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">Clear</button>
        )}
        <button onClick={() => setOpen(false)} aria-label="Minimize" className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800">
          <Icons.X />
        </button>
      </header>

      <div ref={scroller} className="min-h-[12rem] flex-1 space-y-3 overflow-y-auto px-4 py-4 text-sm">
        {messages.length === 0 && (
          <div className="space-y-3">
            <p className="text-zinc-600 dark:text-zinc-400">
              Tell me what the solution should do: its data, the work its agents run, what your customers&apos; assistant handles, the
              knowledge and skills they need, and how to check it works. I&apos;ll propose the changes; you apply the ones you want.
            </p>
            <div className="space-y-1.5">
              {SUGGESTIONS.map((s) => (
                <button key={s} onClick={() => send(s)}
                  className="block w-full rounded-lg border border-zinc-200 px-3 py-2 text-left text-xs text-zinc-700 transition hover:border-brand-300 hover:bg-brand-50/50 dark:border-zinc-800 dark:text-zinc-300 dark:hover:border-brand-800 dark:hover:bg-brand-500/5">
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}
        {messages.map((m, i) => m.role === "user" ? (
          <div key={i} className="ml-8 whitespace-pre-wrap rounded-2xl rounded-br-md bg-brand-600 px-3 py-2 text-white"><WithMentions text={m.content} mentionables={mentionables} /></div>
        ) : (
          <div key={i} className="mr-4 space-y-2">
            <div className={cx("whitespace-pre-wrap rounded-2xl rounded-bl-md px-3 py-2",
              m.error ? "bg-rose-50 text-rose-700 dark:bg-rose-950/40 dark:text-rose-300" : "bg-zinc-100 text-zinc-800 dark:bg-zinc-800 dark:text-zinc-100")}>
              {m.content}
            </div>
            {m.proposal && <ProposalCard proposal={m.proposal} state={m.state} stale={i !== lastProposal && !m.state}
              onApply={() => apply(i)} onDiscard={() => discard(i)} onUndo={() => undo(i)} />}
          </div>
        ))}
        {busy && (
          <div className="mr-4 flex items-center gap-1.5 rounded-2xl rounded-bl-md bg-zinc-100 px-3 py-2.5 text-zinc-500 dark:bg-zinc-800" aria-live="polite">
            <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.3s]" />
            <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.15s]" />
            <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-current" />
            <span className="sr-only">Designing…</span>
          </div>
        )}
      </div>

      <form onSubmit={(e) => { e.preventDefault(); send(input); }} className="border-t border-zinc-200 p-3 dark:border-zinc-800">
        <div className="flex items-end gap-2 rounded-xl border border-zinc-300 bg-white px-3 py-2 focus-within:border-brand-400 focus-within:ring-2 focus-within:ring-brand-100 dark:border-zinc-700 dark:bg-zinc-950 dark:focus-within:ring-brand-900/40">
          <MentionTextarea ref={textarea} rows={2} value={input} maxLength={4000} disabled={busy} above mentionables={mentionables}
            placeholder={messages.length === 0 ? "e.g. Track maintenance requests per property…" : "Ask for a change… (@ to mention a stage, skill, connector…)"}
            onValueChange={setInput}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(input); } }}
            wrapperClassName="flex-1"
            className="max-h-40 min-h-[2.5rem] w-full resize-none bg-transparent text-sm outline-none placeholder:text-zinc-400" />
          <button type="submit" disabled={busy || !input.trim()} aria-label="Send"
            className="rounded-lg bg-brand-600 p-1.5 text-white transition hover:bg-brand-700 disabled:opacity-40">
            <Icons.Send className="h-4 w-4" />
          </button>
        </div>
        <div className="mt-1.5 text-[10px] text-zinc-400">Enter to send · Shift+Enter for a new line · @ to mention · uses your organization&apos;s model</div>
      </form>
    </section>
  );
}

function ProposalCard({ proposal: p, state, stale, onApply, onDiscard, onUndo }: {
  proposal: SolutionDesignProposal; state?: "applied" | "discarded"; stale: boolean;
  onApply: () => void; onDiscard: () => void; onUndo: () => void;
}) {
  if (p.changes.length === 0 && p.errors.length === 0) return null;
  return (
    <div className={cx("rounded-xl border p-3 text-xs",
      state === "applied" ? "border-emerald-200 bg-emerald-50/50 dark:border-emerald-900/60 dark:bg-emerald-950/20"
        : state === "discarded" ? "border-zinc-200 opacity-60 dark:border-zinc-800"
        : "border-brand-200 bg-brand-50/40 dark:border-brand-900/60 dark:bg-brand-500/5")}>
      {p.changes.length > 0 && (
        <>
          <div className="mb-1 font-medium text-zinc-700 dark:text-zinc-300">{p.changes.length} change{p.changes.length === 1 ? "" : "s"}</div>
          <ul className="space-y-0.5 text-zinc-600 dark:text-zinc-400">
            {p.changes.map((c, i) => <li key={i} className="flex gap-1.5"><span className="text-brand-500">•</span>{c}</li>)}
          </ul>
        </>
      )}
      {p.errors.length > 0 && (
        <ul className="mt-2 list-disc space-y-0.5 pl-4 text-rose-600 dark:text-rose-400">{p.errors.map((e) => <li key={e}>{e}</li>)}</ul>
      )}
      {p.problems.length > 0 && (
        <details className="mt-2 text-amber-700 dark:text-amber-400">
          <summary className="cursor-pointer">{p.problems.length} problem{p.problems.length === 1 ? "" : "s"} to fix after applying</summary>
          <ul className="mt-1 list-disc space-y-0.5 pl-4">{p.problems.map((e) => <li key={e}>{e}</li>)}</ul>
        </details>
      )}
      {p.can_apply && (
        <div className="mt-3 flex items-center gap-2">
          {state === "applied" ? (
            <>
              <span className="font-medium text-emerald-700 dark:text-emerald-400">✓ Applied</span>
              <button onClick={onUndo} className="rounded-md px-2 py-1 text-zinc-600 hover:bg-white hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-800">Undo</button>
            </>
          ) : state === "discarded" ? (
            <span className="text-zinc-500">Discarded</span>
          ) : (
            <>
              <button onClick={onApply} className="rounded-md bg-brand-600 px-3 py-1 font-medium text-white hover:bg-brand-700">Apply</button>
              <button onClick={onDiscard} className="rounded-md px-2 py-1 text-zinc-600 hover:bg-white dark:text-zinc-400 dark:hover:bg-zinc-800">Discard</button>
              {stale && <span className="text-[10px] text-zinc-400">An older proposal</span>}
            </>
          )}
        </div>
      )}
    </div>
  );
}
