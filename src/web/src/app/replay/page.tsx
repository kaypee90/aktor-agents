"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useMemo, useState } from "react";
import { Button, PageHeader } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { diffTasks, getLlmSettings, getTask, getTaskJournal, replayTask, type LlmSettingsView } from "@/lib/api";
import { ModelPicker } from "@/components/tasks/ModelPicker";
import type { JournalStep, RunDiff, StepDiffStatus, TaskSummary } from "@/lib/types";

const STATUS_TONE: Record<StepDiffStatus, string> = {
  Same: "text-zinc-500",
  Different: "text-amber-700 dark:text-amber-300",
  OnlyInA: "text-rose-700 dark:text-rose-300",
  OnlyInB: "text-emerald-700 dark:text-emerald-300",
};

/**
 * Step-through and diff for a run's journal (roadmap P6). Every LLM decision and tool result of a
 * task, in order, by agent; replay it in full or fork it from any step; compare two runs.
 * Open with /replay?task=<id> (and &compare=<other id>).
 */
export default function ReplayPage() {
  return <Suspense><ReplayFromUrl /></Suspense>;
}

/** Reads the run from the URL. useSearchParams is current on in-app navigation too, where
 * window.location can still show the page we came from. */
function ReplayFromUrl() {
  const params = useSearchParams();
  const taskId = params.get("task");
  return <Replay key={taskId} taskId={taskId} initialCompare={params.get("compare") ?? ""} />;
}

