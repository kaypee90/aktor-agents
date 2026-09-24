"use client";

import { Handle, Position, type NodeProps } from "@xyflow/react";
import { STATUS_STYLES } from "@/lib/status";
import type { AgentListItem } from "@/lib/types";
import { BotIcon } from "./BotIcon";

export type AgentNodeData = { agent: AgentListItem };

export function AgentNode({ data, selected }: NodeProps & { data: AgentNodeData }) {
  const { agent } = data;
  const style = STATUS_STYLES[agent.status];

  return (
    <div
      className={`w-48 rounded-lg border-2 px-3 py-2 shadow-sm transition-shadow ${style.bg} ${style.border} ${
        selected ? "ring-2 ring-offset-1 ring-blue-500" : ""
      }`}
    >
      <Handle type="target" position={Position.Top} className="!bg-neutral-400" />
      <div className="flex items-center gap-2">
        {/* Status dot sits on the icon's corner so the header stays one line. */}
        <span className={`relative flex h-8 w-8 shrink-0 items-center justify-center rounded-md border ${style.border} ${style.text}`}>
          <BotIcon className="h-5 w-5" />
          <span className={`absolute -right-1 -top-1 h-2.5 w-2.5 rounded-full ring-2 ring-white dark:ring-neutral-900 ${style.dot}`} />
        </span>
        <div className="min-w-0">
          <div className={`truncate text-sm font-semibold ${style.text}`}>{agent.role}</div>
          <div className="truncate text-[11px] text-neutral-500 dark:text-neutral-400">{agent.agent_id}</div>
        </div>
      </div>
      <div className={`mt-1 inline-block rounded px-1.5 py-0.5 text-[10px] font-medium ${style.text} ${style.bg} border ${style.border}`}>
        {agent.status}
      </div>
      <Handle type="source" position={Position.Bottom} className="!bg-neutral-400" />
    </div>
  );
}
