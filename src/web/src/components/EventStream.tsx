"use client";

import { useEffect, useRef } from "react";
import type { RuntimeEvent } from "@/lib/types";

const TYPE_COLORS: Record<string, string> = {
  LlmCallCompleted: "text-zinc-500 dark:text-zinc-400",
  TaskModelChanged: "text-brand-600 dark:text-brand-400",
  AgentCreated: "text-sky-600 dark:text-sky-400",
  AgentStarted: "text-sky-600 dark:text-sky-400",
  AgentThinking: "text-indigo-600 dark:text-indigo-400",
  AgentToolCalled: "text-amber-600 dark:text-amber-400",
  AgentToolCompleted: "text-amber-600 dark:text-amber-400",
  AgentMessageSent: "text-fuchsia-600 dark:text-fuchsia-400",
  AgentMessageReceived: "text-fuchsia-600 dark:text-fuchsia-400",
  AgentSpawnRequested: "text-violet-600 dark:text-violet-400",
  AgentSpawned: "text-violet-600 dark:text-violet-400",
  AgentCompleted: "text-emerald-600 dark:text-emerald-400",
  AgentFailed: "text-rose-600 dark:text-rose-400",
  AgentRestarted: "text-orange-600 dark:text-orange-400",
  AgentTerminated: "text-zinc-500",
  AgentStatusChanged: "text-zinc-500",
  TaskCreated: "text-sky-700 dark:text-sky-300 font-semibold",
  TaskCompleted: "text-emerald-700 dark:text-emerald-300 font-semibold",
  ArtifactCreated: "text-teal-600 dark:text-teal-400",
  EnvironmentChanged: "text-zinc-500",
};

/** Friendlier names for the runtime's event types. */
const LABELS: Record<string, string> = {
  LlmCallCompleted: "Model",
  TaskModelChanged: "Model switch",
  AgentCreated: "Created",
  AgentStarted: "Started",
  AgentThinking: "Thinking",
  AgentToolCalled: "Tool call",
  AgentToolCompleted: "Tool result",
  AgentMessageSent: "Message",
  AgentMessageReceived: "Received",
  AgentSpawnRequested: "Spawn",
  AgentSpawned: "Spawned",
  AgentCompleted: "Completed",
  AgentFailed: "Failed",
  AgentRestarted: "Retry",
  AgentTerminated: "Stopped",
  AgentStatusChanged: "Status",
  TaskCreated: "Task",
  TaskCompleted: "Task done",
  ArtifactCreated: "File",
  EnvironmentChanged: "Environment",
};

export function EventStream({ events }: { events: RuntimeEvent[] }) {
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [events.length]);

  return (
    <div className="flex h-full flex-col overflow-y-auto py-1 text-xs">
      {events.length === 0 && (
        <div className="flex flex-col items-center gap-2 p-8 text-center text-zinc-500">
          <span className="h-4 w-4 animate-spin rounded-full border-2 border-zinc-300 border-t-brand-500" />
          Waiting for activity…
        </div>
      )}
      {events.map((evt) => (
        <div key={evt.event_id} className="group flex gap-2.5 px-4 py-1.5 hover:bg-zinc-50 dark:hover:bg-zinc-900/60">
          <span className="w-14 shrink-0 whitespace-nowrap pt-px font-mono text-[10px] text-zinc-400">{new Date(evt.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" })}</span>
          <span className={`w-20 shrink-0 font-medium ${TYPE_COLORS[evt.type] ?? "text-zinc-500"}`}>{LABELS[evt.type] ?? evt.type}</span>
          <span className="min-w-0 flex-1 text-zinc-700 dark:text-zinc-300">
            {evt.summary}
            {evt.agent_id && <span className="ml-1 font-mono text-[10px] text-zinc-400">{evt.agent_id}</span>}
          </span>
        </div>
      ))}
      <div ref={bottomRef} />
    </div>
  );
}
