"use client";

import { useState } from "react";
import { createTask } from "@/lib/api";

export function GoalForm({ onTaskCreated }: { onTaskCreated: (taskId: string, rootAgentId: string) => void }) {
  const [goal, setGoal] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!goal.trim() || submitting) return;

    setSubmitting(true);
    setError(null);
    try {
      const { task_id, root_agent_id } = await createTask(goal.trim());
      onTaskCreated(task_id, root_agent_id);
      setGoal("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to submit goal");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex gap-2">
      <input
        value={goal}
        onChange={(e) => setGoal(e.target.value)}
        placeholder="e.g. Research the feasibility of an AI-powered property management SaaS."
        className="flex-1 rounded border border-neutral-300 bg-white px-3 py-2 text-sm outline-none focus:border-blue-500 dark:border-neutral-700 dark:bg-neutral-900"
        disabled={submitting}
      />
      <button
        type="submit"
        disabled={submitting || !goal.trim()}
        className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
      >
        {submitting ? "Starting…" : "Start"}
      </button>
      {error && <span className="self-center text-xs text-rose-600">{error}</span>}
    </form>
  );
}
