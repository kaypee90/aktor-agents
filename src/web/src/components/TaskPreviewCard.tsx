"use client";

import type { TaskPreview } from "@/lib/types";
import { Stat } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const usd = (n: number) => `$${n < 1 ? n.toFixed(3) : n.toFixed(2)}`;
const tokens = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}k` : `${n}`);
const duration = (s: number) => (s >= 3600 ? `${(s / 3600).toFixed(1)} h` : s >= 60 ? `${Math.round(s / 60)} min` : `${Math.round(s)} s`);

/** The planned team and the expected spend for a task, before (or while) it runs. */
export function TaskPreviewCard({ preview, compact = false }: { preview: TaskPreview; compact?: boolean }) {
  const e = preview.estimate;
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <Stat label="Team" value={`${preview.team_size} agents`} hint={`${preview.max_depth + 1} level${preview.max_depth > 0 ? "s" : ""}${preview.goal_type ? ` · ${preview.goal_type}` : ""}`} />
        <Stat label="Likely cost" value={usd(e.cost_usd_expected)} hint={`${usd(e.cost_usd_low)} – ${usd(e.cost_usd_high)}`} />
        <Stat label="Tokens" value={tokens(e.tokens_expected)} hint={`${tokens(e.tokens_low)} – ${tokens(e.tokens_high)}`} />
        <Stat label="Time" value={duration(e.duration_seconds_expected)} hint={`${duration(e.duration_seconds_low)} – ${duration(e.duration_seconds_high)}`} />
      </div>

      <div className="flex items-start gap-2 rounded-lg bg-zinc-50 px-3 py-2 text-xs text-zinc-600 dark:bg-zinc-950 dark:text-zinc-400">
        <Icons.Shield className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-500" />
        <span>
          Enforced budget: <span className="font-medium text-zinc-800 dark:text-zinc-200">{usd(preview.budget.max_cost_usd)}</span> and{" "}
          <span className="font-medium text-zinc-800 dark:text-zinc-200">{tokens(preview.budget.max_tokens)}</span> tokens.
          {preview.capped_by_budget && " The high end is limited by it: the team is stopped, and reports what it has, before it can spend more."}
        </span>
      </div>

      {!compact && (
        <div>
          <div className="mb-1.5 text-xs font-medium text-zinc-700 dark:text-zinc-300">Planned team</div>
          <ul className="space-y-1 text-xs">
            {preview.team.map((m, i) => (
              <li key={i} className="flex items-start gap-2" style={{ paddingLeft: `${m.depth * 18}px` }}>
                <span className={`mt-1 h-1.5 w-1.5 shrink-0 rounded-full ${m.depth === 0 ? "bg-brand-500" : "bg-zinc-400"}`} />
                <span>
                  <span className="font-medium text-zinc-800 dark:text-zinc-200">{m.role}</span>
                  <span className="text-zinc-500"> · {m.purpose}</span>
                </span>
              </li>
            ))}
          </ul>
          {preview.rationale && <p className="mt-2 text-xs italic text-zinc-500">{preview.rationale}</p>}
        </div>
      )}

      <div className="text-[11px] text-zinc-400">
        {preview.calibration.source === "history" ? `Calibrated on ${preview.calibration.samples} recent run(s)` : "From defaults (no history yet)"}
        {" · "}this estimate cost {usd(preview.planning.cost_usd)}.
      </div>
    </div>
  );
}
