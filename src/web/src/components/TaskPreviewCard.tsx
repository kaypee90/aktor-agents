"use client";

import type { TaskPreview } from "@/lib/types";

const usd = (n: number) => `$${n < 1 ? n.toFixed(3) : n.toFixed(2)}`;
const tokens = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}k` : `${n}`);
const duration = (s: number) => (s >= 3600 ? `${(s / 3600).toFixed(1)} h` : s >= 60 ? `${Math.round(s / 60)} min` : `${Math.round(s)} s`);

/** The planned team and the expected spend for a task, before (or while) it runs. */
export function TaskPreviewCard({ preview, compact = false }: { preview: TaskPreview; compact?: boolean }) {
  const e = preview.estimate;
  return (
    <div className="space-y-2 text-xs">
      <div className="flex flex-wrap gap-x-5 gap-y-1">
        <span>
          <span className="text-neutral-500">Team:</span> {preview.team_size} agents, {preview.max_depth + 1} level
          {preview.max_depth > 0 ? "s" : ""}
          {preview.goal_type && <span className="text-neutral-500"> ({preview.goal_type})</span>}
        </span>
        <span>
          <span className="text-neutral-500">Cost:</span> {usd(e.cost_usd_low)}–{usd(e.cost_usd_high)}{" "}
          <span className="text-neutral-500">(likely {usd(e.cost_usd_expected)})</span>
        </span>
        <span>
          <span className="text-neutral-500">Tokens:</span> {tokens(e.tokens_low)}–{tokens(e.tokens_high)}
        </span>
        <span>
          <span className="text-neutral-500">Time:</span> {duration(e.duration_seconds_low)}–{duration(e.duration_seconds_high)}
        </span>
        <span>
          <span className="text-neutral-500">Budget cap:</span> {usd(preview.budget.max_cost_usd)} / {tokens(preview.budget.max_tokens)} tokens
        </span>
      </div>
      {preview.capped_by_budget && (
        <div className="text-amber-700 dark:text-amber-300">
          The high end is limited by the budget: the runtime stops the team (and reports what it has) before it can spend more.
        </div>
      )}
      {!compact && (
        <ul className="space-y-0.5">
          {preview.team.map((m, i) => (
            <li key={i} style={{ paddingLeft: `${m.depth * 14}px` }}>
              <span className="font-medium">{m.role}</span>
              <span className="text-neutral-500"> — {m.purpose}</span>
            </li>
          ))}
        </ul>
      )}
      <div className="text-[11px] text-neutral-400">
        Estimate {preview.calibration.source === "history" ? `calibrated on ${preview.calibration.samples} recent task(s)` : "from defaults (no history yet)"};
        the preview itself cost {usd(preview.planning.cost_usd)}.
      </div>
    </div>
  );
}
