"use client";

import Link from "next/link";
import { useState } from "react";
import { Button, ErrorBanner, StatusBadge, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { MentionTextarea, stageMentionables } from "@/components/ui/MentionTextarea";
import { apiErrorMessage, controlPipelineRun, startPipelineRun } from "@/lib/api";
import { RUN_ACTIVE, type PipelineRunView, type WorkspaceRunSummary } from "@/lib/pipelineTypes";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";

const SOURCE_LABEL: Record<string, string> = { manual: "You", chat: "Chat", schedule: "Schedule", webhook: "Webhook", watch: "Watch" };

function duration(run: WorkspaceRunSummary) {
  if (!run.completed_at) return null;
  const s = Math.max(0, (new Date(run.completed_at).getTime() - new Date(run.created_at).getTime()) / 1000);
  return s < 60 ? `${Math.round(s)}s` : s < 3600 ? `${Math.round(s / 60)}m` : `${(s / 3600).toFixed(1)}h`;
}

/**
 * Running the pipeline: an input box to start a run, and the workspace's runs, newest first.
 * Selecting a run shows it on the canvas; runs in progress can be paused or cancelled.
 */
export function RunsPanel({ workspace, selectedRun, onSelectRun, onChanged }: {
  workspace: WorkspaceSnapshot;
  selectedRun: PipelineRunView | null;
  onSelectRun: (runId: string | null) => void;
  onChanged: () => void;
}) {
  const [input, setInput] = useState("");
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const archived = workspace.status === "Archived";
  const active = workspace.runs.filter((r) => RUN_ACTIVE.includes(r.status)).length;

  async function run(e: React.FormEvent) {
    e.preventDefault();
    if (!input.trim()) return;
    setStarting(true);
    setError(null);
    try {
      const result = await startPipelineRun(workspace.workspace_id, input.trim());
      setInput("");
      onChanged();
      if (result.run_id) onSelectRun(result.run_id);
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setStarting(false);
    }
  }

  async function control(runId: string, action: "pause" | "resume" | "cancel") {
    if (action === "cancel" && !confirm("Cancel this run? Its agents stop and unfinished stages are skipped.")) return;
    try {
      await controlPipelineRun(workspace.workspace_id, runId, action);
      onChanged();
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  return (
    <div className="flex h-full flex-col">
      <form onSubmit={run} className="flex items-start gap-2 border-b border-zinc-200 p-3 dark:border-zinc-800">
        <MentionTextarea
          value={input}
          onValueChange={setInput}
          mentionables={stageMentionables(workspace.pipeline?.stages ?? [])}
          wrapperClassName="flex-1"
          onKeyDown={(e) => { if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) run(e); }}
          rows={2}
          disabled={archived}
          placeholder={archived ? "This workspace is archived." : "What should the pipeline do this time? (⌘/Ctrl+Enter to run, @ to mention a stage)"}
          className={cx(inputClass, "block min-h-[2.75rem] resize-y text-xs")}
        />
        <Button type="submit" variant="primary" icon={<Icons.Play className="h-3.5 w-3.5" />} disabled={!input.trim() || starting || archived}>
          {starting ? "Starting…" : "Run"}
        </Button>
      </form>
      {error && <div className="px-3 pt-2"><ErrorBanner error={error} onClose={() => setError(null)} /></div>}

      <div className="flex items-center justify-between px-3 pb-1 pt-2 text-[11px] text-zinc-500">
        <span>Runs{active > 0 && ` · ${active} in progress`}{workspace.queued_runs > 0 && ` · ${workspace.queued_runs} queued`}</span>
        {selectedRun && <button onClick={() => onSelectRun(null)} className="hover:text-zinc-800 hover:underline dark:hover:text-zinc-200">Show the pipeline</button>}
      </div>
      <ul className="min-h-0 flex-1 divide-y divide-zinc-100 overflow-y-auto dark:divide-zinc-800/70">
        {workspace.runs.length === 0 && (
          <li className="p-4 text-xs text-zinc-500">
            No runs yet. Type an input above and press Run, or add a trigger (schedule, webhook, watch) to run it automatically.
          </li>
        )}
        {workspace.runs.map((r) => {
          const isSelected = selectedRun?.run_id === r.run_id;
          const running = RUN_ACTIVE.includes(r.status);
          return (
            <li key={r.run_id} className={cx("group", isSelected && "bg-brand-50/60 dark:bg-brand-500/5")}>
              <div className="flex items-start gap-3 px-3 py-2">
                <button onClick={() => onSelectRun(isSelected ? null : r.run_id)} className="min-w-0 flex-1 text-left">
                  <span className="flex items-center gap-2">
                    <span className="font-mono text-[11px] text-zinc-400">#{r.number}</span>
                    <StatusBadge status={isSelected && selectedRun?.paused ? "Paused" : r.status} />
                    <span className="truncate text-[11px] text-zinc-500">
                      {r.trigger_name ? `${SOURCE_LABEL[r.source] ?? r.source}: ${r.trigger_name}` : SOURCE_LABEL[r.source] ?? r.source}
                      {" · "}{ago(r.created_at)}{duration(r) && ` · ${duration(r)}`}{` · v${r.pipeline_version}`}
                    </span>
                  </span>
                  <span className="mt-0.5 line-clamp-1 text-xs text-zinc-800 dark:text-zinc-200" title={r.input}>{r.input}</span>
                  {r.summary && !running && <span className="mt-0.5 line-clamp-2 text-[11px] text-zinc-500" title={r.summary}>{r.summary}</span>}
                </button>
                <span className="flex shrink-0 items-center gap-1 opacity-70 group-hover:opacity-100">
                  {running && isSelected && (
                    selectedRun?.paused
                      ? <Button size="sm" variant="ghost" onClick={() => control(r.run_id, "resume")} icon={<Icons.Play className="h-3 w-3" />}>Resume</Button>
                      : <Button size="sm" variant="ghost" onClick={() => control(r.run_id, "pause")} icon={<Icons.Pause className="h-3 w-3" />}>Pause</Button>
                  )}
                  {running && <Button size="sm" variant="ghost" onClick={() => control(r.run_id, "cancel")} icon={<Icons.Stop className="h-3 w-3" />}>Cancel</Button>}
                  <Link href={`/?task=${r.run_id}`} title="Open the run's agent graph, files and result" className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800">
                    <Icons.External className="h-3.5 w-3.5" />
                  </Link>
                </span>
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
