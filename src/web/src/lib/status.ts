import type { AgentStatus } from "./types";

export const STATUS_STYLES: Record<AgentStatus, { bg: string; border: string; text: string; dot: string }> = {
  Created: { bg: "bg-zinc-100 dark:bg-zinc-800", border: "border-zinc-300 dark:border-zinc-600", text: "text-zinc-700 dark:text-zinc-200", dot: "bg-zinc-400" },
  Initializing: { bg: "bg-zinc-100 dark:bg-zinc-800", border: "border-zinc-300 dark:border-zinc-600", text: "text-zinc-700 dark:text-zinc-200", dot: "bg-zinc-400" },
  Idle: { bg: "bg-sky-50 dark:bg-sky-500/10", border: "border-sky-300 dark:border-sky-500/30", text: "text-sky-700 dark:text-sky-300", dot: "bg-sky-400" },
  Thinking: { bg: "bg-indigo-50 dark:bg-indigo-500/10", border: "border-indigo-300 dark:border-indigo-500/30", text: "text-indigo-700 dark:text-indigo-300", dot: "bg-indigo-500 animate-pulse" },
  Executing: { bg: "bg-amber-50 dark:bg-amber-500/10", border: "border-amber-300 dark:border-amber-500/30", text: "text-amber-700 dark:text-amber-300", dot: "bg-amber-500 animate-pulse" },
  Waiting: { bg: "bg-violet-50 dark:bg-violet-500/10", border: "border-violet-300 dark:border-violet-500/30", text: "text-violet-700 dark:text-violet-300", dot: "bg-violet-400" },
  Spawning: { bg: "bg-fuchsia-50 dark:bg-fuchsia-500/10", border: "border-fuchsia-300 dark:border-fuchsia-500/30", text: "text-fuchsia-700 dark:text-fuchsia-300", dot: "bg-fuchsia-500 animate-pulse" },
  Completed: { bg: "bg-emerald-50 dark:bg-emerald-500/10", border: "border-emerald-300 dark:border-emerald-500/30", text: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Failed: { bg: "bg-rose-50 dark:bg-rose-500/10", border: "border-rose-300 dark:border-rose-500/30", text: "text-rose-700 dark:text-rose-300", dot: "bg-rose-500" },
  Terminated: { bg: "bg-zinc-100 dark:bg-zinc-800", border: "border-zinc-300 dark:border-zinc-600", text: "text-zinc-600 dark:text-zinc-300", dot: "bg-zinc-500" },
  TimedOut: { bg: "bg-orange-50 dark:bg-orange-500/10", border: "border-orange-300 dark:border-orange-500/30", text: "text-orange-700 dark:text-orange-300", dot: "bg-orange-500" },
};

/** Busy on a request right now: reasoning, running a tool or spawning a helper. */
export function isWorking(status: AgentStatus): boolean {
  return status === "Thinking" || status === "Executing" || status === "Spawning";
}

export function isTerminal(status: AgentStatus): boolean {
  return status === "Completed" || status === "Failed" || status === "Terminated" || status === "TimedOut";
}
