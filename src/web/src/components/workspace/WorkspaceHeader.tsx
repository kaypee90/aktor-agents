"use client";

import { useState } from "react";
import { simulateWorkspaceAlert, updateWorkspaceBudget, workspaceAction } from "@/lib/api";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";

function Meter({ label, used, limit, format }: { label: string; used: number; limit: number; format: (n: number) => string }) {
  const pct = limit > 0 ? Math.min(100, (used / limit) * 100) : 0;
  const tone = pct >= 100 ? "bg-rose-500" : pct >= 80 ? "bg-amber-500" : "bg-emerald-500";
  return (
    <div className="shrink-0" style={{ width: 150 }}>
      <div className="text-[10px] text-zinc-500">{label}</div>
      <div className="text-[11px] tabular-nums">{format(used)} <span className="text-zinc-400">/ {format(limit)}</span></div>
      <div className="mt-1 h-1.5 overflow-hidden rounded bg-zinc-200 dark:bg-zinc-800"><div className={`h-full ${tone}`} style={{ width: `${pct}%` }} /></div>
    </div>
  );
}

/** What the efficiency features saved: checks run with no LLM call, and cached input share. */
function Efficiency({ workspace }: { workspace: WorkspaceSnapshot }) {
  const tokens = workspace.agents.reduce((n, a) => n + a.tokens_used, 0);
  const cached = workspace.agents.reduce((n, a) => n + (a.cached_input_tokens ?? 0), 0);
  const avoided = workspace.llm_calls_avoided ?? 0;
  if (avoided === 0 && cached === 0) return null;
  return (
    <div className="shrink-0 text-[10px] text-zinc-500" title="Watches run checks in code; cached tokens are billed at a discount">
      <div>Saved</div>
      <div className="text-[11px] tabular-nums text-emerald-700 dark:text-emerald-400">
        {avoided > 0 && `${avoided.toLocaleString()} LLM calls`}
        {avoided > 0 && cached > 0 && " · "}
        {cached > 0 && `${Math.round((cached / Math.max(1, tokens)) * 100)}% cached`}
      </div>
    </div>
  );
}

/** The next midnight UTC, when the daily budget renews, as the user's local time. */
function renewsAt(): string {
  const now = new Date();
  const midnight = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1));
  return midnight.toLocaleString(undefined, { weekday: "short", hour: "2-digit", minute: "2-digit" });
}

/** Shown while today's budget is used up: agents are paused, and raising the budget lets them
 * carry on straight away (each parked agent re-checks within a minute). */
function BudgetBanner({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [tokens, setTokens] = useState(workspace.daily_token_limit * 2);
  const [dollars, setDollars] = useState(Math.round(workspace.daily_cost_limit_usd * 200) / 100);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const outOfTokens = workspace.tokens_today >= workspace.daily_token_limit;
  const outOfCost = workspace.cost_today >= workspace.daily_cost_limit_usd;
  if (workspace.status === "Archived" || (!outOfTokens && !outOfCost)) return null;

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await updateWorkspaceBudget(workspace.workspace_id, { daily_token_limit: tokens, daily_cost_limit_usd: dollars });
      setEditing(false);
      onChanged();
    } catch (err) {
      const message = err instanceof Error ? err.message : "";
      setError(/ 403 /.test(message) ? "Only an admin or owner can change the budget." : "Couldn't update the budget.");
    } finally {
      setSaving(false);
    }
  }

  const input = "w-28 rounded border border-amber-300 bg-white px-2 py-0.5 text-zinc-900 dark:border-amber-700 dark:bg-zinc-900 dark:text-zinc-100";
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-amber-300 bg-amber-50 px-4 py-2 text-xs text-amber-900 dark:border-amber-800 dark:bg-amber-950/60 dark:text-amber-100">
      <span className="font-semibold">Agents are paused: today&apos;s {outOfTokens ? "token" : "cost"} budget is used up.</span>
      <span>They carry on at midnight UTC ({renewsAt()} your time), or as soon as you raise the budget.</span>
      {!editing ? (
        <button onClick={() => setEditing(true)} className="ml-auto rounded bg-amber-600 px-2.5 py-1 font-medium text-white hover:bg-amber-700">
          Raise budget
        </button>
      ) : (
        <form onSubmit={save} className="ml-auto flex flex-wrap items-center gap-2">
          <label className="flex items-center gap-1">
            Tokens/day
            <input type="number" min={1000} step={1} value={tokens} onChange={(e) => setTokens(Number(e.target.value) || 1000)} className={input} />
          </label>
          <label className="flex items-center gap-1">
            $/day
            <input type="number" min={0.01} step={0.01} value={dollars} onChange={(e) => setDollars(Number(e.target.value) || 0.01)} className={input} />
          </label>
          <button type="submit" disabled={saving} className="rounded bg-amber-600 px-2.5 py-1 font-medium text-white hover:bg-amber-700 disabled:opacity-50">
            {saving ? "Saving…" : "Save"}
          </button>
          <button type="button" onClick={() => setEditing(false)} className="px-1 hover:underline">Cancel</button>
        </form>
      )}
      {error && <span className="w-full text-rose-700 dark:text-rose-300">{error}</span>}
    </div>
  );
}

