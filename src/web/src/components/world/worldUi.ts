import type { ActivityKind, Resident, WorldSnapshot } from "@/lib/worldTypes";

/** Stable per-resident accent colour, so a resident is recognisable across map, feed and panel. */
const ACCENTS = [
  "text-sky-600 dark:text-sky-400",
  "text-rose-600 dark:text-rose-400",
  "text-emerald-600 dark:text-emerald-400",
  "text-amber-600 dark:text-amber-400",
  "text-violet-600 dark:text-violet-400",
  "text-teal-600 dark:text-teal-400",
  "text-fuchsia-600 dark:text-fuchsia-400",
  "text-orange-600 dark:text-orange-400",
  "text-indigo-600 dark:text-indigo-400",
  "text-lime-700 dark:text-lime-400",
];

export function accentFor(agentId: string | null | undefined): string {
  if (!agentId) return "text-neutral-500";
  let h = 0;
  for (let i = 0; i < agentId.length; i++) h = (h * 31 + agentId.charCodeAt(i)) >>> 0;
  return ACCENTS[h % ACCENTS.length];
}

export const KIND_LABEL: Record<ActivityKind, string> = {
  said: "said",
  talked: "private",
  moved: "moved",
  posted: "board",
  gave: "gift",
  proposed: "vote",
  voted: "vote",
  removed: "removed",
  rejected: "vote",
  dormant: "dormant",
  revived: "revived",
  joined: "joined",
  left: "left",
  plan: "plan",
  note: "note",
  world: "world",
};

export const KIND_ICON: Record<ActivityKind, string> = {
  said: "💬",
  talked: "✉️",
  moved: "🚶",
  posted: "📌",
  gave: "⚡",
  proposed: "🗳️",
  voted: "🗳️",
  removed: "⛔",
  rejected: "🗳️",
  dormant: "💤",
  revived: "✨",
  joined: "🐣",
  left: "👋",
  plan: "🧭",
  note: "📝",
  world: "🌍",
};

export function residentMap(world: WorldSnapshot | null): Map<string, Resident> {
  return new Map((world?.residents ?? []).map((r) => [r.agent_id, r]));
}

export function isLiving(r: Resident): boolean {
  return r.state === "Active" || r.state === "Dormant";
}
