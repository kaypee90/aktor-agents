"use client";

import type { NodeProps } from "@xyflow/react";

export type LocationNodeData = { name: string; description: string; count: number };

export function LocationNode({ data }: NodeProps & { data: LocationNodeData }) {
  return (
    <div className="h-full w-full rounded-xl border-2 border-dashed border-zinc-300 bg-zinc-50/70 dark:border-zinc-700 dark:bg-zinc-900/40">
      <div className="flex items-baseline justify-between gap-2 px-3 pt-2">
        <span className="text-sm font-semibold text-zinc-700 dark:text-zinc-200">{data.name}</span>
        <span className="text-[10px] text-zinc-400">{data.count} here</span>
      </div>
      <div className="truncate px-3 text-[10px] text-zinc-500" title={data.description}>{data.description}</div>
    </div>
  );
}
