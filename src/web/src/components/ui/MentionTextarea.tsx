"use client";

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { getKnowledgeSummary, getLlmSettings, listSkills, modelChoices, type ModelChoice, type SkillSummary } from "@/lib/api";
import { cx } from "./index";

/**
 * Something you can @mention in a text box (docs/workspaces.md#mentions): an agent (a pipeline
 * stage, by its id), a model (an organization's profile, by its id), a provider, or a skill
 * (`@skill:name`), or knowledge (`@knowledge:refund-policy.docx`). The server explains each mention
 * to the model that reads the text, so "@claude-fast for @diagnose" means exactly that, however
 * capable the model is; an agent whose instructions name a skill loads it first, and one whose
 * instructions name knowledge reads it first.
 */
export type Mentionable = { kind: "agent" | "model" | "provider" | "skill" | "knowledge"; handle: string; label: string; detail?: string };

/** The handle the server's own model goes by (Mentions.DefaultModelHandle). */
export const DEFAULT_MODEL_HANDLE = "default-model";

const KIND_STYLE: Record<Mentionable["kind"], { label: string; className: string }> = {
  agent: { label: "Agent", className: "bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-300" },
  model: { label: "Model", className: "bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300" },
  provider: { label: "Provider", className: "bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300" },
  skill: { label: "Skill", className: "bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300" },
  knowledge: { label: "Knowledge", className: "bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" },
};

/** How knowledge is mentioned: a file name or a fact's key as one handle (KnowledgeFiles.Handle on the server). */
export function knowledgeHandle(nameOrKey: string) {
  return nameOrKey.trim().toLowerCase().replace(/[^a-z0-9._-]/g, "-").replace(/-{2,}/g, "-").replace(/^[-._]+|[-._]+$/g, "");
}

// Knowledge names, per scope (a workspace's own plus the organization's), for a minute.
const knowledgeCache = new Map<string, { at: number; promise: Promise<Mentionable[]> }>();
function loadKnowledge(workspaceId: string | null): Promise<Mentionable[]> {
  const key = workspaceId ?? "";
  const hit = knowledgeCache.get(key);
  if (hit && Date.now() - hit.at < 60_000) return hit.promise;
  const asMentionables = (s: { file_names: string[]; fact_keys?: string[] }, where: string): Mentionable[] => [
    ...s.file_names.map((f) => ({ kind: "knowledge" as const, handle: `knowledge:${knowledgeHandle(f)}`, label: f, detail: `Document · ${where}` })),
    ...(s.fact_keys ?? []).map((k) => ({ kind: "knowledge" as const, handle: `knowledge:${knowledgeHandle(k)}`, label: k, detail: `Fact · ${where}` })),
  ];
  const promise = Promise.all([
    workspaceId ? getKnowledgeSummary(workspaceId).then((s) => asMentionables(s, "this workspace")).catch(() => []) : Promise.resolve([]),
    getKnowledgeSummary().then((s) => asMentionables(s, "organization")).catch(() => []),
  ]).then(([own, org]) => [...own, ...org.filter((o) => !own.some((m) => m.handle === o.handle))]);
  knowledgeCache.set(key, { at: Date.now(), promise });
  return promise;
}

/** The documents and facts agents here can read, as mentionables: a workspace's own and its organization's. */
export function useKnowledgeMentionables(workspaceId?: string | null): Mentionable[] {
  const [knowledge, setKnowledge] = useState<Mentionable[]>([]);
  useEffect(() => {
    let live = true;
    loadKnowledge(workspaceId ?? null).then((k) => { if (live) setKnowledge(k); });
    return () => { live = false; };
  }, [workspaceId]);
  return knowledge;
}

// The organization's models change rarely: one request serves every text box for a minute.
let modelsCache: { at: number; promise: Promise<ModelChoice[]> } | null = null;
function loadModels(): Promise<ModelChoice[]> {
  if (!modelsCache || Date.now() - modelsCache.at > 60_000) {
    modelsCache = { at: Date.now(), promise: getLlmSettings().then(modelChoices).catch(() => []) };
  }
  return modelsCache.promise;
}

export function modelHandle(choice: Pick<ModelChoice, "id">) {
  return choice.id === "server" ? DEFAULT_MODEL_HANDLE : choice.id;
}

/** The models a stage or task can run on: the server's and the organization's profiles. */
export function useModelChoices(): ModelChoice[] {
  const [models, setModels] = useState<ModelChoice[]>([]);
  useEffect(() => {
    let live = true;
    loadModels().then((m) => { if (live) setModels(m); });
    return () => { live = false; };
  }, []);
  return models;
}

/** The organization's models and their providers, as mentionables. */
export function useModelMentionables(): Mentionable[] {
  const models = useModelChoices();
  return useMemo(() => {
    const providers = new Map<string, number>();
    for (const m of models) providers.set(m.provider, (providers.get(m.provider) ?? 0) + 1);
    return [
      ...models.map((m) => ({ kind: "model" as const, handle: modelHandle(m), label: m.name, detail: `${m.provider} · ${m.model}` })),
      ...[...providers].map(([p, n]) => ({ kind: "provider" as const, handle: p.toLowerCase(), label: p, detail: `${n} model${n === 1 ? "" : "s"}` })),
    ];
  }, [models]);
}

