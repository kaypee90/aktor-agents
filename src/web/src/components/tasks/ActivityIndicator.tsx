"use client";

import { cx } from "@/components/ui";

/**
 * What a running task or agent is doing right now: working on a request (a pulsing dot, with how
 * many agents are busy), paused, or waiting for something to do.
 */
export function ActivityIndicator({ paused, busy, label, className }: {
  paused: boolean; busy: number; label?: string; className?: string;
}) {
  const state = paused ? "paused" : busy > 0 ? "working" : "waiting";
  const text = paused ? "Paused"
    : busy > 0 ? (label ?? (busy > 1 ? `Working · ${busy} agents` : "Working"))
    : "Waiting";
  return (
    <span role="status" aria-live="polite"
      title={state === "working" ? "An agent is working on a request" : state === "paused" ? "Paused: press Resume to continue" : "Nothing is being worked on right now"}
      className={cx("inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5 text-[11px] font-medium",
        state === "working" && "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300",
        state === "paused" && "border-amber-200 bg-amber-50 text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300",
        state === "waiting" && "border-zinc-200 bg-zinc-50 text-zinc-500 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-400",
        className)}>
      <span className="relative flex h-2 w-2">
        {state === "working" && <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />}
        <span className={cx("relative inline-flex h-2 w-2 rounded-full",
          state === "working" ? "bg-emerald-500" : state === "paused" ? "bg-amber-500" : "bg-zinc-400")} />
      </span>
      {text}
    </span>
  );
}
