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
      <div className="h-1.5 w-full overflow-hidden rounded bg-zinc-200 dark:bg-zinc-700">
        <div className={`h-full ${tone}`} style={{ width: `${Math.min(100, Math.round(share * 100))}%` }} />
      </div>
      <div className="mt-0.5 text-[10px] text-zinc-500 dark:text-zinc-400">
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
      className={`w-52 rounded-xl border bg-white px-3 py-2.5 shadow-sm transition dark:bg-zinc-900 ${
        selected ? "border-brand-500 ring-2 ring-brand-500/30" : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-800 dark:hover:border-zinc-700"
      }`}
    >
      <Handle type="target" position={Position.Top} className="!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600" />
      <div className="flex items-center gap-2.5">
        <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300">
          <BotIcon className="h-4.5 w-4.5" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold text-zinc-900 dark:text-zinc-100">{agent.role}</div>
          <div className="truncate font-mono text-[10px] text-zinc-400">{agent.agent_id}</div>
        </div>
      </div>
      <div className={`mt-2 inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[10px] font-medium ${style.text} ${style.bg} ${style.border}`}>
        <span className={`h-1.5 w-1.5 rounded-full ${style.dot}`} />
        {agent.status}
      </div>
      {spend && <SpendBar spend={spend} />}
      <Handle type="source" position={Position.Bottom} className="!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600" />
    </div>
  );
}
