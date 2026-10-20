"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { StatusBadge, cx, inlineInputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { listSolutions, listWorkspaces, type SolutionSummary, type WorkspaceListFilter } from "@/lib/api";
import type { WorkspaceListItem } from "@/lib/workspaceTypes";
import { withoutTemplateTag } from "@/components/workspace/WorkspaceHeader";

const PAGE = 50;
const FILTER_KEY = "aktor:workspaceListFilter";

type Status = NonNullable<WorkspaceListFilter["status"]>;
type Filter = { status: Status; solution: string; q: string };
const DEFAULT_FILTER: Filter = { status: "active", solution: "", q: "" };

function readFilter(): Filter {
  try {
    const saved = JSON.parse(localStorage.getItem(FILTER_KEY) ?? "null") as Partial<Filter> | null;
    return { ...DEFAULT_FILTER, ...saved, q: "" };
  } catch {
    return DEFAULT_FILTER;
  }
}

const STATUSES: { value: Status; label: string }[] = [
  { value: "active", label: "Active" },
  { value: "archived", label: "Archived" },
  { value: "all", label: "All" },
];

/**
 * The workspace list beside an open workspace: filtered by status (active means not archived), by
 * the solution that made it and by words in its name or goal, and loaded a page at a time.
 * `reloadKey` changing reloads what's shown (after a workspace is opened, archived or renamed).
 */
