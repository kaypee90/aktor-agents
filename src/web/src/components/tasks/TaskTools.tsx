"use client";

import { useCallback, useEffect, useState } from "react";
import {
  addTaskConnection,
  listTaskConnections,
  refreshTaskConnection,
  removeTaskConnection,
  updateTaskConnection,
} from "@/lib/api";
import { ConnectionsPanel, type ConnectionsApi } from "@/components/workspace/IntegrationsPanel";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

function taskConnections(taskId: string): ConnectionsApi {
  return {
    key: taskId,
    list: () => listTaskConnections(taskId),
    add: (body) => addTaskConnection(taskId, body),
    update: (id, body) => updateTaskConnection(taskId, id, { enabled_tools: body.enabled_tools, settings: body.settings }),
    refresh: (id) => refreshTaskConnection(taskId, id),
    remove: (id) => removeTaskConnection(taskId, id),
  };
}

/**
 * The task's tools: connect MCP servers (or HTTP APIs) for this task's agents. They can use the
 * enabled tools from their next step, including while the task is running.
 */
export function TaskToolsButton({ taskId }: { taskId: string }) {
  const [open, setOpen] = useState(false);
  const [count, setCount] = useState<number | null>(null);

  const recount = useCallback(() => {
    listTaskConnections(taskId).then((c) => setCount(c.length)).catch(() => setCount(null));
  }, [taskId]);
  useEffect(() => { recount(); }, [recount]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  return (
    <>
      <button onClick={() => setOpen(true)} title="Connect MCP servers and APIs for this task's agents"
        className={cx("inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1 text-xs font-medium transition",
          count ? "border-brand-300 bg-brand-50 text-brand-700 dark:border-brand-800 dark:bg-brand-950 dark:text-brand-300"
            : "border-zinc-200 text-zinc-600 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-800")}>
        <Icons.Connect className="h-3.5 w-3.5" />
        Tools{count ? ` · ${count}` : ""}
      </button>

      {open && (
        <div className="fixed inset-0 z-50 flex justify-end bg-black/30" onMouseDown={() => setOpen(false)}>
          <aside onMouseDown={(e) => e.stopPropagation()} role="dialog" aria-modal
            className="flex h-full w-full max-w-md flex-col border-l border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-950">
            <header className="flex items-start gap-3 border-b border-zinc-200 px-4 py-3 dark:border-zinc-800">
              <div className="min-w-0 flex-1">
                <h2 className="text-sm font-semibold text-zinc-900 dark:text-zinc-50">Tools for this task</h2>
                <p className="mt-0.5 text-xs text-zinc-500">
                  Connect MCP servers or APIs. Every agent of this task can use the enabled tools from its next step. Secrets are
                  encrypted and never shown to agents.
                </p>
              </div>
              <button onClick={() => setOpen(false)} className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" aria-label="Close">
                <Icons.X />
              </button>
            </header>
            <div className="min-h-0 flex-1 overflow-y-auto">
              <ConnectionsPanel api={taskConnections(taskId)} onChanged={recount} toolsOnly
                emptyText="No tools connected. Add an MCP server (its URL, and a token if it needs one) or a REST API, and this task's agents can use its tools." />
            </div>
          </aside>
        </div>
      )}
    </>
  );
}
