"use client";

import { cancelTask, pauseTask, resumeTask } from "@/lib/api";
import type { TaskSummary } from "@/lib/types";

export function TaskStatusBar({ task }: { task: TaskSummary }) {
  const running = !["Completed", "Failed", "Terminated", "TimedOut"].includes(task.status);

  return (
    <div className="flex items-center justify-between gap-3 rounded border border-neutral-200 bg-neutral-50 px-3 py-2 text-sm dark:border-neutral-800 dark:bg-neutral-900">
      <div className="min-w-0">
        <span className="font-medium">{task.goal}</span>
        <span className="ml-2 text-xs text-neutral-500">
          [{task.task_id.slice(0, 8)}] · {task.status}
        </span>
      </div>
      {running && (
        <div className="flex shrink-0 gap-2 text-xs">
          <button onClick={() => pauseTask(task.task_id)} className="rounded border border-neutral-300 px-2 py-1 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800">
            Pause
          </button>
          <button onClick={() => resumeTask(task.task_id)} className="rounded border border-neutral-300 px-2 py-1 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800">
            Resume
          </button>
          <button onClick={() => cancelTask(task.task_id)} className="rounded border border-rose-300 px-2 py-1 text-rose-600 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-400 dark:hover:bg-rose-950">
            Cancel
          </button>
        </div>
      )}
    </div>
  );
}
