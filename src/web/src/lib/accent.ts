/** Stable per-agent accent colour, so an agent is recognisable wherever it appears. */
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
  if (!agentId) return "text-zinc-500";
  let h = 0;
  for (let i = 0; i < agentId.length; i++) h = (h * 31 + agentId.charCodeAt(i)) >>> 0;
  return ACCENTS[h % ACCENTS.length];
}
