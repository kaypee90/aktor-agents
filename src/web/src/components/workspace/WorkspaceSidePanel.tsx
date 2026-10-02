"use client";

import { useState } from "react";
import { API_BASE, addWorkspaceTrigger, deleteWorkspaceTrigger, getWorkspaceFiles } from "@/lib/api";
import type { AgentStatus, RuntimeEvent } from "@/lib/types";
import type { TriggerView, WorkspaceFile, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { STATUS_STYLES } from "@/lib/status";
import { useLiveList } from "@/lib/useLiveList";
import { BotIcon } from "../BotIcon";
import { EventStream } from "../EventStream";
import { FilesPanel } from "./FilesPanel";
import { pauseInfo } from "./pauseInfo";
import { IntegrationsPanel } from "./IntegrationsPanel";
import { SafetyPanel } from "./SafetyPanel";

type Tab = "agents" | "files" | "triggers" | "integrations" | "safety" | "events";

function describeInterval(t: TriggerView) {
  if (t.cron) return `cron ${t.cron} (UTC)`;
  const s = t.interval_seconds ?? 0;
  return s % 3600 === 0 ? `every ${s / 3600}h` : s % 60 === 0 ? `every ${s / 60} min` : `every ${s}s`;
}

function describeSchedule(t: TriggerView) {
  if (t.kind === "Webhook") return "on webhook";
  if (t.kind === "Watch") return `watch, ${describeInterval(t)}`;
  return describeInterval(t);
}

export function WorkspaceSidePanel({ workspace, events, onSelectAgent, onChanged }: {
  workspace: WorkspaceSnapshot;
  events: RuntimeEvent[];
  onSelectAgent: (id: string) => void;
  onChanged: () => void;
}) {
  const [tab, setTab] = useState<Tab>("agents");
  // Reload the file list whenever an agent saves a file (and once on open).
  const fileWrites = events.filter((e) => e.type === "ArtifactCreated").length;
  const workspaceId = workspace.workspace_id;
  const files: WorkspaceFile[] = useLiveList(workspaceId, fileWrites, () => getWorkspaceFiles(workspaceId)) ?? [];


  const pendingApprovals = workspace.approvals?.filter((a) => a.status === "Pending").length ?? 0;
  const tabs: [Tab, string, number | null][] = [
    ["agents", "Agents", workspace.agents.length],
    ["files", "Files", files.length || null],
    ["triggers", "Triggers", workspace.triggers.length || null],
    ["integrations", "Integrations", workspace.connections?.length || null],
    ["safety", "Safety", pendingApprovals || null],
    ["events", "Events", null],
  ];

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex gap-1 overflow-x-auto border-b border-zinc-200 px-2 text-xs dark:border-zinc-800">
        {tabs.map(([t, label, count]) => (
          <button key={t} onClick={() => setTab(t)}
            className={`-mb-px flex shrink-0 items-center gap-1.5 whitespace-nowrap border-b-2 px-2 py-2.5 transition-colors ${
              tab === t ? "border-brand-500 font-medium text-zinc-900 dark:text-zinc-100" : "border-transparent text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200"
            }`}>
            {label}
            {count !== null && (
              <span className={`rounded-full px-1.5 text-[10px] tabular-nums ${
                t === "safety" ? "bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300" : "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-400"
              }`}>{count}</span>
            )}
          </button>
        ))}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {tab === "agents" && (
          <ul className="divide-y divide-zinc-200 text-xs dark:divide-zinc-800">
            {workspace.agents.map((a) => {
              const style = STATUS_STYLES[(a.status as AgentStatus)] ?? STATUS_STYLES.Idle;
              const paused = pauseInfo(a);
              return (
                <li key={a.agent_id}>
                  <button onClick={() => onSelectAgent(a.agent_id)} className="flex w-full gap-2 p-3 text-left hover:bg-zinc-50 dark:hover:bg-zinc-900">
                    <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-md border ${style.border} ${style.text}`}>
                      <BotIcon className="h-5 w-5" />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-1.5">
                        <span className="truncate font-semibold">{a.role}</span>
                        {a.standing && <span className="rounded bg-indigo-100 px-1 text-[9px] uppercase text-indigo-700 dark:bg-indigo-950 dark:text-indigo-300">standing</span>}
                        <span className={`ml-auto text-[10px] ${paused ? "font-medium text-amber-600 dark:text-amber-400" : style.text}`}>{paused?.label ?? a.status}</span>
                      </span>
                      <span className={`block truncate ${paused ? "text-amber-700 dark:text-amber-300" : "text-zinc-500"}`} title={paused?.detail}>
                        {paused?.detail ?? a.current_task ?? a.goal}
                      </span>
                      <span className="block text-[10px] text-zinc-400">{a.tokens_used.toLocaleString()} tokens · ${a.cost_usd.toFixed(4)}</span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        {tab === "files" && <FilesPanel workspace={workspace} files={files} />}

        {tab === "triggers" && <Triggers workspace={workspace} onChanged={onChanged} />}

        {tab === "integrations" && <IntegrationsPanel workspaceId={workspace.workspace_id} onChanged={onChanged} />}

        {tab === "safety" && <SafetyPanel workspace={workspace} onChanged={onChanged} />}

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

  const field = "w-full rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900";

  return (
    <div className="space-y-3 p-3 text-xs">
      {workspace.triggers.length === 0 && <div className="text-zinc-500">No triggers yet. Agents create their own, or add one here.</div>}
      {workspace.triggers.map((t) => (
        <div key={t.trigger_id} className="rounded border border-zinc-200 p-2 dark:border-zinc-800">
          <div className="flex items-center justify-between gap-2">
            <span className="font-semibold">{t.kind === "Webhook" ? "🔗" : t.kind === "Watch" ? "👁" : "⏰"} {t.name}</span>
            <button onClick={() => deleteWorkspaceTrigger(workspace.workspace_id, t.trigger_id).then(onChanged)} className="text-[10px] text-rose-600 hover:underline">delete</button>
          </div>
          <div className="text-zinc-500">{describeSchedule(t)} → {agentName(t.target_agent_id)} · by {t.created_by === "user" ? "you" : agentName(t.created_by)}</div>
          {t.watch_summary && (
            <div className="mt-0.5 font-mono text-[10px] text-zinc-600 dark:text-zinc-400">{t.watch_summary}</div>
          )}
          {t.kind === "Watch" && (
            <div className="mt-0.5 text-[10px] text-emerald-700 dark:text-emerald-400">
              {t.checks} checks without the LLM · {t.alerts} alert{t.alerts === 1 ? "" : "s"} · matching now: {t.last_match_count}
            </div>
          )}
          {t.last_error && <div className="mt-0.5 text-[10px] text-amber-700 dark:text-amber-300">Last error: {t.last_error}</div>}
          {t.instruction && <div className="mt-0.5 text-zinc-600 dark:text-zinc-400">“{t.instruction}”</div>}
          <div className="mt-0.5 text-[10px] text-zinc-400">
            fired {t.fire_count}×{t.last_fired_at && ` · last ${new Date(t.last_fired_at).toLocaleTimeString()}`}
            {t.next_due_at && t.kind === "Schedule" && ` · next ${new Date(t.next_due_at).toLocaleString()}`}
            {t.dropped_count > 0 && ` · ${t.dropped_count} dropped (rate limit)`}
          </div>
        </div>
      ))}

      <form onSubmit={add} className="space-y-2 rounded border border-dashed border-zinc-300 p-2 dark:border-zinc-700">
        <div className="flex gap-1">
          {(["schedule", "webhook"] as const).map((k) => (
            <button type="button" key={k} onClick={() => setKind(k)} className={`rounded px-2 py-0.5 capitalize ${kind === k ? "bg-zinc-800 text-white dark:bg-zinc-100 dark:text-zinc-900" : "text-zinc-500"}`}>{k}</button>
          ))}
        </div>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" className={field} />
        <textarea value={instruction} onChange={(e) => setInstruction(e.target.value)} rows={2} placeholder="What the agent should do each time" className={field} />
        {kind === "schedule" && (
          <div className="grid grid-cols-2 gap-2">
            <label className="flex items-center gap-1 whitespace-nowrap">
              every
              <input type="number" min={1} value={minutes} onChange={(e) => setMinutes(Number(e.target.value) || 1)}
                className="w-16 rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900" />
              min
            </label>
            <input value={cron} onChange={(e) => setCron(e.target.value)} placeholder="or cron, e.g. 0 9 * * 1-5" className={field} />
          </div>
        )}
        <select value={target} onChange={(e) => setTarget(e.target.value)} className={field}>
          {workspace.agents.map((a) => <option key={a.agent_id} value={a.agent_id}>{a.role}</option>)}
        </select>
        {error && <div className="text-rose-600">{error}</div>}
        <button type="submit" className="rounded bg-brand-500 px-3 py-1 font-medium text-white hover:bg-brand-600">Add {kind}</button>
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
