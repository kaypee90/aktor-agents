import type { WorkspaceAgentView } from "@/lib/workspaceTypes";

/** `short` fits on a canvas node; `detail` is the full explanation. The resume time leads both,
 * since it's what gets cut off first when space runs out. */
export type PauseInfo = { label: string; short: string; detail: string };

/** A time as the user's local clock shows it, e.g. "02:00" (or "Tue 02:00" if not today). */
export function localTime(iso: string): string {
  const at = new Date(iso);
  const sameDay = at.toDateString() === new Date().toDateString();
  return at.toLocaleString(undefined, sameDay ? { hour: "2-digit", minute: "2-digit" } : { weekday: "short", hour: "2-digit", minute: "2-digit" });
}

/** Whether the runtime is holding this agent back (a budget or plan limit), for display. A pause
 * whose time has passed has lifted: the agent just hasn't needed to run since. */
export function pauseInfo(agent: WorkspaceAgentView, now = Date.now()): PauseInfo | null {
  if (!agent.pause_reason) return null;
  if (agent.paused_until && Date.parse(agent.paused_until) <= now) return null;
  const budget = /budget/i.test(agent.pause_reason);
  const resumes = agent.paused_until ? `Resumes ${localTime(agent.paused_until)}` : null;
  return {
    label: budget ? "paused · budget" : "paused",
    short: resumes ?? agent.pause_reason,
    detail: resumes ? `${resumes}: ${agent.pause_reason}` : agent.pause_reason,
  };
}
