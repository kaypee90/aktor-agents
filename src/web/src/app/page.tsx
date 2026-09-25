"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { getTask, listAgents, resetAll, subscribeToEvents } from "@/lib/api";
import type { AgentListItem, RuntimeEvent, TaskSummary } from "@/lib/types";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { AgentGraph } from "@/components/AgentGraph";
import { EventStream } from "@/components/EventStream";
import { FinalResultPanel } from "@/components/FinalResultPanel";
import { GoalForm } from "@/components/GoalForm";
import { TaskStatusBar } from "@/components/TaskStatusBar";

const MAX_EVENTS = 500;
const POLL_INTERVAL_MS = 2000;

export default function Home() {
  const [taskId, setTaskId] = useState<string | null>(null);
  const [task, setTask] = useState<TaskSummary | null>(null);
  const [agents, setAgents] = useState<AgentListItem[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const sourceRef = useRef<EventSource | null>(null);

  const handleTaskCreated = useCallback((newTaskId: string) => {
    setTaskId(newTaskId);
    setTask(null);
    setAgents([]);
    setEvents([]);
    setSelectedAgentId(null);
  }, []);

  async function handleReset() {
    if (!confirm("This permanently deletes all tasks, agents, messages, events, and artifacts. Continue?")) {
      return;
    }
    setResetting(true);
    try {
      await resetAll();
      setTaskId(null);
      setTask(null);
      setAgents([]);
      setEvents([]);
      setSelectedAgentId(null);
      sourceRef.current?.close();
    } catch (err) {
      alert(err instanceof Error ? err.message : "Reset failed");
    } finally {
      setResetting(false);
    }
  }

  // Poll agent graph + task status (the source of truth is Postgres/the registry grain; polling is
  // simple and sufficient at demo scale, while the event stream below gives real-time detail).
  useEffect(() => {
    if (!taskId) return;

    let cancelled = false;
    async function poll() {
      const [nextAgents, nextTask] = await Promise.all([
        listAgents(),
        getTask(taskId!).catch(() => null),
      ]);
      if (cancelled) return;
      // listAgents() is system-wide; scope the graph to this task's own agent tree.
      setAgents(nextTask ? nextAgents.filter((a) => a.root_agent_id === nextTask.root_agent_id) : nextAgents);
      if (nextTask) setTask(nextTask);
    }

    poll();
    const interval = setInterval(poll, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [taskId]);

  // Live event stream via SSE, filtered server-side to this task.
  useEffect(() => {
    if (!taskId) return;

    sourceRef.current?.close();
    setEvents([]);
    const source = subscribeToEvents((evt) => {
      setEvents((prev) => {
        const next = [...prev, evt];
        return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
      });
    }, taskId);
    sourceRef.current = source;

    return () => source.close();
  }, [taskId]);

  return (
    <div className="flex h-screen flex-col bg-white text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
      <header className="flex items-start justify-between border-b border-neutral-200 px-4 py-3 dark:border-neutral-800">
        <div>
          <h1 className="text-lg font-semibold">Aktor Agents — Autonomous Agent Runtime</h1>
          <p className="text-xs text-neutral-500">
            Submit a goal and watch the agent hierarchy, messages, and tool calls unfold live.
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
        <Link
          href="/workspaces"
          className="rounded border border-emerald-300 px-3 py-1.5 text-xs text-emerald-700 hover:bg-emerald-50 dark:border-emerald-800 dark:text-emerald-300 dark:hover:bg-emerald-950"
        >
          Workspaces →
        </Link>
        <Link
          href="/simulation"
          className="rounded border border-blue-300 px-3 py-1.5 text-xs text-blue-700 hover:bg-blue-50 dark:border-blue-800 dark:text-blue-300 dark:hover:bg-blue-950"
        >
          World simulation →
        </Link>
        <button
          onClick={handleReset}
          disabled={resetting}
          title="Delete all tasks, agents, messages, events, and artifacts to start fresh"
          className="shrink-0 rounded border border-neutral-300 px-3 py-1.5 text-xs text-neutral-600 hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        >
          {resetting ? "Resetting…" : "Reset all"}
        </button>
        </div>
      </header>

      <div className="border-b border-neutral-200 p-3 dark:border-neutral-800">
        <GoalForm onTaskCreated={handleTaskCreated} />
        {task && (
          <div className="mt-2">
            <TaskStatusBar task={task} />
          </div>
        )}
      </div>

      {task && <FinalResultPanel taskId={task.task_id} taskStatus={task.status} />}

      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1 border-r border-neutral-200 dark:border-neutral-800">
          <AgentGraph agents={agents} selectedId={selectedAgentId} onSelect={setSelectedAgentId} />
        </div>

        {selectedAgentId && (
          <div className="w-96 shrink-0 border-r border-neutral-200 dark:border-neutral-800">
            <AgentDetailsPanel agentId={selectedAgentId} onClose={() => setSelectedAgentId(null)} />
          </div>
        )}

        <div className="w-96 shrink-0">
          <EventStream events={events} />
        </div>
      </div>
    </div>
  );
}
