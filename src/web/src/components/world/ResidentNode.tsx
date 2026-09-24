"use client";

import { Handle, Position, type NodeProps } from "@xyflow/react";
import type { Resident } from "@/lib/worldTypes";
import { BotIcon } from "../BotIcon";
import { accentFor } from "./worldUi";

export type ResidentNodeData = {
  resident: Resident;
  maxEnergy: number;
  bubble: { text: string; private: boolean } | null;
  busy: boolean;
  worldEnded: boolean;
};

export const RESIDENT_NODE_WIDTH = 176;
export const RESIDENT_NODE_HEIGHT = 96;

export function ResidentNode({ data, selected }: NodeProps & { data: ResidentNodeData }) {
  const { resident, maxEnergy, bubble, busy, worldEnded } = data;
  const dormant = resident.state === "Dormant";
  const energyPct = Math.max(0, Math.min(100, (resident.energy / maxEnergy) * 100));
  const energyColor = energyPct > 50 ? "bg-emerald-500" : energyPct > 20 ? "bg-amber-500" : "bg-rose-500";

  return (
    <div
      style={{ width: RESIDENT_NODE_WIDTH }}
      className={`relative rounded-lg border bg-white px-2 py-1.5 shadow-sm dark:bg-neutral-900 ${
        selected ? "border-blue-500 ring-2 ring-blue-500/40" : "border-neutral-200 dark:border-neutral-700"
      } ${dormant ? "opacity-50 grayscale" : ""}`}
    >
      {/* Invisible handles so message edges can attach to any resident. */}
      <Handle type="target" position={Position.Left} className="!h-1 !w-1 !border-0 !bg-transparent" />
      <Handle type="source" position={Position.Right} className="!h-1 !w-1 !border-0 !bg-transparent" />

      {bubble && (
        <div
          className={`absolute -top-2 left-2 right-2 -translate-y-full truncate rounded-md px-2 py-1 text-[10px] shadow ${
            bubble.private
              ? "border border-dashed border-fuchsia-300 bg-fuchsia-50 text-fuchsia-800 dark:border-fuchsia-700 dark:bg-fuchsia-950 dark:text-fuchsia-200"
              : "bg-neutral-800 text-white dark:bg-neutral-100 dark:text-neutral-900"
          }`}
          title={bubble.text}
        >
          {bubble.text}
        </div>
      )}

      <div className="flex items-center gap-2">
        <span className={`relative flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-current/30 ${accentFor(resident.agent_id)}`}>
          <BotIcon className="h-5 w-5" />
          {busy && <span className="absolute -right-1 -top-1 h-2.5 w-2.5 animate-pulse rounded-full bg-indigo-500 ring-2 ring-white dark:ring-neutral-900" />}
        </span>
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold">{resident.name}</div>
          <div className="truncate text-[11px] text-neutral-500">{resident.role}</div>
        </div>
      </div>

      <div className="mt-1.5 flex items-center gap-1.5">
        <div className="h-1.5 flex-1 overflow-hidden rounded bg-neutral-200 dark:bg-neutral-800">
          <div className={`h-full ${energyColor}`} style={{ width: `${energyPct}%` }} />
        </div>
        <span className="w-12 text-right text-[10px] tabular-nums text-neutral-500">⚡{resident.energy}</span>
      </div>
      <div className="mt-0.5 truncate text-[10px] text-neutral-400">
        {worldEnded ? "world ended" : dormant ? "dormant (needs energy)" : busy ? "thinking…" : resident.agent_status ?? ""}
      </div>
    </div>
  );
}