export function WorkspaceList({ selectedId, onOpen, reloadKey }: { selectedId: string | null; onOpen: (id: string) => void; reloadKey: number }) {
  const [filter, setFilter] = useState<Filter>(readFilter);
  const [words, setWords] = useState("");
  const [items, setItems] = useState<WorkspaceListItem[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [solutions, setSolutions] = useState<SolutionSummary[]>([]);
  // Only the latest request's answer is shown, so a slow page can't overwrite a newer filter's.
  const latest = useRef(0);
  const shown = useRef(0);
  useEffect(() => { shown.current = items.length; }, [items]);

  const change = useCallback((next: Partial<Filter>) => {
    setFilter((f) => {
      const merged = { ...f, ...next };
      if (merged.status === f.status && merged.solution === f.solution && merged.q === f.q) return f;
      try { localStorage.setItem(FILTER_KEY, JSON.stringify({ status: merged.status, solution: merged.solution })); } catch { /* only costs the remembered filter */ }
      return merged;
    });
  }, []);

  // Typing settles for a moment before it searches.
  useEffect(() => {
    const t = setTimeout(() => change({ q: words.trim() }), 250);
    return () => clearTimeout(t);
  }, [words, change]);

  // Asks for a page; the answer is shown only if no newer request was made since.
  const load = useCallback((offset: number, count: number, append: boolean) => {
    const request = ++latest.current;
    // One more than asked says whether there's another page.
    return listWorkspaces({ status: filter.status, solution: filter.solution, q: filter.q, offset, limit: count + 1 })
      .then((rows) => {
        if (request !== latest.current) return;
        setHasMore(rows.length > count);
        const page = rows.slice(0, count);
        setItems((before) => (append ? [...before, ...page.filter((w) => !before.some((b) => b.workspace_id === w.workspace_id))] : page));
      })
      .catch(() => { /* the next reload retries */ })
      .finally(() => { if (request === latest.current) setLoading(false); });
  }, [filter]);

  // A new filter starts from the first page; the old rows stay until the new ones arrive.
  useEffect(() => { void load(0, PAGE, false); }, [load]);

  // A reload keeps as many as were shown.
  const firstReload = useRef(true);
  useEffect(() => {
    if (firstReload.current) { firstReload.current = false; return; }
    void load(0, Math.max(shown.current, PAGE), false);
    // Only on reloadKey: a filter change already loads.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reloadKey]);

  useEffect(() => {
    listSolutions().then(setSolutions).catch(() => setSolutions([]));
  }, []);

  const filtered = filter.status !== "active" || filter.solution !== "" || filter.q !== "";
  const solutionKnown = filter.solution === "" || filter.solution === "none" || solutions.some((s) => s.install_id === filter.solution);

  return (
    <aside className="flex h-full flex-col bg-zinc-50/50 dark:bg-zinc-950">
      <div className="space-y-1.5 border-b border-zinc-200 p-2 dark:border-zinc-800">
        <div role="tablist" aria-label="Workspace status" className="flex rounded-lg bg-zinc-100 p-0.5 dark:bg-zinc-900">
          {STATUSES.map((s) => (
            <button key={s.value} role="tab" aria-selected={filter.status === s.value} onClick={() => change({ status: s.value })}
              className={cx("flex-1 rounded-md px-2 py-1 text-xs font-medium transition-colors",
                filter.status === s.value
                  ? "bg-white text-zinc-900 shadow-sm dark:bg-zinc-800 dark:text-zinc-100"
                  : "text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200")}>
              {s.label}
            </button>
          ))}
        </div>
        {(solutions.length > 0 || filter.solution !== "") && (
          <select aria-label="Solution" className={cx(inlineInputClass, "w-full py-1 text-xs")} value={solutionKnown ? filter.solution : ""}
            onChange={(e) => change({ solution: e.target.value })}>
            <option value="">All solutions</option>
            {solutions.map((s) => (
              <option key={s.install_id} value={s.install_id}>{s.title || s.name}{s.status === "Uninstalled" ? " (uninstalled)" : ""}</option>
            ))}
            <option value="none">Not from a solution</option>
          </select>
        )}
        <input type="search" aria-label="Search workspaces" placeholder="Search name or goal" value={words} onChange={(e) => setWords(e.target.value)}
          className={cx(inlineInputClass, "w-full py-1 text-xs")} />
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {!loading && items.length === 0 && (
          <div className="p-4 text-xs text-zinc-500">
            {!filtered ? "No workspaces yet." : filter.status === "archived" && !filter.q && !filter.solution ? "No archived workspaces." : "No workspaces match."}
          </div>
        )}
        <ul className="space-y-1">
          {items.map((w) => (
            <li key={w.workspace_id}>
              <button onClick={() => onOpen(w.workspace_id)}
                className={cx(
                  "block w-full rounded-lg px-3 py-2.5 text-left transition-colors",
                  w.workspace_id === selectedId
                    ? "bg-white shadow-sm ring-1 ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800"
                    : "hover:bg-zinc-100 dark:hover:bg-zinc-900/60",
                )}>
                <span className="flex items-center justify-between gap-2">
                  <span className="flex min-w-0 items-center gap-1.5">
                    {w.visibility === "Private" && <span title="Only you can see this workspace"><Icons.Lock className="h-3.5 w-3.5 shrink-0 text-amber-600 dark:text-amber-400" /></span>}
                    <span className="truncate text-sm font-medium text-zinc-900 dark:text-zinc-100">{w.name}</span>
                  </span>
                  <StatusBadge status={w.status} />
                </span>
                <span className="mt-0.5 line-clamp-2 text-xs text-zinc-500">{withoutTemplateTag(w.goal)}</span>
                <span className="mt-1 flex min-w-0 items-center gap-1.5 text-[11px] text-zinc-400">
                  <span className="shrink-0">{w.triggers} trigger{w.triggers === 1 ? "" : "s"}</span>
                  {w.solution && (
                    <span className="truncate" title={`Made by the solution ${w.solution.title}`}>· {w.solution.title}</span>
                  )}
                </span>
              </button>
            </li>
          ))}
        </ul>
        {hasMore && (
          <button onClick={() => { setLoading(true); load(items.length, PAGE, true); }} disabled={loading}
            className="mt-2 w-full rounded-lg py-1.5 text-xs font-medium text-zinc-500 hover:bg-zinc-100 hover:text-zinc-800 disabled:opacity-50 dark:hover:bg-zinc-900 dark:hover:text-zinc-200">
            {loading ? "Loading…" : "Show more"}
          </button>
        )}
      </div>
    </aside>
  );
}
