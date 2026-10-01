"use client";

import { Handle, Position, type NodeProps } from "@xyflow/react";
import { STATUS_STYLES } from "@/lib/status";
import type { AgentListItem, AgentSpend } from "@/lib/types";
import { BotIcon } from "./BotIcon";

export type AgentNodeData = { agent: AgentListItem; spend?: AgentSpend };

/** The branch's spend (this agent and everything below it) against this agent's own budget, which
 * every child's budget was carved from: the runtime keeps the branch inside it. */
function SpendBar({ spend }: { spend: AgentSpend }) {
  const share = spend.budget_max_cost_usd > 0 ? spend.branch_cost_usd / spend.budget_max_cost_usd : 0;
  const tone = share >= 0.9 ? "bg-rose-500" : share >= 0.75 ? "bg-amber-500" : "bg-emerald-500";
  return (
    <div className="mt-1.5" title={`This branch: $${spend.branch_cost_usd.toFixed(4)} / ${spend.branch_tokens.toLocaleString()} tokens. This agent alone: $${spend.cost_usd.toFixed(4)}.`}>
      <div className="h-1.5 w-full overflow-hidden rounded bg-neutral-200 dark:bg-neutral-700">
        <div className={`h-full ${tone}`} style={{ width: `${Math.min(100, Math.round(share * 100))}%` }} />
      </div>
      <div className="mt-0.5 text-[10px] text-neutral-500 dark:text-neutral-400">
        ${spend.branch_cost_usd.toFixed(3)} of ${spend.budget_max_cost_usd.toFixed(2)}
      </div>
    </div>
  );
}

export function AgentNode({ data, selected }: NodeProps & { data: AgentNodeData }) {
  const { agent, spend } = data;
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
      {spend && <SpendBar spend={spend} />}
      <Handle type="source" position={Position.Bottom} className="!bg-neutral-400" />
    </div>
  );
}
