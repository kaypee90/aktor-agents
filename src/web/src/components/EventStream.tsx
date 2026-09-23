"use client";

import { useEffect, useRef } from "react";
import type { RuntimeEvent } from "@/lib/types";

const TYPE_COLORS: Record<string, string> = {
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
  AgentTerminated: "text-neutral-500",
  AgentStatusChanged: "text-neutral-500",
  TaskCreated: "text-sky-700 dark:text-sky-300 font-semibold",
  TaskCompleted: "text-emerald-700 dark:text-emerald-300 font-semibold",
  ArtifactCreated: "text-teal-600 dark:text-teal-400",
  EnvironmentChanged: "text-neutral-500",
};

export function EventStream({ events }: { events: RuntimeEvent[] }) {
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [events.length]);

  return (
    <div className="flex h-full flex-col overflow-y-auto p-2 font-mono text-xs">
      {events.length === 0 && (
        <div className="p-4 text-center text-neutral-500">Waiting for events…</div>
      )}
      {events.map((evt) => (
        <div key={evt.event_id} className="flex gap-2 border-b border-neutral-200/60 px-2 py-1 dark:border-neutral-800">
          <span className="shrink-0 text-neutral-400">
            {new Date(evt.timestamp).toLocaleTimeString()}
          </span>
          <span className={`shrink-0 ${TYPE_COLORS[evt.type] ?? "text-neutral-500"}`}>{evt.type}</span>
          {evt.agent_id && <span className="shrink-0 text-neutral-400">[{evt.agent_id}]</span>}
          <span className="truncate text-neutral-700 dark:text-neutral-300">{evt.summary}</span>
        </div>
      ))}
      <div ref={bottomRef} />
    </div>
  );
}