// Skills, per scope (the organization's, or a workspace's own plus the organization's), for a minute.
const skillsCache = new Map<string, { at: number; promise: Promise<SkillSummary[]> }>();
function loadSkills(workspaceId: string | null): Promise<SkillSummary[]> {
  const key = workspaceId ?? "";
  const hit = skillsCache.get(key);
  if (hit && Date.now() - hit.at < 60_000) return hit.promise;
  const enabled = (list: SkillSummary[]) => list.filter((s) => s.enabled);
  const promise = Promise.all([
    listSkills().then(enabled).catch(() => []),
    workspaceId ? listSkills(workspaceId).then(enabled).catch(() => []) : Promise.resolve([]),
  ]).then(([org, own]) => [...own, ...org.filter((s) => !own.some((o) => o.name === s.name))]);
  skillsCache.set(key, { at: Date.now(), promise });
  return promise;
}

/** The skills agents here can use, as mentionables: a workspace's own and its organization's. */
export function useSkillMentionables(workspaceId?: string | null): Mentionable[] {
  const [skills, setSkills] = useState<SkillSummary[]>([]);
  useEffect(() => {
    let live = true;
    loadSkills(workspaceId ?? null).then((s) => { if (live) setSkills(s); });
    return () => { live = false; };
  }, [workspaceId]);
  return useMemo(() => skills.map((s) => ({ kind: "skill" as const, handle: `skill:${s.name}`, label: s.name, detail: s.description })), [skills]);
}

/** Pipeline stages as mentionable agents. */
export function stageMentionables(stages: { stage_id: string; name: string; role?: string }[]): Mentionable[] {
  return stages.map((s) => ({ kind: "agent", handle: s.stage_id, label: s.name, detail: s.role && s.role !== s.name ? s.role : undefined }));
}

/** What a workspace's text boxes can mention: its pipeline's stages, and the skills and knowledge its agents use. */
export function useWorkspaceMentionables(workspace: { workspace_id: string; pipeline?: { stages: { stage_id: string; name: string; role?: string }[] } | null }): Mentionable[] {
  const skills = useSkillMentionables(workspace.workspace_id);
  const knowledge = useKnowledgeMentionables(workspace.workspace_id);
  const stages = workspace.pipeline?.stages;
  return useMemo(() => [...stageMentionables(stages ?? []), ...skills, ...knowledge], [stages, skills, knowledge]);
}

/** The handles mentioned in a text (without the @). */
export function mentionsIn(text: string): string[] {
  return [...text.matchAll(/(?<![\w.@])@([A-Za-z0-9][A-Za-z0-9._:/-]*[A-Za-z0-9]|[A-Za-z0-9])/g)].map((m) => m[1]);
}

type Props = Omit<React.TextareaHTMLAttributes<HTMLTextAreaElement>, "value" | "onChange"> & {
  value: string;
  onValueChange: (value: string) => void;
  /** What can be mentioned here. */
  mentionables: Mentionable[];
  /** One line (an input) instead of a text area. */
  singleLine?: boolean;
  /** Open the suggestions above the box (for boxes at the bottom of the screen). */
  above?: boolean;
  ref?: React.Ref<HTMLTextAreaElement>;
  wrapperClassName?: string;
};

/**
 * A text box where typing @ suggests agents, models and providers. ↑/↓ move, Enter or Tab picks,
 * Esc closes; while suggestions are open, Enter picks rather than submitting.
 */
