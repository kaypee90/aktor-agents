"use client";

import { useEffect, useState } from "react";
import {
  getAgent,
  getAgentMessages,
  getAgentToolCalls,
  pauseAgent,
  resumeAgent,
  terminateAgent,
} from "@/lib/api";
import { STATUS_STYLES, isTerminal } from "@/lib/status";
import type { AgentSnapshot, MessageRecord, ToolCallRecord } from "@/lib/types";
import { BotIcon } from "./BotIcon";

export function AgentDetailsPanel({ agentId, onClose }: { agentId: string; onClose: () => void }) {
  const [snapshot, setSnapshot] = useState<AgentSnapshot | null>(null);
  const [messages, setMessages] = useState<MessageRecord[]>([]);
  const [toolCalls, setToolCalls] = useState<ToolCallRecord[]>([]);
  const [tab, setTab] = useState<"trace" | "messages">("trace");

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const [snap, msgs, calls] = await Promise.all([
          getAgent(agentId),
          getAgentMessages(agentId),
          getAgentToolCalls(agentId),
        ]);
        if (cancelled) return;
        setSnapshot(snap);
        setMessages(msgs);
        setToolCalls(calls);
      } catch {
        // Agent may not exist yet or the API may be briefly unavailable; try again next tick.
      }
    }

    load();
    const interval = setInterval(load, 3000);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [agentId]);

  if (!snapshot) {
    return (
      <div className="flex h-full flex-col p-4">
        <button onClick={onClose} className="self-end text-sm text-zinc-500 hover:text-zinc-800">
          ✕
        </button>
        <div className="mt-4 text-sm text-zinc-500">Loading agent…</div>
      </div>
    );
  }

  const style = STATUS_STYLES[snapshot.status];
  const terminal = isTerminal(snapshot.status);

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex items-start justify-between border-b border-zinc-200 p-3 dark:border-zinc-800">
        <div className="flex items-center gap-2">
          <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-md border ${style.border} ${style.text}`}>
            <BotIcon className="h-5 w-5" />
          </span>
          <div>
            <div className="text-sm font-semibold">{snapshot.role}</div>
            <div className="text-xs text-zinc-500">{snapshot.agent_id}</div>
          </div>
        </div>
        <button onClick={onClose} className="text-sm text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200">
          ✕
        </button>
      </div>

      <div className="space-y-2 border-b border-zinc-200 p-3 text-xs dark:border-zinc-800">
        <span className={`inline-block rounded px-2 py-0.5 font-medium ${style.text} ${style.bg} border ${style.border}`}>
          {snapshot.status}
        </span>
        <div><span className="text-zinc-500">Goal: </span>{snapshot.goal}</div>
        <div><span className="text-zinc-500">Parent: </span>{snapshot.parent_agent_id ?? "(root)"}</div>
        <div><span className="text-zinc-500">Depth: </span>{snapshot.depth}</div>
        <div><span className="text-zinc-500">Children: </span>{snapshot.children.length}</div>
        <div><span className="text-zinc-500">Capabilities: </span>{snapshot.capabilities.join(", ") || "—"}</div>
        <div><span className="text-zinc-500">Tools: </span>{snapshot.allowed_tools.join(", ")}</div>
        {snapshot.failure_reason && (
          <div className="text-rose-600 dark:text-rose-400">
            <span className="text-zinc-500">Failure: </span>{snapshot.failure_reason}
          </div>
        )}

        <div className="grid grid-cols-2 gap-x-3 gap-y-1 pt-1 text-zinc-600 dark:text-zinc-400">
          <div>Tokens: {snapshot.usage.tokens_used}/{snapshot.budget.max_tokens}</div>
          <div>Tool calls: {snapshot.usage.tool_calls_used}/{snapshot.budget.max_tool_calls}</div>
          <div>Children: {snapshot.usage.children_spawned}/{snapshot.budget.max_children}</div>
          <div>Cost: ${snapshot.usage.cost_usd.toFixed(4)}/${snapshot.budget.max_cost_usd.toFixed(2)}</div>
        </div>

        {!terminal && (
          <div className="flex gap-2 pt-2">
            <button
              onClick={() => pauseAgent(snapshot.agent_id)}
              className="rounded border border-zinc-300 px-2 py-1 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
            >
              Pause
            </button>
            <button
              onClick={() => resumeAgent(snapshot.agent_id)}
              className="rounded border border-zinc-300 px-2 py-1 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
            >
              Resume
            </button>
            <button
              onClick={() => terminateAgent(snapshot.agent_id)}
              className="rounded border border-rose-300 px-2 py-1 text-rose-600 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-400 dark:hover:bg-rose-950"
            >
              Terminate
            </button>
          </div>
        )}
      </div>

      <div className="flex border-b border-zinc-200 text-xs dark:border-zinc-800">
        {(["trace", "messages"] as const).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`flex-1 px-3 py-2 capitalize ${
              tab === t ? "border-b-2 border-blue-500 font-medium" : "text-zinc-500"
            }`}
          >
            {t === "trace" ? `Reasoning trace (${toolCalls.length})` : `Messages (${messages.length})`}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto p-3 text-xs">
        {tab === "trace" &&
          (toolCalls.length === 0 ? (
            <div className="text-zinc-500">No tool calls yet.</div>
          ) : (
            <ul className="space-y-2">
              {toolCalls.map((tc) => (
                <li key={tc.id} className="rounded border border-zinc-200 p-2 dark:border-zinc-800">
                  <div className="flex items-center justify-between">
                    <span className="font-mono font-semibold">{tc.tool_name}</span>
                    <span className={tc.success ? "text-emerald-600" : "text-rose-600"}>
                      {tc.success ? "ok" : "failed"}
                    </span>
                  </div>
                  <div className="mt-1 truncate text-zinc-500">{tc.arguments_json}</div>
                  {tc.result_json && <div className="mt-1 truncate text-zinc-400">→ {tc.result_json}</div>}
                </li>
              ))}
            </ul>
          ))}

        {tab === "messages" &&
          (messages.length === 0 ? (
            <div className="text-zinc-500">No messages yet.</div>
          ) : (
            <ul className="space-y-2">
              {messages.map((m) => {
                const outgoing = m.from_agent_id === snapshot.agent_id;
                return (
                  <li key={m.message_id} className="rounded border border-zinc-200 p-2 dark:border-zinc-800">
                    <div className="font-medium text-zinc-600 dark:text-zinc-300">
                      {outgoing ? `→ ${m.to_agent_id}` : `← ${m.from_agent_id}`}{" "}
                      <span className="font-normal text-zinc-400">({m.message_type})</span>
                    </div>
                    <div className="mt-1 text-zinc-500">{m.payload}</div>
                  </li>
                );
              })}
            </ul>
          ))}
      </div>
    </div>
  );
}