function Replay({ taskId, initialCompare }: { taskId: string | null; initialCompare: string }) {
  const router = useRouter();
  const [task, setTask] = useState<TaskSummary | null>(null);
  const [steps, setSteps] = useState<JournalStep[]>([]);
  const [index, setIndex] = useState(0);
  const [compareId, setCompareId] = useState(initialCompare);
  const [diff, setDiff] = useState<RunDiff | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!taskId) return;
    Promise.all([getTask(taskId), getTaskJournal(taskId)])
      .then(async ([t, j]) => {
        setTask(t);
        setSteps(j);
        setIndex(0);
        // A replay is compared with its original by default.
        if (t.replay_of_task_id) {
          setCompareId((c) => c || t.replay_of_task_id!);
          setDiff(await diffTasks(t.replay_of_task_id, taskId));
        }
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [taskId]);

  const loadDiff = useCallback(async () => {
    if (!taskId || !compareId.trim()) return;
    setError(null);
    try {
      setDiff(await diffTasks(compareId.trim(), taskId));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [taskId, compareId]);

  // A fork's live part can run on another model, to compare models from the same starting point.
  const [models, setModels] = useState<LlmSettingsView | null>(null);
  const [forkModel, setForkModel] = useState("");
  useEffect(() => {
    getLlmSettings().then(setModels).catch(() => { /* the picker is optional */ });
  }, []);

  async function startReplay(mode: "full" | "fork") {
    if (!taskId) return;
    setBusy(true);
    setError(null);
    try {
      const forkAfter = mode === "fork" ? steps[index]?.seq : undefined;
      const { task_id } = await replayTask(taskId, mode, forkAfter, mode === "fork" ? forkModel || null : null);
      router.push(`/?task=${task_id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  }

  const current = steps[index];
  const agents = useMemo(() => Array.from(new Set(steps.map((s) => s.agent_path))), [steps]);

  if (!taskId) {
    return <div className="p-6 text-sm text-zinc-500">Open this page with ?task=&lt;task id&gt;.</div>;
  }

  return (
    <div className="flex min-h-full flex-col">
      <PageHeader
        title="Run journal"
        description={
          <>
            {task?.goal ?? taskId}
            {task?.replay_of_task_id && (
              <>
                {" "}· {task.replay_mode} replay of{" "}
                <Link className="text-brand-600 hover:underline dark:text-brand-400" href={`/replay?task=${task.replay_of_task_id}`}>
                  {task.replay_of_task_id.slice(0, 8)}
                </Link>
              </>
            )}
          </>
        }
        actions={
          <Link href={`/?task=${taskId}`}><Button icon={<Icons.Tasks className="h-3.5 w-3.5" />}>Agent graph</Button></Link>
        }
      />

      {error && <div className="px-4 py-2 text-xs text-rose-600">{error}</div>}

      <div className="flex flex-wrap items-center gap-3 border-b border-zinc-200 px-4 py-3 text-sm dark:border-zinc-800">
        <span className="text-zinc-500">
          {steps.length} steps · {agents.length} agents
        </span>
        <button
          onClick={() => startReplay("full")}
          disabled={busy || steps.length === 0}
          title="Re-run the whole task from the journal: no model calls, no external tool calls"
          className="rounded-lg bg-brand-500 px-3 py-1.5 text-xs font-medium text-white hover:bg-brand-600 disabled:opacity-50"
        >
          Replay in full
        </button>
        <button
          onClick={() => startReplay("fork")}
          disabled={busy || !current}
          title="Replay up to the selected step, then continue with the live model"
          className="rounded border border-blue-300 px-3 py-1.5 text-xs text-blue-700 hover:bg-blue-50 disabled:opacity-50 dark:border-blue-800 dark:text-blue-300 dark:hover:bg-blue-950"
        >
          Fork after step #{current?.seq ?? "–"}
        </button>
        {models && (
          <ModelPicker view={models} value={forkModel} onChange={setForkModel} className="ml-1" defaultLabel="Fork on the original's model"
            title="The model the fork continues on after the selected step (the original's when left on Default)" />
        )}
        <span className="mx-2 h-5 w-px bg-zinc-200 dark:bg-zinc-800" />
        <input
          value={compareId}
          onChange={(e) => setCompareId(e.target.value)}
          placeholder="Compare with task id…"
          className="w-64 rounded border border-zinc-300 bg-white px-2 py-1 text-xs dark:border-zinc-700 dark:bg-zinc-900"
        />
        <button
          onClick={loadDiff}
          disabled={!compareId.trim()}
          className="rounded border border-zinc-300 px-3 py-1.5 text-xs hover:bg-zinc-100 disabled:opacity-50 dark:border-zinc-700 dark:hover:bg-zinc-800"
        >
          Diff
        </button>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-2">
        {/* Step-through */}
        <section className="border-r border-zinc-200 p-4 dark:border-zinc-800">
          {steps.length === 0 ? (
            <div className="text-sm text-zinc-500">No recorded steps.</div>
          ) : (
            <>
              <div className="flex items-center gap-2">
                <button onClick={() => setIndex((i) => Math.max(0, i - 1))} className="rounded border px-2 py-1 text-xs dark:border-zinc-700">
                  ◀
                </button>
                <input
                  type="range"
                  min={0}
                  max={steps.length - 1}
                  value={index}
                  onChange={(e) => setIndex(Number(e.target.value))}
                  className="flex-1"
                />
                <button onClick={() => setIndex((i) => Math.min(steps.length - 1, i + 1))} className="rounded border px-2 py-1 text-xs dark:border-zinc-700">
                  ▶
                </button>
                <span className="w-20 text-right text-xs text-zinc-500">
                  {index + 1} / {steps.length}
                </span>
              </div>

              {current && (
                <div className="mt-3 space-y-2 text-sm">
                  <div className="flex flex-wrap gap-x-4 text-xs text-zinc-500">
                    <span>#{current.seq}</span>
                    <span className="font-medium text-zinc-800 dark:text-zinc-200">{current.role ?? current.agent_id}</span>
                    <span>{current.agent_path}</span>
                    <span>{current.kind === "llm" ? `decision ${current.step}` : `tool ${current.tool_name}`}</span>
                    <span>{new Date(current.at).toLocaleTimeString()}</span>
                  </div>
                  <div>{current.summary}</div>
                  <pre className="max-h-[50vh] overflow-auto rounded bg-zinc-100 p-2 text-[11px] dark:bg-zinc-900">
                    {JSON.stringify(current.payload, null, 2)}
                  </pre>
                </div>
              )}

              <ol className="mt-4 max-h-[40vh] space-y-0.5 overflow-auto text-xs">
                {steps.map((s, i) => (
                  <li key={s.seq}>
                    <button
                      onClick={() => setIndex(i)}
                      className={`w-full truncate rounded px-1 text-left ${i === index ? "bg-blue-100 dark:bg-blue-950" : "hover:bg-zinc-100 dark:hover:bg-zinc-900"}`}
                    >
                      <span className="text-zinc-400">#{s.seq}</span> {s.role ?? s.agent_path} ·{" "}
                      {s.kind === "llm" ? "decides" : s.tool_name}: {s.summary}
                    </button>
                  </li>
                ))}
              </ol>
            </>
          )}
        </section>

        {/* Diff */}
        <section className="p-4">
          {!diff ? (
            <div className="text-sm text-zinc-500">Compare this run with another (its original, or another replay) to see where they differ.</div>
          ) : (
            <div className="space-y-3 text-sm">
              <div className={diff.identical ? "font-medium text-emerald-700 dark:text-emerald-300" : "font-medium text-amber-700 dark:text-amber-300"}>
                {diff.identical ? "The runs match step for step." : "The runs differ."}
              </div>
              <div className="flex flex-wrap gap-x-4 text-xs text-zinc-500">
                <span>{diff.same} same</span>
                <span>{diff.different} different</span>
                <span>{diff.only_in_a} only in {diff.a.slice(0, 8)}</span>
                <span>{diff.only_in_b} only in {diff.b.slice(0, 8)}</span>
              </div>
              {(diff.agents_only_in_a.length > 0 || diff.agents_only_in_b.length > 0) && (
                <div className="text-xs">
                  Agents only in {diff.a.slice(0, 8)}: {diff.agents_only_in_a.join(", ") || "none"}; only in {diff.b.slice(0, 8)}:{" "}
                  {diff.agents_only_in_b.join(", ") || "none"}
                </div>
              )}
              <ol className="max-h-[70vh] space-y-1 overflow-auto text-xs">
                {diff.steps
                  .filter((s) => s.status !== "Same")
                  .concat(diff.steps.filter((s) => s.status === "Same"))
                  .map((s, i) => (
                    <li key={i} className="rounded border border-zinc-200 p-1.5 dark:border-zinc-800">
                      <div className={`font-medium ${STATUS_TONE[s.status]}`}>
                        {s.status} · {s.agent_path} · {s.kind === "llm" ? `decision ${s.step}` : s.tool_name}
                      </div>
                      {s.status !== "Same" && (
                        <div className="mt-0.5 grid grid-cols-2 gap-2 text-zinc-600 dark:text-zinc-400">
                          <span>{s.summary_a ?? "—"}</span>
                          <span>{s.summary_b ?? "—"}</span>
                        </div>
                      )}
                    </li>
                  ))}
              </ol>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
