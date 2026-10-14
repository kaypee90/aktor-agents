/** The colors a stage can have on the canvas (StageColors on the server). Class names are spelled out
 * so Tailwind keeps them. */
export const STAGE_COLORS = {
  slate: { label: "Grey", swatch: "bg-slate-400", bar: "bg-slate-400", tint: "bg-slate-50 dark:bg-slate-500/10" },
  red: { label: "Red", swatch: "bg-red-500", bar: "bg-red-500", tint: "bg-red-50 dark:bg-red-500/10" },
  orange: { label: "Orange", swatch: "bg-orange-500", bar: "bg-orange-500", tint: "bg-orange-50 dark:bg-orange-500/10" },
  amber: { label: "Yellow", swatch: "bg-amber-400", bar: "bg-amber-400", tint: "bg-amber-50 dark:bg-amber-500/10" },
  green: { label: "Green", swatch: "bg-green-500", bar: "bg-green-500", tint: "bg-green-50 dark:bg-green-500/10" },
  teal: { label: "Teal", swatch: "bg-teal-500", bar: "bg-teal-500", tint: "bg-teal-50 dark:bg-teal-500/10" },
  sky: { label: "Sky", swatch: "bg-sky-500", bar: "bg-sky-500", tint: "bg-sky-50 dark:bg-sky-500/10" },
  blue: { label: "Blue", swatch: "bg-blue-500", bar: "bg-blue-500", tint: "bg-blue-50 dark:bg-blue-500/10" },
  indigo: { label: "Indigo", swatch: "bg-indigo-500", bar: "bg-indigo-500", tint: "bg-indigo-50 dark:bg-indigo-500/10" },
  violet: { label: "Purple", swatch: "bg-violet-500", bar: "bg-violet-500", tint: "bg-violet-50 dark:bg-violet-500/10" },
  pink: { label: "Pink", swatch: "bg-pink-500", bar: "bg-pink-500", tint: "bg-pink-50 dark:bg-pink-500/10" },
} as const;

export type StageColor = keyof typeof STAGE_COLORS;

export const colorOf = (color: string | null | undefined) => (color && color in STAGE_COLORS ? STAGE_COLORS[color as StageColor] : null);