export function MentionTextarea({ value, onValueChange, mentionables, singleLine, above, ref, wrapperClassName, onKeyDown, ...rest }: Props) {
  const box = useRef<HTMLTextAreaElement | HTMLInputElement | null>(null);
  const [query, setQuery] = useState<{ text: string; start: number } | null>(null);
  // Where the list goes on screen. It's drawn on the page itself, so a box inside a clipped
  // container (a rounded card, a modal) can't cut it off.
  const [anchor, setAnchor] = useState<{ left: number; top: number; bottom: number; width: number } | null>(null);
  const [active, setActive] = useState(0);
  // Where the caret goes once a picked mention is in the box (set as soon as React renders it,
  // so keys typed right after picking land after the mention).
  const caretAfterPick = useRef<number | null>(null);
  useLayoutEffect(() => {
    const el = box.current;
    if (el && caretAfterPick.current !== null) {
      el.setSelectionRange(caretAfterPick.current, caretAfterPick.current);
      caretAfterPick.current = null;
    }
  }, [value]);

  const matches = useMemo(() => {
    if (!query) return [];
    const q = query.text.toLowerCase();
    // Just "@": a few of each kind, so every kind shows up.
    if (q === "") {
      const kinds = ["agent", "skill", "knowledge", "model", "provider"] as const;
      return kinds.flatMap((k) => mentionables.filter((m) => m.kind === k).slice(0, 4)).slice(0, 12);
    }
    return mentionables
      .filter((m) => m.handle.toLowerCase().includes(q) || m.label.toLowerCase().includes(q))
      .sort((a, b) => Number(!a.handle.toLowerCase().startsWith(q)) - Number(!b.handle.toLowerCase().startsWith(q)))
      .slice(0, 8);
  }, [query, mentionables]);
  const open = matches.length > 0;

  // The list is placed on the page, so it closes when anything scrolls or the window resizes.
  useEffect(() => {
    if (!open) return;
    const close = (e: Event) => { if (!(e.target instanceof Node && document.querySelector("[data-mention-list]")?.contains(e.target))) setQuery(null); };
    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    return () => { window.removeEventListener("scroll", close, true); window.removeEventListener("resize", close); };
  }, [open]);

  function detect(el: HTMLTextAreaElement | HTMLInputElement) {
    const caret = el.selectionStart ?? el.value.length;
    const m = /(^|[\s(,])@([\w.:/-]*)$/.exec(el.value.slice(0, caret));
    setQuery(m ? { text: m[2], start: caret - m[2].length - 1 } : null);
    if (m) {
      const r = el.getBoundingClientRect();
      setAnchor({ left: r.left, top: r.top, bottom: r.bottom, width: r.width });
    }
    setActive(0);
  }

  function pick(item: Mentionable) {
    const el = box.current;
    if (!el || !query) return;
    const caret = el.selectionStart ?? value.length;
    const insert = `@${item.handle} `;
    onValueChange(value.slice(0, query.start) + insert + value.slice(caret));
    setQuery(null);
    caretAfterPick.current = query.start + insert.length;
    el.focus();
  }

  function keyDown(e: React.KeyboardEvent<HTMLTextAreaElement & HTMLInputElement>) {
    if (open) {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        setActive((i) => (i + (e.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length);
        return;
      }
      if (e.key === "Enter" || e.key === "Tab") {
        e.preventDefault();
        pick(matches[active] ?? matches[0]);
        return;
      }
      if (e.key === "Escape") {
        e.preventDefault();
        setQuery(null);
        return;
      }
    }
    onKeyDown?.(e);
  }

  const setRef = (el: HTMLTextAreaElement | HTMLInputElement | null) => {
    box.current = el;
    if (typeof ref === "function") ref(el as HTMLTextAreaElement);
    else if (ref) (ref as React.RefObject<HTMLTextAreaElement | null>).current = el as HTMLTextAreaElement;
  };

  const shared = {
    ...rest,
    value,
    onKeyDown: keyDown,
    onChange: (e: React.ChangeEvent<HTMLTextAreaElement & HTMLInputElement>) => { onValueChange(e.target.value); detect(e.target); },
    onClick: (e: React.MouseEvent<HTMLTextAreaElement & HTMLInputElement>) => detect(e.currentTarget),
    onBlur: (e: React.FocusEvent<HTMLTextAreaElement & HTMLInputElement>) => { setTimeout(() => setQuery(null), 120); rest.onBlur?.(e); },
    role: "combobox",
    "aria-autocomplete": "list" as const,
    "aria-expanded": open,
  };

  return (
    <div className={cx("relative", wrapperClassName)}>
      {singleLine
        ? <input ref={setRef} {...(shared as unknown as React.InputHTMLAttributes<HTMLInputElement>)} />
        : <textarea ref={setRef} {...shared} />}
      {open && anchor && createPortal(
        <ul role="listbox" data-mention-list
          style={{
            left: Math.min(anchor.left, window.innerWidth - 376),
            width: Math.max(256, Math.min(360, anchor.width)),
            // Above when asked, or when there's no room below.
            ...(above || anchor.bottom + 270 > window.innerHeight
              ? { bottom: window.innerHeight - anchor.top + 4 }
              : { top: anchor.bottom + 4 }),
          }}
          className="fixed z-[100] max-h-64 overflow-y-auto rounded-lg border border-zinc-200 bg-white py-1 text-xs shadow-xl dark:border-zinc-700 dark:bg-zinc-900">
          {matches.map((m, i) => (
            <li key={`${m.kind}:${m.handle}`} role="option" aria-selected={i === active}
              onMouseDown={(e) => { e.preventDefault(); pick(m); }}
              onMouseEnter={() => setActive(i)}
              className={cx("flex cursor-pointer items-center gap-2 px-2.5 py-1.5", i === active && "bg-zinc-100 dark:bg-zinc-800")}>
              <span className={cx("w-14 shrink-0 rounded px-1 py-0.5 text-center text-[10px] font-medium", KIND_STYLE[m.kind].className)}>{KIND_STYLE[m.kind].label}</span>
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-zinc-900 dark:text-zinc-100">{m.label}</span>
                <span className="block truncate text-[10px] text-zinc-500">@{m.handle}{m.detail ? ` · ${m.detail}` : ""}</span>
              </span>
            </li>
          ))}
          <li className="border-t border-zinc-100 px-2.5 pt-1 text-[10px] text-zinc-400 dark:border-zinc-800">↑↓ to move · Enter or Tab to pick · Esc to close</li>
        </ul>,
        document.body,
      )}
    </div>
  );
}
