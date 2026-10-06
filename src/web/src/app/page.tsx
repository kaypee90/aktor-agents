"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import {
  apiErrorMessage,
  cancelTask,
  getTask,
  getLlmSettings,
  getTaskEvents,
  getTaskSpend,
  listAgents,
  listTasks,
  pauseTask,
  resetAll,
  resumeTask,
  subscribeToEvents,
  modelChoices,
  switchTaskModel,
  type LlmSettingsView,
  type TaskListItem,
} from "@/lib/api";
import type { AgentListItem, AgentSpend, RuntimeEvent, TaskPreview, TaskSummary } from "@/lib/types";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { AgentGraph } from "@/components/AgentGraph";
import { EventStream } from "@/components/EventStream";
import { FinalResultPanel } from "@/components/FinalResultPanel";
import { TaskPreviewCard } from "@/components/TaskPreviewCard";
import { ModelPicker } from "@/components/tasks/ModelPicker";
import { ActivityIndicator } from "@/components/tasks/ActivityIndicator";
import { TaskChat } from "@/components/tasks/TaskChat";
import { TaskComposer } from "@/components/tasks/TaskComposer";
import { TaskToolsButton } from "@/components/tasks/TaskTools";
import { Badge, Button, Card, CardHeader, EmptyState, PageHeader, StatusBadge, Tabs, ago, compact, cx, money } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { isWorking } from "@/lib/status";

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
  const params = useSearchParams();
  const taskId = params.get("task");
  // Conversation by default; the agent graph on request (kept in the URL so a reload keeps it).
  const view = params.get("view") === "agents" ? "agents" : "chat";
  // The pre-run estimate only exists in memory, for the run just started from this page.
  const [preview, setPreview] = useState<{ taskId: string; preview: TaskPreview } | null>(null);

  const openTask = useCallback((id: string | null, newPreview: TaskPreview | null = null) => {
    setPreview(id && newPreview ? { taskId: id, preview: newPreview } : null);
    router.push(id ? `/?task=${encodeURIComponent(id)}` : "/");
  }, [router]);

  const setView = useCallback((next: "chat" | "agents") => {
    if (taskId) router.replace(`/?task=${encodeURIComponent(taskId)}${next === "agents" ? "&view=agents" : ""}`, { scroll: false });
  }, [router, taskId]);

  return taskId
    ? <TaskRun key={taskId} taskId={taskId} preview={preview?.taskId === taskId ? preview.preview : null} onBack={() => openTask(null)}
        view={view} onView={setView} />
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
    <div className="min-h-full">
      {me?.user?.platform_admin && (
        <div className="flex justify-end px-6 pt-4">
          <Button variant="ghost" size="sm" onClick={reset}>Reset all data</Button>
        </div>
      )}
      <div className="mx-auto max-w-3xl px-4 pb-12 pt-[8vh]">
        <div className="mb-8 text-center">
          <Icons.Logo className="mx-auto h-11 w-11" />
          <h1 className="mt-5 text-3xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">What should your agents work on?</h1>
          <p className="mx-auto mt-2 max-w-xl text-sm text-zinc-500">
            Ask for an outcome: a report, an analysis, a spreadsheet, a slide deck. Attach files for context. A team of agents plans the
            work, does it and hands you the result, and you can keep refining it in the conversation.
          </p>
        </div>
        <TaskComposer onStarted={(id, p) => onOpen(id, p)} />
      </div>

      <section className="mx-auto max-w-3xl px-4 pb-12">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Recent</h2>
          <Link href="/runs" className="inline-flex items-center gap-1 text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">
            All runs <Icons.ChevronRight className="h-3 w-3" />
          </Link>
        </div>
        {recent === null ? (
          <div className="h-24 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />
        ) : recent.length === 0 ? (
          <EmptyState icon={<Icons.Chat className="h-5 w-5" />} title="No conversations yet" description="Ask for something above to start your first one." />
        ) : (
          <ul className="divide-y divide-zinc-200 overflow-hidden rounded-2xl border border-zinc-200 bg-white dark:divide-zinc-800 dark:border-zinc-800 dark:bg-zinc-900">
            {recent.map((t) => (
              <li key={t.task_id}>
                <button onClick={() => onOpen(t.task_id)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-zinc-50 dark:hover:bg-zinc-800/60">
                  <Icons.Chat className="h-4 w-4 shrink-0 text-zinc-400" />
                  <span className="min-w-0 flex-1 truncate text-sm text-zinc-800 dark:text-zinc-200">{t.goal}</span>
                  {t.source !== "api" && <Badge>{t.source.toUpperCase()}</Badge>}
                  <StatusBadge status={t.status} />
                  <span className="hidden w-20 shrink-0 text-right text-[11px] text-zinc-400 sm:inline">{ago(t.created_at)}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

/** One run, live: the agent tree, events, the result, and controls. */
function TaskRun({ taskId, preview, onBack, view, onView }: {
  taskId: string; preview: TaskPreview | null; onBack: () => void; view: "chat" | "agents"; onView: (v: "chat" | "agents") => void;
}) {
  const [task, setTask] = useState<TaskSummary | null>(null);
  const [agents, setAgents] = useState<AgentListItem[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [spend, setSpend] = useState<Record<string, AgentSpend>>({});
  const [selected, setSelected] = useState<string | null>(null);
  const [panel, setPanel] = useState<"activity" | "result" | "estimate">("activity");
  const [copied, setCopied] = useState(false);
  const [models, setModels] = useState<LlmSettingsView | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);
  const [switchError, setSwitchError] = useState<string | null>(null);
  // Set on Pause/Resume so the buttons switch at once, until the next poll confirms it.
  const [pausedNow, setPausedNow] = useState<boolean | null>(null);

  useEffect(() => {
    getLlmSettings().then(setModels).catch(() => { /* the switcher is optional */ });
  }, []);

  /** Moves the whole team to another model from each agent's next step (docs/llm-settings.md). */
  async function switchModel(profileId: string) {
    setSwitching(profileId);
    setSwitchError(null);
    try {
      const { model } = await switchTaskModel(taskId, profileId);
      setTask((t) => (t ? { ...t, model } : t));
    } catch (e) {
      setSwitchError(apiErrorMessage(e));
    } finally {
      setSwitching(null);
    }
  }

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
      if (nextTask) {
        setTask(nextTask);
        setPausedNow(null);
      }
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
  const paused = running && (pausedNow ?? task?.paused ?? false);
  const busy = agents.filter((a) => isWorking(a.status)).length;
  const activity = running && <ActivityIndicator paused={paused} busy={busy} />;

  async function setPaused(pause: boolean) {
    setPausedNow(pause);
    try {
      await (pause ? pauseTask(taskId) : resumeTask(taskId));
    } catch {
      setPausedNow(null);
    }
  }
  const modelNames = useMemo(
    () => Object.fromEntries(models ? modelChoices(models).map((m) => [m.id, m.name]) : []),
    [models],
  );
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

  const viewToggle = (
    <div className="flex rounded-lg border border-zinc-200 bg-zinc-50 p-0.5 text-xs dark:border-zinc-700 dark:bg-zinc-900" role="tablist">
      {([["chat", "Chat", Icons.Chat], ["agents", "Agents", Icons.Graph]] as const).map(([id, label, Icon]) => (
        <button key={id} role="tab" aria-selected={view === id} onClick={() => onView(id)}
          className={cx("inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 font-medium transition",
            view === id ? "bg-white text-zinc-900 shadow-sm dark:bg-zinc-800 dark:text-zinc-100" : "text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200")}>
          <Icon className="h-3.5 w-3.5" /> {label}
        </button>
      ))}
    </div>
  );

  const stop = () => {
    if (confirm("Stop the team? What it has done so far is kept, and you can follow up later.")) cancelTask(taskId);
  };

  if (view === "chat") {
    return (
      <div className="flex h-full flex-col">
        <header className="flex items-center gap-3 border-b border-zinc-200 bg-white px-4 py-2.5 dark:border-zinc-800 dark:bg-zinc-950">
          <button onClick={onBack} className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" title="All tasks">
            <Icons.ChevronRight className="h-4 w-4 rotate-180" />
          </button>
          <span className="min-w-0 flex-1 truncate text-sm font-medium text-zinc-900 dark:text-zinc-100" title={task?.goal}>{task?.goal ?? "Loading…"}</span>
          {task && <StatusBadge status={task.status} />}
          {activity}
          <span className="hidden text-xs text-zinc-500 md:inline">{agents.length} agents · {money(totalCost)}</span>
          <TaskToolsButton taskId={taskId} />
          {viewToggle}
          <Button size="sm" variant="ghost" icon={copied ? <Icons.Check className="h-3.5 w-3.5" /> : <Icons.Link className="h-3.5 w-3.5" />} onClick={copyLink}>
            <span className="hidden sm:inline">{copied ? "Copied" : "Share"}</span>
          </Button>
        </header>
        <div className="min-h-0 flex-1">
          <TaskChat taskId={taskId} running={running} events={events} agents={agents} onStop={stop}
            budget={task?.budget} ceiling={task?.budget_ceiling}
            onShowAgents={(id) => { if (id) setSelected(id); onView("agents"); }} />
        </div>
      </div>
    );
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
            {viewToggle}
            <TaskToolsButton taskId={taskId} />
            {task && <StatusBadge status={task.status} />}
            {activity}
            {running && (
              <>
                {paused
                  ? <Button size="sm" icon={<Icons.Play className="h-3.5 w-3.5" />} onClick={() => setPaused(false)}>Resume</Button>
                  : <Button size="sm" icon={<Icons.Pause className="h-3.5 w-3.5" />} onClick={() => setPaused(true)}>Pause</Button>}
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
          {task?.model && models && running && (
            <ModelPicker view={models} value={switching ?? task.model.profile_id} allowDefault={false} disabled={switching !== null}
              title="Switch the task to another model: agents use it from their next step (agents given their own model keep it)"
              onChange={(id) => id !== task.model?.profile_id && switchModel(id)} />
          )}
          {task?.model && !running && (
            <span title="The model this task ran on last">
              Model <span className="font-medium text-zinc-700 dark:text-zinc-300">{task.model.provider === "Mock" ? "Mock (demo)" : task.model.name}</span>
              {task.model.provider !== "Mock" && task.model.name !== task.model.model && <span className="font-mono"> · {task.model.model}</span>}
            </span>
          )}
          {switchError && <span className="text-rose-600 dark:text-rose-400">{switchError}</span>}
        </div>
      </PageHeader>

      <div className="flex min-h-0 flex-1">
        <div className="relative min-w-0 flex-1">
          <AgentGraph agents={agents} spend={spend} selectedId={selected} onSelect={setSelected} modelNames={modelNames} />
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
