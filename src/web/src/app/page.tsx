"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import {
  cancelTask,
  getTask,
  getTaskEvents,
  getTaskSpend,
  listAgents,
  listTasks,
  pauseTask,
  resetAll,
  resumeTask,
  subscribeToEvents,
  type TaskListItem,
} from "@/lib/api";
import type { AgentListItem, AgentSpend, RuntimeEvent, TaskPreview, TaskSummary } from "@/lib/types";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { AgentGraph } from "@/components/AgentGraph";
import { EventStream } from "@/components/EventStream";
import { FinalResultPanel } from "@/components/FinalResultPanel";
import { TaskPreviewCard } from "@/components/TaskPreviewCard";
import { TaskComposer } from "@/components/tasks/TaskComposer";
import { Badge, Button, Card, CardHeader, EmptyState, PageHeader, StatusBadge, Tabs, ago, compact, money } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const MAX_EVENTS = 500;
const POLL_INTERVAL_MS = 2000;
const TERMINAL = ["Completed", "Failed", "Terminated", "TimedOut", "Rejected"];

export default function TasksPage() {
  return <Suspense><TasksFromUrl /></Suspense>;
}

/** The URL says which run is open (?task=<id>): every result from MCP, A2A or the API links to
 * one. useSearchParams stays current on in-app navigation, unlike window.location. */
function TasksFromUrl() {
  const router = useRouter();
  const taskId = useSearchParams().get("task");
  // The pre-run estimate only exists in memory, for the run just started from this page.
  const [preview, setPreview] = useState<{ taskId: string; preview: TaskPreview } | null>(null);

  const openTask = useCallback((id: string | null, newPreview: TaskPreview | null = null) => {
    setPreview(id && newPreview ? { taskId: id, preview: newPreview } : null);
    router.push(id ? `/?task=${encodeURIComponent(id)}` : "/");
  }, [router]);

  return taskId
    ? <TaskRun key={taskId} taskId={taskId} preview={preview?.taskId === taskId ? preview.preview : null} onBack={() => openTask(null)} />
    : <TaskHome onOpen={openTask} />;
}

/** The start screen: compose a task, and pick up recent runs. */
function TaskHome({ onOpen }: { onOpen: (id: string, preview?: TaskPreview | null) => void }) {
  const { me } = useAuth();
  const [recent, setRecent] = useState<TaskListItem[] | null>(null);

  useEffect(() => {
    listTasks(8).then(setRecent).catch(() => setRecent([]));
  }, []);

  async function reset() {
    if (!confirm("This permanently deletes all tasks, agents, messages, events and artifacts. Continue?")) return;
    await resetAll();
    setRecent([]);
  }

  return (
    <div>
      <PageHeader
        title="Tasks"
        description="Give a goal to an agent team. It plans the work, starts the specialists it needs, works under a budget the runtime enforces, and reports back."
        actions={me?.user?.platform_admin && (
          <Button variant="danger" size="sm" onClick={reset}>Reset all data</Button>
        )}
      />
      <div className="mx-auto max-w-5xl space-y-8 px-6 py-6">
        <TaskComposer onStarted={(id, p) => onOpen(id, p)} />

        <section>
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Recent runs</h2>
            <Link href="/runs" className="inline-flex items-center gap-1 text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">
              All runs <Icons.ChevronRight className="h-3 w-3" />
            </Link>
          </div>
          {recent === null ? (
            <div className="h-24 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />
          ) : recent.length === 0 ? (
            <EmptyState icon={<Icons.Tasks className="h-5 w-5" />} title="No runs yet" description="Describe a goal above to start your first agent team." />
          ) : (
            <div className="grid gap-3 sm:grid-cols-2">
              {recent.map((t) => (
                <button key={t.task_id} onClick={() => onOpen(t.task_id)} className="text-left">
                  <Card className="h-full p-4 transition hover:border-brand-300 hover:shadow-md dark:hover:border-brand-900">
                    <div className="flex items-start justify-between gap-3">
                      <div className="line-clamp-2 text-sm font-medium text-zinc-900 dark:text-zinc-100">{t.goal}</div>
                      <StatusBadge status={t.status} />
                    </div>
                    <div className="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px] text-zinc-500">
                      <span>{ago(t.created_at)}</span>
                      <span>{t.agents} agents</span>
                      <span>{money(t.cost_usd)}</span>
                      {t.source !== "api" && <Badge>{t.source.toUpperCase()}</Badge>}
                    </div>
                  </Card>
                </button>
              ))}
            </div>
          )}
        </section>
      </div>
    </div>
  );
}

