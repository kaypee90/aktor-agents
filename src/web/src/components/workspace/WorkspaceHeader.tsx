"use client";

import { workspaceAction } from "@/lib/api";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";

function Meter({ label, used, limit, format }: { label: string; used: number; limit: number; format: (n: number) => string }) {
  const pct = limit > 0 ? Math.min(100, (used / limit) * 100) : 0;
  const tone = pct >= 100 ? "bg-rose-500" : pct >= 80 ? "bg-amber-500" : "bg-emerald-500";
  return (
    <div className="shrink-0" style={{ width: 150 }}>
      <div className="text-[10px] text-neutral-500">{label}</div>
      <div className="text-[11px] tabular-nums">{format(used)} <span className="text-neutral-400">/ {format(limit)}</span></div>
      <div className="mt-1 h-1.5 overflow-hidden rounded bg-neutral-200 dark:bg-neutral-800"><div className={`h-full ${tone}`} style={{ width: `${pct}%` }} /></div>
    </div>
  );
}

export function WorkspaceHeader({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const tone: Record<string, string> = {
    Active: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
    Paused: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
    Archived: "bg-neutral-200 text-neutral-600 dark:bg-neutral-800 dark:text-neutral-300",
  };
  const act = (a: "pause" | "resume" | "archive") => workspaceAction(workspace.workspace_id, a).finally(onChanged);
  const btn = "rounded border border-neutral-300 px-2 py-1 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800";

  return (
    <div className="flex flex-wrap items-center gap-x-6 gap-y-2 border-b border-neutral-200 px-4 py-2 text-xs dark:border-neutral-800">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold">{workspace.name}</span>
          <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${tone[workspace.status]}`}>{workspace.status}</span>
        </div>
        <div className="max-w-xl truncate text-neutral-500" title={workspace.goal}>{workspace.goal}</div>
      </div>
      <Meter label="Tokens today" used={workspace.tokens_today} limit={workspace.daily_token_limit} format={(n) => n >= 1000 ? `${Math.round(n / 1000)}k` : `${n}`} />
      <Meter label="Cost today" used={workspace.cost_today} limit={workspace.daily_cost_limit_usd} format={(n) => `$${n.toFixed(2)}`} />
      <div className="shrink-0 text-[10px] text-neutral-500">
        <div>All time</div>
        <div className="text-[11px] tabular-nums text-neutral-700 dark:text-neutral-300">{workspace.total_tokens.toLocaleString()} tokens · ${workspace.total_cost_usd.toFixed(4)}</div>
      </div>
      <div className="flex shrink-0 gap-2">
        {workspace.status === "Active" && <button onClick={() => act("pause")} className={btn}>Pause</button>}
        {workspace.status === "Paused" && <button onClick={() => act("resume")} className={btn}>Resume</button>}
        {workspace.status !== "Archived" && (
          <button onClick={() => confirm("Archive this workspace? All its agents stop and its triggers are removed.") && act("archive")}
            className="rounded border border-rose-300 px-2 py-1 text-rose-600 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-400 dark:hover:bg-rose-950">
            Archive
          </button>
        )}
      </div>
    </div>
  );
}