export function WorkspaceHeader({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const tone: Record<string, string> = {
    Active: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
    Paused: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
    Archived: "bg-zinc-200 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300",
  };
  const act = (a: "pause" | "resume" | "archive") => workspaceAction(workspace.workspace_id, a).finally(onChanged);
  const btn = "rounded-lg border border-zinc-200 bg-white px-2.5 py-1.5 font-medium text-zinc-700 shadow-sm hover:bg-zinc-50 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-200 dark:hover:bg-zinc-800";

  return (
    <>
      <div className="flex flex-wrap items-center gap-x-6 gap-y-2 border-b border-zinc-200 px-4 py-2.5 text-xs dark:border-zinc-800">
        <div className="min-w-48 flex-1">
          <div className="flex items-center gap-2">
            <span className="truncate whitespace-nowrap text-sm font-semibold text-zinc-900 dark:text-zinc-100">{workspace.name}</span>
            <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${tone[workspace.status]}`}>{workspace.status}</span>
          </div>
          <div className="max-w-xl truncate text-zinc-500" title={workspace.goal}>{withoutTemplateTag(workspace.goal)}</div>
        </div>
        <Meter label="Tokens today" used={workspace.tokens_today} limit={workspace.daily_token_limit} format={(n) => n >= 1000 ? `${Math.round(n / 1000)}k` : `${n}`} />
        <Meter label="Cost today" used={workspace.cost_today} limit={workspace.daily_cost_limit_usd} format={(n) => `$${n.toFixed(2)}`} />
        <Efficiency workspace={workspace} />
        <div className="shrink-0 text-[10px] text-zinc-500">
          <div>All time</div>
          <div className="text-[11px] tabular-nums text-zinc-700 dark:text-zinc-300">{workspace.total_tokens.toLocaleString()} tokens · ${workspace.total_cost_usd.toFixed(4)}</div>
        </div>
        <div className="flex shrink-0 gap-2">
          {workspace.template_id && workspace.status === "Active" && workspace.triggers.some((t) => t.kind === "Webhook") && (
            <button
              onClick={() => simulateWorkspaceAlert(workspace.workspace_id).finally(onChanged)}
              title="Send the template's sample event through this workspace's own webhook: it starts a run"
              className={btn}
            >
              {workspace.template_id === "incident-response" ? "Simulate alert" : "Send sample event"}
            </button>
          )}
          {workspace.status === "Active" && <button onClick={() => act("pause")} className={btn}>Pause</button>}
          {workspace.status === "Paused" && <button onClick={() => act("resume")} className={btn}>Resume</button>}
          {workspace.status !== "Archived" && (
            <button onClick={() => confirm("Archive this workspace? All its agents stop and its triggers are removed.") && act("archive")}
              className="rounded-lg border border-rose-200 px-2.5 py-1.5 font-medium text-rose-600 hover:bg-rose-50 dark:border-rose-900/60 dark:text-rose-400 dark:hover:bg-rose-950/50">
              Archive
            </button>
          )}
        </div>
      </div>
      <BudgetBanner key={`${workspace.daily_token_limit}:${workspace.daily_cost_limit_usd}`} workspace={workspace} onChanged={onChanged} />
    </>
  );
}

/** A template's goal starts with "[template-id template]"; the name already says that. */
export function withoutTemplateTag(goal: string) {
  return goal.replace(/^\[[^\]]+ template\]\s*/, "");
}
