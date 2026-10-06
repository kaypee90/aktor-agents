"use client";

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { getLlmSettings, modelChoices, type ModelChoice } from "@/lib/api";
import { cx } from "./index";

/**
 * Something you can @mention in a text box (docs/workspaces.md#mentions): an agent (a pipeline
 * stage, by its id), a model (an organization's profile, by its id) or a provider. The server
 * explains each mention to the model that reads the text, so "@claude-fast for @diagnose" means
 * exactly that, however capable the model is.
 */
export type Mentionable = { kind: "agent" | "model" | "provider"; handle: string; label: string; detail?: string };

/** The handle the server's own model goes by (Mentions.DefaultModelHandle). */
export const DEFAULT_MODEL_HANDLE = "default-model";

const KIND_STYLE: Record<Mentionable["kind"], { label: string; className: string }> = {
  agent: { label: "Agent", className: "bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-300" },
  model: { label: "Model", className: "bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300" },
  provider: { label: "Provider", className: "bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300" },
};

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

/** Pipeline stages as mentionable agents. */
export function stageMentionables(stages: { stage_id: string; name: string; role?: string }[]): Mentionable[] {
  return stages.map((s) => ({ kind: "agent", handle: s.stage_id, label: s.name, detail: s.role && s.role !== s.name ? s.role : undefined }));
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
    return mentionables
      .filter((m) => m.handle.toLowerCase().includes(q) || m.label.toLowerCase().includes(q))
      .sort((a, b) => Number(!a.handle.toLowerCase().startsWith(q)) - Number(!b.handle.toLowerCase().startsWith(q)))
      .slice(0, 8);
  }, [query, mentionables]);
  const open = matches.length > 0;

  function detect(el: HTMLTextAreaElement | HTMLInputElement) {
    const caret = el.selectionStart ?? el.value.length;
    const m = /(^|[\s(,])@([\w.:/-]*)$/.exec(el.value.slice(0, caret));
    setQuery(m ? { text: m[2], start: caret - m[2].length - 1 } : null);
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
      {open && (
        <ul role="listbox"
          className={cx("absolute left-0 z-50 max-h-64 w-[min(360px,100%)] min-w-64 overflow-y-auto rounded-lg border border-zinc-200 bg-white py-1 text-xs shadow-xl dark:border-zinc-700 dark:bg-zinc-900",
            above ? "bottom-full mb-1" : "top-full mt-1")}>
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
        </ul>
      )}
    </div>
  );
}
