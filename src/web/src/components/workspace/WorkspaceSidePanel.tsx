"use client";

import { useMemo, useState } from "react";
import { API_BASE, addWorkspaceTrigger, deleteWorkspaceTrigger } from "@/lib/api";
import type { AgentListItem, AgentStatus, RuntimeEvent } from "@/lib/types";
import type { TriggerView, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { STATUS_STYLES } from "@/lib/status";
import { AgentGraph } from "../AgentGraph";
import { BotIcon } from "../BotIcon";
import { EventStream } from "../EventStream";

type Tab = "agents" | "triggers" | "graph" | "events";

function describeSchedule(t: TriggerView) {
  if (t.kind === "Webhook") return "on webhook";
  if (t.cron) return `cron ${t.cron} (UTC)`;
  const s = t.interval_seconds ?? 0;
  return s % 3600 === 0 ? `every ${s / 3600}h` : s % 60 === 0 ? `every ${s / 60} min` : `every ${s}s`;
}

export function WorkspaceSidePanel({ workspace, events, onSelectAgent, onChanged }: {
  workspace: WorkspaceSnapshot;
  events: RuntimeEvent[];
  onSelectAgent: (id: string) => void;
  onChanged: () => void;
}) {
  const [tab, setTab] = useState<Tab>("agents");

  const graphAgents: AgentListItem[] = useMemo(() => {
    const byId = new Map(workspace.agents.map((a) => [a.agent_id, a]));
    const depth = (id: string, seen = 0): number => {
      const p = byId.get(id)?.parent_agent_id;
      return p && byId.has(p) && seen < 20 ? 1 + depth(p, seen + 1) : 0;
    };
    return workspace.agents.map((a) => ({
      agent_id: a.agent_id,
      role: a.role,
      goal: a.goal,
      status: (a.status as AgentStatus) ?? "Idle",
      capabilities: [],
      parent_agent_id: a.parent_agent_id,
      root_agent_id: workspace.coordinator_agent_id,
      depth: depth(a.agent_id),
    }));
  }, [workspace.agents, workspace.coordinator_agent_id]);

  const tabs: [Tab, string][] = [
    ["agents", `Agents (${workspace.agents.length})`],
    ["triggers", `Triggers (${workspace.triggers.length})`],
    ["graph", "Graph"],
    ["events", "Events"],
  ];

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex border-b border-neutral-200 text-xs dark:border-neutral-800">
        {tabs.map(([t, label]) => (
          <button key={t} onClick={() => setTab(t)} className={`flex-1 px-2 py-2 ${tab === t ? "border-b-2 border-blue-500 font-medium" : "text-neutral-500"}`}>
            {label}
          </button>
        ))}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {tab === "agents" && (
          <ul className="divide-y divide-neutral-200 text-xs dark:divide-neutral-800">
            {workspace.agents.map((a) => {
              const style = STATUS_STYLES[(a.status as AgentStatus)] ?? STATUS_STYLES.Idle;
              return (
                <li key={a.agent_id}>
                  <button onClick={() => onSelectAgent(a.agent_id)} className="flex w-full gap-2 p-3 text-left hover:bg-neutral-50 dark:hover:bg-neutral-900">
                    <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-md border ${style.border} ${style.text}`}>
                      <BotIcon className="h-5 w-5" />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-1.5">
                        <span className="truncate font-semibold">{a.role}</span>
                        {a.standing && <span className="rounded bg-indigo-100 px-1 text-[9px] uppercase text-indigo-700 dark:bg-indigo-950 dark:text-indigo-300">standing</span>}
                        <span className={`ml-auto text-[10px] ${style.text}`}>{a.status}</span>
                      </span>
                      <span className="block truncate text-neutral-500">{a.current_task ?? a.goal}</span>
                      <span className="block text-[10px] text-neutral-400">{a.tokens_used.toLocaleString()} tokens · ${a.cost_usd.toFixed(4)}</span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        {tab === "triggers" && <Triggers workspace={workspace} onChanged={onChanged} />}

        {tab === "graph" && (
          <div className="h-full min-h-[400px]">
            <AgentGraph agents={graphAgents} selectedId={null} onSelect={onSelectAgent} />
          </div>
        )}

        {tab === "events" && <EventStream events={events} />}
      </div>
    </div>
  );
}

function Triggers({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const [kind, setKind] = useState<"schedule" | "webhook">("schedule");
  const [name, setName] = useState("");
  const [instruction, setInstruction] = useState("");
  const [minutes, setMinutes] = useState(60);
  const [cron, setCron] = useState("");
  const [target, setTarget] = useState(workspace.coordinator_agent_id);
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<TriggerView | null>(null);
  const agentName = (id: string) => workspace.agents.find((a) => a.agent_id === id)?.role ?? id;

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const t = await addWorkspaceTrigger(workspace.workspace_id, {
        kind,
        name: name || (kind === "webhook" ? "Webhook" : "Schedule"),
        instruction,
        target_agent_id: target,
        ...(kind === "schedule" ? (cron.trim() ? { cron: cron.trim() } : { every_minutes: minutes }) : {}),
      });
      setCreated(t);
      setName("");
      setInstruction("");
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message.replace(/^.*?failed: \d+ /, "") : "Failed");
    }
  }

  const field = "w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900";

  return (
    <div className="space-y-3 p-3 text-xs">
      {workspace.triggers.length === 0 && <div className="text-neutral-500">No triggers yet. Agents create their own, or add one here.</div>}
      {workspace.triggers.map((t) => (
        <div key={t.trigger_id} className="rounded border border-neutral-200 p-2 dark:border-neutral-800">
          <div className="flex items-center justify-between gap-2">
            <span className="font-semibold">{t.kind === "Webhook" ? "🔗" : "⏰"} {t.name}</span>
            <button onClick={() => deleteWorkspaceTrigger(workspace.workspace_id, t.trigger_id).then(onChanged)} className="text-[10px] text-rose-600 hover:underline">delete</button>
          </div>
          <div className="text-neutral-500">{describeSchedule(t)} → {agentName(t.target_agent_id)} · by {t.created_by === "user" ? "you" : agentName(t.created_by)}</div>
          {t.instruction && <div className="mt-0.5 text-neutral-600 dark:text-neutral-400">“{t.instruction}”</div>}
          <div className="mt-0.5 text-[10px] text-neutral-400">
            fired {t.fire_count}×{t.last_fired_at && ` · last ${new Date(t.last_fired_at).toLocaleTimeString()}`}
            {t.next_due_at && t.kind === "Schedule" && ` · next ${new Date(t.next_due_at).toLocaleString()}`}
            {t.dropped_count > 0 && ` · ${t.dropped_count} dropped (rate limit)`}
          </div>
        </div>
      ))}

      <form onSubmit={add} className="space-y-2 rounded border border-dashed border-neutral-300 p-2 dark:border-neutral-700">
        <div className="flex gap-1">
          {(["schedule", "webhook"] as const).map((k) => (
            <button type="button" key={k} onClick={() => setKind(k)} className={`rounded px-2 py-0.5 capitalize ${kind === k ? "bg-neutral-800 text-white dark:bg-neutral-100 dark:text-neutral-900" : "text-neutral-500"}`}>{k}</button>
          ))}
        </div>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" className={field} />
        <textarea value={instruction} onChange={(e) => setInstruction(e.target.value)} rows={2} placeholder="What the agent should do each time" className={field} />
        {kind === "schedule" && (
          <div className="grid grid-cols-2 gap-2">
            <label className="flex items-center gap-1 whitespace-nowrap">
              every
              <input type="number" min={1} value={minutes} onChange={(e) => setMinutes(Number(e.target.value) || 1)}
                className="w-16 rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900" />
              min
            </label>
            <input value={cron} onChange={(e) => setCron(e.target.value)} placeholder="or cron, e.g. 0 9 * * 1-5" className={field} />
          </div>
        )}
        <select value={target} onChange={(e) => setTarget(e.target.value)} className={field}>
          {workspace.agents.map((a) => <option key={a.agent_id} value={a.agent_id}>{a.role}</option>)}
        </select>
        {error && <div className="text-rose-600">{error}</div>}
        <button type="submit" className="rounded bg-blue-600 px-3 py-1 font-medium text-white hover:bg-blue-700">Add {kind}</button>
        {created?.webhook_path && (
          <div className="rounded bg-amber-50 p-2 text-amber-900 dark:bg-amber-950 dark:text-amber-100">
            Point your service at this URL (POST). Keep it secret; it&apos;s also in the chat:
            <code className="mt-1 block break-all text-[10px]">{API_BASE}{created.webhook_path}</code>
          </div>
        )}
      </form>
    </div>
  );
}
