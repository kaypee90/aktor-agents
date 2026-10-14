"use client";

import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import type { WorkspaceVisibility } from "@/lib/workspaceTypes";

const CHOICES: { value: WorkspaceVisibility; label: string; hint: string; icon: typeof Icons.Users }[] = [
  { value: "Organization", label: "Everyone in my organization", hint: "Colleagues can see it, its runs and files, and work on it.", icon: Icons.Users },
  { value: "Private", label: "Only me", hint: "Hidden from everyone else, admins included: its runs, files and spend too.", icon: Icons.Lock },
];

/** Who sees a workspace: everyone in the organization, or only its owner. */
export function VisibilityChoice({ value, onChange, disabled }: { value: WorkspaceVisibility; onChange: (v: WorkspaceVisibility) => void; disabled?: boolean }) {
  return (
    <div role="radiogroup" aria-label="Who can see it" className="grid gap-2 sm:grid-cols-2">
      {CHOICES.map((c) => (
        <button key={c.value} type="button" role="radio" aria-checked={value === c.value} disabled={disabled} onClick={() => onChange(c.value)}
          className={cx("flex items-start gap-2.5 rounded-lg border p-2.5 text-left transition disabled:opacity-60",
            value === c.value ? "border-brand-400 bg-brand-50 dark:border-brand-600 dark:bg-brand-500/10" : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-700")}>
          <c.icon className={cx("mt-0.5 h-4 w-4 shrink-0", value === c.value ? "text-brand-600 dark:text-brand-400" : "text-zinc-400")} />
          <span>
            <span className="block text-xs font-medium">{c.label}</span>
            <span className="block text-[11px] leading-snug text-zinc-500">{c.hint}</span>
          </span>
        </button>
      ))}
    </div>
  );
}