/** One run, live: the agent tree, events, the result, and controls. */
function TaskRun({ taskId, preview, onBack }: { taskId: string; preview: TaskPreview | null; onBack: () => void }) {
  const [task, setTask] = useState<TaskSummary | null>(null);
  const [agents, setAgents] = useState<AgentListItem[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [spend, setSpend] = useState<Record<string, AgentSpend>>({});
  const [selected, setSelected] = useState<string | null>(null);
  const [panel, setPanel] = useState<"activity" | "result" | "estimate">("activity");
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function poll() {
      const [nextAgents, nextTask, nextSpend] = await Promise.all([
        listAgents(),
        getTask(taskId).catch(() => null),
        getTaskSpend(taskId).catch(() => null),
      ]);
      if (cancelled) return;
      if (nextSpend) setSpend(Object.fromEntries(nextSpend.map((s) => [s.agent_id, s])));
      setAgents(nextTask ? nextAgents.filter((a) => a.root_agent_id === nextTask.root_agent_id) : []);
      if (nextTask) setTask(nextTask);
    }
    poll();
    const interval = setInterval(poll, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [taskId]);

  useEffect(() => {
    // Keep each event once, in time order: history and the live stream can overlap.
    const merge = (incoming: RuntimeEvent[]) => setEvents((prev) => {
      const seen = new Set(prev.map((e) => e.event_id));
      const next = [...prev, ...incoming.filter((e) => !seen.has(e.event_id))]
        .sort((a, b) => a.timestamp.localeCompare(b.timestamp));
      return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
    });
    const source = subscribeToEvents((evt) => merge([evt]), taskId);
    // What happened before this page opened (or the whole run, if it already finished).
    getTaskEvents(taskId, MAX_EVENTS)
      .then((rows) => merge(rows.map((r) => {
        let data: Record<string, string> = {};
        try { data = JSON.parse(r.data_json) ?? {}; } catch { /* keep empty */ }
        return { ...r, data };
      })))
      .catch(() => { /* live events still arrive */ });
    return () => source.close();
  }, [taskId]);

  const running = task !== null && !TERMINAL.includes(task.status);
  const artifactWrites = events.filter((e) => e.type === "ArtifactCreated").length;
  const totalCost = Object.values(spend).reduce((n, s) => n + s.cost_usd, 0);
  const totalTokens = Object.values(spend).reduce((n, s) => n + s.tokens_used, 0);

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(window.location.href);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard blocked.
    }
  }

  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title={
          <span className="flex items-center gap-3">
            <button onClick={onBack} className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" title="All tasks">
              <Icons.ChevronRight className="h-4 w-4 rotate-180" />
            </button>
            <span className="line-clamp-1">{task?.goal ?? "Loading…"}</span>
          </span>
        }
        actions={
          <>
            {task && <StatusBadge status={task.status} />}
            {running && (
              <>
                <Button size="sm" icon={<Icons.Pause className="h-3.5 w-3.5" />} onClick={() => pauseTask(taskId)}>Pause</Button>
                <Button size="sm" icon={<Icons.Play className="h-3.5 w-3.5" />} onClick={() => resumeTask(taskId)}>Resume</Button>
                <Button size="sm" variant="danger" icon={<Icons.Stop className="h-3.5 w-3.5" />}
                  onClick={() => confirm("Stop every agent of this task?") && cancelTask(taskId)}>
                  Cancel
                </Button>
              </>
            )}
            <Link href={`/replay?task=${taskId}`}>
              <Button size="sm" icon={<Icons.Replay className="h-3.5 w-3.5" />}>Journal &amp; replay</Button>
            </Link>
            <Button size="sm" variant="ghost" icon={copied ? <Icons.Check className="h-3.5 w-3.5" /> : <Icons.Link className="h-3.5 w-3.5" />} onClick={copyLink}>
              {copied ? "Copied" : "Link"}
            </Button>
          </>
        }
      >
        <div className="mt-3 flex flex-wrap gap-x-5 gap-y-1 text-xs text-zinc-500">
          <span><span className="font-medium text-zinc-700 dark:text-zinc-300">{agents.length}</span> agents</span>
          <span><span className="font-medium text-zinc-700 dark:text-zinc-300">{compact(totalTokens)}</span> tokens</span>
          <span><span className="font-medium text-zinc-700 dark:text-zinc-300">{money(totalCost)}</span> spent</span>
          {task && <span>Started {ago(task.created_at)}</span>}
          {task?.correlation_id && <span className="font-mono">corr {task.correlation_id}</span>}
          {task?.replay_of_task_id && <Badge tone="blue">{task.replay_mode} replay</Badge>}
        </div>
      </PageHeader>

      <div className="flex min-h-0 flex-1">
        <div className="relative min-w-0 flex-1">
          <AgentGraph agents={agents} spend={spend} selectedId={selected} onSelect={setSelected} />
        </div>

        <aside className="flex w-[26rem] shrink-0 flex-col border-l border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
          {selected ? (
            <AgentDetailsPanel agentId={selected} onClose={() => setSelected(null)} />
          ) : (
            <>
              <Tabs
                className="px-3 pt-2"
                value={panel}
                onChange={setPanel}
                tabs={[
                  { id: "activity", label: "Activity", count: events.length },
                  { id: "result", label: "Result" },
                  ...(preview ? [{ id: "estimate" as const, label: "Estimate" }] : []),
                ]}
              />
              <div className="min-h-0 flex-1 overflow-y-auto">
                {panel === "activity" && <EventStream events={events} />}
                {panel === "result" && (task
                  ? <FinalResultPanel taskId={taskId} taskStatus={task.status} artifactWrites={artifactWrites} />
                  : <div className="p-4 text-sm text-zinc-500">Loading…</div>)}
                {panel === "result" && running && <div className="p-4 text-sm text-zinc-500">The result appears when the root agent reports.</div>}
                {panel === "estimate" && preview && (
                  <Card className="m-3"><CardHeader title="Pre-run estimate" description="From the planning call before this run." /><div className="p-4"><TaskPreviewCard preview={preview} /></div></Card>
                )}
              </div>
            </>
          )}
        </aside>
      </div>
    </div>
  );
}
