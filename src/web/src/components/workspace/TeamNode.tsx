"use client";

import { Handle, Position, type NodeProps } from "@xyflow/react";
import type { WorkspaceAgentView } from "@/lib/workspaceTypes";
import { BotIcon } from "../BotIcon";
import { accentFor } from "../world/worldUi";
import { pauseInfo } from "./pauseInfo";

// Nodes have a fixed size, which the canvas passes to React Flow as already measured. The canvas
// rebuilds its nodes whenever activity changes, and an unmeasured node stays hidden until the
// browser measures it again, so under steady activity the agents never became visible.
export const TEAM_NODE_WIDTH = 200;
export const TEAM_NODE_HEIGHT = 92;
export const USER_NODE_WIDTH = 84;
export const USER_NODE_HEIGHT = 36;

export type Bubble = { text: string; tone: "message" | "tool" | "user" };

export type TeamAgentNodeData = { agent: WorkspaceAgentView; bubble: Bubble | null };
export type TeamUserNodeData = { bubble: Bubble | null };

const BUBBLE_TONES: Record<Bubble["tone"], string> = {
  message: "bg-zinc-800 text-white dark:bg-zinc-100 dark:text-zinc-900",
  tool: "border border-dashed border-sky-300 bg-sky-50 text-sky-800 dark:border-sky-700 dark:bg-sky-950 dark:text-sky-200",
  user: "bg-brand-500 text-white",
};

function SpeechBubble({ bubble }: { bubble: Bubble | null }) {
  if (!bubble) return null;
  return (
    <div
      className={`absolute -top-2 left-2 right-2 -translate-y-full truncate rounded-md px-2 py-1 text-[10px] shadow ${BUBBLE_TONES[bubble.tone]}`}
      title={bubble.text}
    >
      {bubble.text}
    </div>
  );
}

/** Invisible handles on every side, so edges can attach whichever way the layout puts them. */
function Handles() {
  const hidden = "!h-1 !w-1 !border-0 !bg-transparent";
  return (
    <>
      <Handle type="target" position={Position.Top} className={hidden} />
      <Handle type="source" position={Position.Bottom} className={hidden} />
    </>
  );
}

const STATUS_TEXT: Record<string, string> = {
  Thinking: "thinking…",
  Executing: "working…",
  Waiting: "waiting",
  Completed: "done",
  Failed: "failed",
  TimedOut: "timed out",
  Terminated: "stopped",
};

export function TeamAgentNode({ data, selected }: NodeProps & { data: TeamAgentNodeData }) {
  const { agent, bubble } = data;
  const busy = agent.status === "Thinking" || agent.status === "Executing";
  const finished = ["Completed", "Failed", "TimedOut", "Terminated"].includes(agent.status);
  const failed = agent.status === "Failed" || agent.status === "TimedOut";
  const paused = finished ? null : pauseInfo(agent);

  return (
    <div
      style={{ width: TEAM_NODE_WIDTH, height: TEAM_NODE_HEIGHT }}
      className={`relative overflow-visible rounded-lg border bg-white px-2 py-1.5 shadow-sm dark:bg-zinc-900 ${
        selected ? "border-blue-500 ring-2 ring-brand-500/40"
          : failed ? "border-rose-300 dark:border-rose-800"
          : paused ? "border-amber-400 dark:border-amber-700"
          : "border-zinc-200 dark:border-zinc-700"
      } ${finished && !failed ? "opacity-60" : ""}`}
    >
      <Handles />
      <SpeechBubble bubble={bubble} />

      <div className="flex items-center gap-2">
        <span className={`relative flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-current/30 ${accentFor(agent.agent_id)}`}>
          <BotIcon className="h-5 w-5" />
          {busy && <span className="absolute -right-1 -top-1 h-2.5 w-2.5 animate-pulse rounded-full bg-indigo-500 ring-2 ring-white dark:ring-zinc-900" />}
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1">
            <span className="truncate text-sm font-semibold">{agent.role}</span>
            {agent.standing && agent.role !== "Coordinator" && (
              <span className="shrink-0 rounded bg-indigo-100 px-1 text-[9px] uppercase text-indigo-700 dark:bg-indigo-950 dark:text-indigo-300">standing</span>
            )}
          </div>
          <div className={`truncate text-[11px] ${failed ? "text-rose-600" : paused ? "font-medium text-amber-600 dark:text-amber-400" : "text-zinc-500"}`}>
            {paused?.label ?? STATUS_TEXT[agent.status] ?? agent.status.toLowerCase()}
          </div>
        </div>
      </div>

      <div className={`mt-1 truncate text-[10px] ${paused ? "text-amber-700 dark:text-amber-300" : "text-zinc-500"}`}
        title={paused?.detail ?? agent.current_task ?? agent.goal}>
        {paused?.short ?? agent.current_task ?? agent.goal}
      </div>
      <div className="text-[10px] tabular-nums text-zinc-400">{agent.tokens_used.toLocaleString()} tokens</div>
    </div>
  );
}

export function TeamUserNode({ data }: NodeProps & { data: TeamUserNodeData }) {
  return (
    <div style={{ width: USER_NODE_WIDTH, height: USER_NODE_HEIGHT }} className="relative flex items-center gap-2 rounded-full border border-blue-300 bg-blue-50 px-3 py-1.5 shadow-sm dark:border-blue-800 dark:bg-blue-950">
      <Handles />
      <SpeechBubble bubble={data.bubble} />
      <span className="flex h-6 w-6 items-center justify-center rounded-full bg-brand-500 text-white">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" className="h-4 w-4" aria-hidden="true">
          <circle cx="12" cy="8" r="4" />
          <path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1" />
        </svg>
      </span>
      <span className="text-xs font-medium text-blue-900 dark:text-blue-100">You</span>
    </div>
  );
}
