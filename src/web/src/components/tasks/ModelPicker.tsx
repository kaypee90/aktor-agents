"use client";

import { modelChoices, type LlmSettingsView } from "@/lib/api";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { formatTokenPrice } from "@/lib/tokenPricing";

const price = (n: number) => (n === 0 ? "free" : formatTokenPrice(n));

/**
 * Picks the model a task runs on (docs/llm-settings.md): the organization's default, the server's,
 * or one of its models, with each one's price per million tokens so cheap and strong ones are easy
 * to tell apart. An empty value means "the default".
 */
export function ModelPicker({ view, value, onChange, allowDefault = true, defaultLabel, disabled, className, title }: {
  view: LlmSettingsView;
  value: string;
  onChange: (id: string) => void;
  /** Offer "Default" as its own choice (new tasks); a running task always names a model. */
  allowDefault?: boolean;
  /** What the empty choice means here, when not the organization's default. */
  defaultLabel?: string;
  disabled?: boolean;
  className?: string;
  title?: string;
}) {
  const choices = modelChoices(view);
  const label = (c: (typeof choices)[number]) =>
    `${c.name}${c.provider === "Mock" ? " (demo)" : c.name === c.model ? "" : ` · ${c.model}`} — ${price(c.in_per_million)} in / ${price(c.out_per_million)} out · USD per 1M tokens`;
  const defaultChoice = choices.find((c) => c.is_default) ?? choices[0];

  return (
    <label title={title ?? "The model this task's agents use"}
      className={cx("relative inline-flex min-w-0 items-center gap-1.5 rounded-lg border border-zinc-200 bg-white py-1 pl-2 pr-1 text-xs text-zinc-700 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-300",
        disabled && "opacity-60", className)}>
      <Icons.Bolt className="h-3.5 w-3.5 shrink-0 text-brand-500" />
      <select className="min-w-0 max-w-64 cursor-pointer truncate bg-transparent pr-1 outline-none" value={value} disabled={disabled}
        onChange={(e) => onChange(e.target.value)}>
        {allowDefault && <option value="">{defaultLabel ?? `Default: ${defaultChoice.name}`}</option>}
        {choices.map((c) => <option key={c.id} value={c.id}>{label(c)}</option>)}
      </select>
    </label>
  );
}
