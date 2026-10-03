"use client";

import { useCallback, useEffect, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { listWorkspaces } from "@/lib/api";
import type { WorkspaceListItem } from "@/lib/workspaceTypes";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/**
 * Whose skills or knowledge a page shows: the whole organization's, or one workspace's own (which
 * only that workspace's agents use). Kept in the URL as ?workspace=, so a workspace can link to it.
 */
export function useScope(): [string | null, (workspace: string | null) => void] {
  const router = useRouter();
  const pathname = usePathname();
  const workspace = useSearchParams().get("workspace");
  const set = useCallback((next: string | null) => {
    router.replace(next ? `${pathname}?workspace=${encodeURIComponent(next)}` : pathname, { scroll: false });
  }, [router, pathname]);
  return [workspace, set];
}

export function ScopePicker({ value, onChange, what }: {
  value: string | null;
  onChange: (workspace: string | null) => void;
  /** "skills" or "knowledge", for the explanation. */
  what: string;
}) {
  const [workspaces, setWorkspaces] = useState<WorkspaceListItem[] | null>(null);
  useEffect(() => {
    listWorkspaces().then((w) => setWorkspaces(w.filter((x) => x.status !== "Archived"))).catch(() => setWorkspaces([]));
  }, []);
  const current = workspaces?.find((w) => w.workspace_id === value);

  return (
    <div className="flex flex-wrap items-center gap-3 rounded-xl border border-zinc-200 bg-white px-3 py-2 dark:border-zinc-800 dark:bg-zinc-900">
      <span className="flex items-center gap-1.5 text-xs font-medium text-zinc-500">
        {value ? <Icons.Workspaces className="h-3.5 w-3.5" /> : <Icons.Knowledge className="h-3.5 w-3.5" />} Scope
      </span>
      <select value={value ?? ""} onChange={(e) => onChange(e.target.value || null)}
        className="rounded-lg border border-zinc-200 bg-white px-2 py-1 text-sm dark:border-zinc-700 dark:bg-zinc-950">
        <option value="">Whole organization</option>
        {(workspaces ?? []).map((w) => <option key={w.workspace_id} value={w.workspace_id}>Workspace: {w.name}</option>)}
        {value && workspaces && !current && <option value={value}>Workspace {value}</option>}
      </select>
      <span className={cx("text-xs", value ? "text-brand-700 dark:text-brand-300" : "text-zinc-500")}>
        {value
          ? `Only the agents of ${current ? `the ${current.name} workspace` : "this workspace"} use these ${what}, on top of the organization's.`
          : `Every agent of your organization can use these ${what}.`}
      </span>
    </div>
  );
}
