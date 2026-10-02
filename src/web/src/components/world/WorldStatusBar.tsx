"use client";

import { useEffect, useState } from "react";
import { endWorld, pauseWorld, resumeWorld } from "@/lib/api";
import type { WorldSnapshot } from "@/lib/worldTypes";

export function WorldStatusBar({ world, onChanged }: { world: WorldSnapshot; onChanged: () => void }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  const pct = world.max_ticks > 0 ? Math.min(100, (world.tick / world.max_ticks) * 100) : 0;
  const remaining = world.status === "Running" && world.ends_at ? Math.max(0, Math.round((new Date(world.ends_at).getTime() - now) / 1000)) : null;
  const statusTone: Record<string, string> = {
    Running: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
    Paused: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
    Ended: "bg-zinc-200 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300",
    Created: "bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300",
  };

  async function act(fn: (id: string) => Promise<void>) {
    try {
      await fn(world.world_id);
    } finally {
      onChanged();
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-zinc-200 px-4 py-2 text-xs dark:border-zinc-800">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold">{world.name}</span>
          <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${statusTone[world.status]}`}>{world.status}</span>
        </div>
        <div className="max-w-xl truncate text-zinc-500" title={world.description}>{world.description}</div>
      </div>

      <div className="w-44">
        <div className="flex justify-between text-zinc-500">
          <span>Tick {world.tick}/{world.max_ticks}</span>
          <span>{world.tick_interval_seconds}s/tick</span>
        </div>
        <div className="mt-1 h-1.5 overflow-hidden rounded bg-zinc-200 dark:bg-zinc-800">
          <div className="h-full bg-blue-500 transition-all" style={{ width: `${pct}%` }} />
        </div>
      </div>

      <div className="text-zinc-500">
        <div>{world.totals.active_residents} active / {world.totals.total_residents} residents</div>
        <div>{remaining !== null ? `${Math.floor(remaining / 60)}m ${remaining % 60}s left` : world.status === "Ended" ? `Ended: ${world.end_reason}` : "clock stopped"}</div>
      </div>

      <div className="text-zinc-500">
        <div>{world.totals.tokens_used.toLocaleString()} tokens</div>
        <div>${world.totals.cost_usd.toFixed(4)} spent</div>
      </div>

      <div className="ml-auto flex gap-2">
        {world.status === "Running" && (
          <button onClick={() => act(pauseWorld)} className="rounded border border-zinc-300 px-2 py-1 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800">Pause</button>
        )}
        {world.status === "Paused" && (
          <button onClick={() => act(resumeWorld)} className="rounded border border-zinc-300 px-2 py-1 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800">Resume</button>
        )}
        {world.status !== "Ended" && (
          <button onClick={() => confirm("End this world? All residents will be retired.") && act(endWorld)} className="rounded border border-rose-300 px-2 py-1 text-rose-600 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-400 dark:hover:bg-rose-950">End world</button>
        )}
      </div>
    </div>
  );
}
