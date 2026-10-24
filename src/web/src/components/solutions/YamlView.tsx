"use client";

import { useEffect, useMemo, useRef } from "react";
import { cx } from "@/components/ui";
import { locateYamlProblem, type YamlLocation } from "@/lib/yamlProblems";

/** Each line is this tall (text-xs, leading-5), so a line number is a scroll offset. */
const LINE_PX = 20;

export interface LocatedProblem {
  problem: string;
  at: YamlLocation | null;
}

/** Where each problem is in yaml (null when it isn't in this file). */
export function useLocatedProblems(yaml: string, problems: string[]): LocatedProblem[] {
  return useMemo(() => problems.map((problem) => ({ problem, at: locateYamlProblem(yaml, problem) })), [yaml, problems]);
}

/** For each line, how it's marked: the problem's own line, or in that problem's block. */
function marks(located: LocatedProblem[]) {
  const lines = new Map<number, { strong: boolean; problems: string[] }>();
  for (const { problem, at } of located) {
    if (!at) continue;
    for (let l = at.from; l <= at.to; l++) {
      const m = lines.get(l) ?? { strong: false, problems: [] };
      if (l === at.line) {
        m.strong = true;
        m.problems.push(problem);
      }
      lines.set(l, m);
    }
  }
  return lines;
}

const rowClass = (mark: { strong: boolean } | undefined) =>
  mark?.strong ? "bg-rose-200/70 dark:bg-rose-900/50"
    : mark ? "bg-rose-50 dark:bg-rose-950/30"
    : undefined;

/**
 * aktor.yaml with line numbers and the lines that have problems highlighted. Read-only, or
 * editable (a textarea over the highlighted text, so the highlights follow what's typed).
 * Setting focusLine scrolls to it.
 */
export function YamlView({ text, located, onChange, focusLine, className }: {
  text: string;
  located: LocatedProblem[];
  onChange?: (text: string) => void;
  focusLine?: { line: number } | null;
  className?: string;
}) {
  const lines = useMemo(() => text.split("\n"), [text]);
  const marked = useMemo(() => marks(located), [located]);
  const scroller = useRef<HTMLDivElement>(null);
  const editor = useRef<HTMLTextAreaElement>(null);
  const backdrop = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!focusLine) return;
    const top = Math.max(0, (focusLine.line - 4) * LINE_PX);
    if (editor.current) {
      const start = lines.slice(0, focusLine.line - 1).reduce((n, l) => n + l.length + 1, 0);
      editor.current.focus();
      editor.current.setSelectionRange(start, start + (lines[focusLine.line - 1]?.length ?? 0));
      editor.current.scrollTop = top;
    } else if (scroller.current) {
      scroller.current.scrollTop = top;
    }
    // Only when asked to focus, not as the text changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusLine]);

  const rows = lines.map((line, i) => {
    const mark = marked.get(i + 1);
    return (
      <div key={i} className={cx("flex", rowClass(mark))} title={mark?.problems.join("\n") || undefined}>
        <span className={cx("w-10 shrink-0 select-none pr-3 text-right", mark?.strong ? "font-semibold text-rose-600 dark:text-rose-400" : "text-zinc-400 dark:text-zinc-600")}>{i + 1}</span>
        <span className="whitespace-pre pr-4">{line || " "}</span>
      </div>
    );
  });

  const frame = cx("bg-zinc-50 font-mono text-xs leading-5 text-zinc-800 dark:bg-zinc-950 dark:text-zinc-300", className);
  if (!onChange) {
    return <div ref={scroller} className={cx(frame, "overflow-auto py-4")}><div className="w-max min-w-full">{rows}</div></div>;
  }

  // Editing: the rows (numbers and highlights, text transparent) scroll with the textarea on top.
  return (
    <div className={cx(frame, "relative overflow-hidden")}>
      <div ref={backdrop} aria-hidden className="pointer-events-none absolute inset-0 overflow-hidden py-4 text-transparent">
        <div className="w-max min-w-full">{rows}</div>
      </div>
      <textarea ref={editor} value={text} spellCheck={false} wrap="off"
        className="relative block h-full w-full resize-none bg-transparent py-4 pl-10 pr-4 caret-zinc-800 outline-none dark:caret-zinc-200"
        onScroll={(e) => {
          if (!backdrop.current) return;
          backdrop.current.scrollTop = e.currentTarget.scrollTop;
          backdrop.current.scrollLeft = e.currentTarget.scrollLeft;
        }}
        onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

/** Problems as a list; those found in the YAML link to their line. */
export function YamlProblemList({ located, onPick, className }: { located: LocatedProblem[]; onPick?: (line: number) => void; className?: string }) {
  return (
    <ul className={cx("list-disc space-y-0.5 pl-5 text-xs", className)}>
      {located.map(({ problem, at }) => (
        <li key={problem}>
          {problem}
          {at && onPick && (
            <button type="button" className="ml-1.5 rounded bg-rose-100 px-1.5 font-mono text-[11px] text-rose-700 hover:bg-rose-200 dark:bg-rose-900/50 dark:text-rose-300 dark:hover:bg-rose-900"
              onClick={() => onPick(at.line)}>line {at.line}</button>
          )}
        </li>
      ))}
    </ul>
  );
}

/** The few lines around a problem, highlighted: for places that don't show the whole file. */
export function YamlExcerpt({ text, at }: { text: string; at: YamlLocation }) {
  const lines = text.split("\n");
  const first = Math.max(1, Math.min(at.from, at.line - 2));
  const last = Math.min(lines.length, Math.max(at.line + 2, Math.min(at.to, at.line + 4)));
  return (
    <div className="mt-1 overflow-x-auto rounded border border-rose-200 bg-white py-1 font-mono text-[11px] leading-4 text-zinc-700 dark:border-rose-900/60 dark:bg-zinc-950 dark:text-zinc-300">
      {lines.slice(first - 1, last).map((line, i) => {
        const n = first + i;
        const strong = n === at.line;
        return (
          <div key={n} className={cx("flex", strong ? "bg-rose-200/70 dark:bg-rose-900/50" : n >= at.from && n <= at.to && "bg-rose-50 dark:bg-rose-950/30")}>
            <span className={cx("w-9 shrink-0 select-none pr-2 text-right", strong ? "font-semibold text-rose-600 dark:text-rose-400" : "text-zinc-400")}>{n}</span>
            <span className="whitespace-pre pr-3">{line || " "}</span>
          </div>
        );
      })}
    </div>
  );
}
