import type { AgentStatus } from "./types";

export const STATUS_STYLES: Record<AgentStatus, { bg: string; border: string; text: string; dot: string }> = {
  Created: { bg: "bg-slate-100 dark:bg-slate-800", border: "border-slate-300 dark:border-slate-600", text: "text-slate-700 dark:text-slate-200", dot: "bg-slate-400" },
  Initializing: { bg: "bg-slate-100 dark:bg-slate-800", border: "border-slate-300 dark:border-slate-600", text: "text-slate-700 dark:text-slate-200", dot: "bg-slate-400" },
  Idle: { bg: "bg-sky-50 dark:bg-sky-950", border: "border-sky-300 dark:border-sky-700", text: "text-sky-700 dark:text-sky-300", dot: "bg-sky-400" },
  Thinking: { bg: "bg-indigo-50 dark:bg-indigo-950", border: "border-indigo-300 dark:border-indigo-700", text: "text-indigo-700 dark:text-indigo-300", dot: "bg-indigo-500 animate-pulse" },
  Executing: { bg: "bg-amber-50 dark:bg-amber-950", border: "border-amber-300 dark:border-amber-700", text: "text-amber-700 dark:text-amber-300", dot: "bg-amber-500 animate-pulse" },
  Waiting: { bg: "bg-violet-50 dark:bg-violet-950", border: "border-violet-300 dark:border-violet-700", text: "text-violet-700 dark:text-violet-300", dot: "bg-violet-400" },
  Spawning: { bg: "bg-fuchsia-50 dark:bg-fuchsia-950", border: "border-fuchsia-300 dark:border-fuchsia-700", text: "text-fuchsia-700 dark:text-fuchsia-300", dot: "bg-fuchsia-500 animate-pulse" },
  Completed: { bg: "bg-emerald-50 dark:bg-emerald-950", border: "border-emerald-300 dark:border-emerald-700", text: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Failed: { bg: "bg-rose-50 dark:bg-rose-950", border: "border-rose-300 dark:border-rose-700", text: "text-rose-700 dark:text-rose-300", dot: "bg-rose-500" },
  Terminated: { bg: "bg-neutral-100 dark:bg-neutral-800", border: "border-neutral-300 dark:border-neutral-600", text: "text-neutral-600 dark:text-neutral-300", dot: "bg-neutral-500" },
  TimedOut: { bg: "bg-orange-50 dark:bg-orange-950", border: "border-orange-300 dark:border-orange-700", text: "text-orange-700 dark:text-orange-300", dot: "bg-orange-500" },
};

export function isTerminal(status: AgentStatus): boolean {
  return status === "Completed" || status === "Failed" || status === "Terminated" || status === "TimedOut";
}
