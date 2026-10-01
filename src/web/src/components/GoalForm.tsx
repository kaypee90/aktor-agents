"use client";

import { useState } from "react";
import { createTask, previewTask } from "@/lib/api";
import type { TaskPreview } from "@/lib/types";
import { TaskPreviewCard } from "./TaskPreviewCard";

/**
 * Every new task is previewed first (roadmap P2): one cheap planning call shows the team the root
 * is likely to build and what it should cost. Below the server's confirm threshold the task starts
 * straight away; above it, nothing runs until the user accepts the estimate.
 */
export function GoalForm({
  onTaskCreated,
}: {
  onTaskCreated: (taskId: string, rootAgentId: string, preview: TaskPreview | null) => void;
}) {
  const [goal, setGoal] = useState("");
  const [maxCost, setMaxCost] = useState("");
  const [busy, setBusy] = useState<"previewing" | "starting" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<TaskPreview | null>(null);

  const budget = () => (maxCost.trim() ? { max_cost_usd: Number(maxCost) } : undefined);

  async function start(preview: TaskPreview | null) {
    setBusy("starting");
    try {
      const { task_id, root_agent_id } = await createTask(preview?.goal ?? goal.trim(), budget(), preview?.preview_id);
      onTaskCreated(task_id, root_agent_id, preview);
      setGoal("");
      setPending(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start the task");
    } finally {
      setBusy(null);
    }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!goal.trim() || busy) return;

    setBusy("previewing");
    setError(null);
    setPending(null);
    let preview: TaskPreview | null = null;
    try {
      preview = await previewTask(goal.trim(), budget());
    } catch (err) {
      // A preview that fails must not block work: say so and start without one.
      setError(`Preview unavailable (${err instanceof Error ? err.message : "error"}); starting without an estimate.`);
    }

    if (preview?.requires_confirmation) {
      setPending(preview);
      setBusy(null);
      return;
    }

    await start(preview);
  }

  return (
    <div className="space-y-2">
      <form onSubmit={handleSubmit} className="flex gap-2">
        <input
          value={goal}
          onChange={(e) => setGoal(e.target.value)}
          placeholder="e.g. Research the feasibility of an AI-powered property management SaaS."
          className="flex-1 rounded border border-neutral-300 bg-white px-3 py-2 text-sm outline-none focus:border-blue-500 dark:border-neutral-700 dark:bg-neutral-900"
          disabled={busy !== null || pending !== null}
        />
        <input
          value={maxCost}
          onChange={(e) => setMaxCost(e.target.value.replace(/[^0-9.]/g, ""))}
          placeholder="Max $"
          title="Most the whole team may spend (optional; the server's default otherwise)"
          className="w-20 rounded border border-neutral-300 bg-white px-2 py-2 text-sm outline-none focus:border-blue-500 dark:border-neutral-700 dark:bg-neutral-900"
          disabled={busy !== null || pending !== null}
        />
        <button
          type="submit"
          disabled={busy !== null || pending !== null || !goal.trim()}
          className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {busy === "previewing" ? "Estimating…" : busy === "starting" ? "Starting…" : "Start"}
        </button>
      </form>
      {error && <div className="text-xs text-rose-600">{error}</div>}

      {pending && (
        <div className="rounded border border-amber-300 bg-amber-50 p-3 dark:border-amber-800 dark:bg-amber-950/40">
          <div className="mb-2 text-sm font-medium text-amber-800 dark:text-amber-200">
            This task could cost up to ${pending.estimate.cost_usd_high.toFixed(2)}, above the ${pending.confirm_above_usd.toFixed(2)} confirmation
            threshold. Start it?
          </div>
          <TaskPreviewCard preview={pending} />
          <div className="mt-3 flex gap-2">
            <button
              onClick={() => start(pending)}
              disabled={busy !== null}
              className="rounded bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
            >
              {busy === "starting" ? "Starting…" : `Start (budget $${pending.budget.max_cost_usd.toFixed(2)})`}
            </button>
            <button
              onClick={() => setPending(null)}
              disabled={busy !== null}
              className="rounded border border-neutral-300 px-3 py-1.5 text-sm hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
            >
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
