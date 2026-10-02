"use client";

import { useEffect, useState } from "react";
import { artifactDownloadUrl, artifactsZipUrl, getTaskArtifacts, getTaskResult } from "@/lib/api";
import type { ArtifactListItem, TaskResult } from "@/lib/types";
import { useLiveList } from "@/lib/useLiveList";

const TERMINAL_STATUSES = new Set(["Completed", "Failed", "Terminated", "TimedOut"]);

export function FinalResultPanel({ taskId, taskStatus, artifactWrites = 0 }: {
  taskId: string;
  taskStatus: string;
  /** How many "file written" events the page has seen for this task; each one reloads the list. */
  artifactWrites?: number;
}) {
  const [result, setResult] = useState<TaskResult | null>(null);
  const [open, setOpen] = useState(true);
  const terminal = TERMINAL_STATUSES.has(taskStatus);

  useEffect(() => {
    if (!terminal) return;

    let cancelled = false;

    async function load() {
      try {
        const resultResponse = await getTaskResult(taskId);
        if (cancelled) return;
        if (resultResponse.ready && resultResponse.result) {
          setResult(resultResponse.result);
          clearInterval(interval);
        }
      } catch {
        // API may be briefly unavailable right after task completion; retry on the next tick.
      }
    }

    load();
    // The root's completion event is processed slightly after its own status flips, so poll a
    // few times until the aggregated result is actually ready.
    const interval = setInterval(load, 1500);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [taskId, terminal]);

  // Files are saved by a background writer that can trail the live events and the task's status,
  // so reload when the task finishes, when its result lands, and after every file write.
  const artifactList = useLiveList<ArtifactListItem>(terminal ? taskId : null, artifactWrites + (result ? 1 : 0),
    () => getTaskArtifacts(taskId));
  const artifacts = artifactList ?? [];

  if (!TERMINAL_STATUSES.has(taskStatus)) return null;

  // An agent rewriting a file produces one artifact record per write; count files, not writes.
  const uniqueFileCount = new Set(artifacts.map((a) => a.file_name)).size;

  return (
    <div className="border-b border-zinc-200 dark:border-zinc-800">
      <button
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center justify-between px-3 py-2 text-left text-sm font-medium hover:bg-zinc-50 dark:hover:bg-zinc-900"
      >
        <span>Final Result</span>
        <span className="text-zinc-400">{open ? "▲" : "▼"}</span>
      </button>

      {open && (
        <div className="space-y-3 px-3 pb-3 text-sm">
          {!result ? (
            <div className="text-zinc-500">Aggregating final result…</div>
          ) : (
            <>
              <p className="text-zinc-700 dark:text-zinc-300">{result.summary}</p>

              <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-zinc-500">
                <span>Participating agents: {result.participating_agents}</span>
                <span>Tool calls: {result.metrics.total_tool_calls ?? "—"}</span>
                <span>Tokens used: {result.metrics.total_tokens_used ?? "—"}</span>
                <span>Cost: ${result.metrics.total_cost_usd ?? "0.0000"}</span>
              </div>

              {result.metrics.estimated_cost_usd && (
                <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-zinc-500">
                  <span>
                    Estimated: ${Number(result.metrics.estimated_cost_usd).toFixed(4)} / {result.metrics.estimated_team_size} agents
                  </span>
                  <span>
                    Actual vs estimate: {result.metrics.cost_estimate_ratio ? `${Number(result.metrics.cost_estimate_ratio).toFixed(2)}×` : "—"} cost
                  </span>
                </div>
              )}

              {result.findings.length > 0 && (
                <div>
                  <div className="mb-1 text-xs font-semibold uppercase text-zinc-500">Findings</div>
                  <ul className="list-inside list-disc space-y-1 text-zinc-600 dark:text-zinc-400">
                    {result.findings.map((f, i) => (
                      <li key={i}>{f}</li>
                    ))}
                  </ul>
                </div>
              )}

              {result.unresolved_items.length > 0 && (
                <div>
                  <div className="mb-1 text-xs font-semibold uppercase text-amber-600">Unresolved</div>
                  <ul className="list-inside list-disc space-y-1 text-zinc-600 dark:text-zinc-400">
                    {result.unresolved_items.map((item, i) => (
                      <li key={i}>{item}</li>
                    ))}
                  </ul>
                </div>
              )}
            </>
          )}

          <div>
            <div className="mb-1 flex items-center gap-3">
              <span className="text-xs font-semibold uppercase text-zinc-500">
                Artifacts {uniqueFileCount > 0 && `(${uniqueFileCount})`}
              </span>
              {uniqueFileCount > 1 && (
                <a
                  href={artifactsZipUrl(taskId)}
                  className="rounded border border-zinc-300 px-2 py-0.5 text-xs hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
                >
                  Download all (.zip)
                </a>
              )}
            </div>
            {artifactList === null ? (
              <div className="text-xs text-zinc-500">Loading files…</div>
            ) : artifacts.length === 0 ? (
              <div className="text-xs text-zinc-500">
                No files were written to the workspace for this task (agents only wrote via
                filesystem_write would appear here).
              </div>
            ) : (
              <ul className="space-y-1">
                {artifacts.map((a) => (
                  <li key={a.artifact_id}>
                    <a
                      href={artifactDownloadUrl(taskId, a.artifact_id)}
                      download={a.file_name}
                      className="text-blue-600 hover:underline dark:text-blue-400"
                    >
                      {a.file_name}
                    </a>
                    <span className="ml-2 text-xs text-zinc-400">by {a.created_by_agent}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
