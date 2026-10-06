"use client";

import Link from "next/link";
import { useState } from "react";
import { API_BASE, addWorkspaceTrigger, apiErrorMessage, deleteWorkspaceTrigger, getWorkspaceFiles } from "@/lib/api";
import type { AgentStatus, RuntimeEvent } from "@/lib/types";
import type { TriggerView, WorkspaceFile, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { STATUS_STYLES } from "@/lib/status";
import { useLiveList } from "@/lib/useLiveList";
import { BotIcon } from "../BotIcon";
import { MentionTextarea, stageMentionables } from "@/components/ui/MentionTextarea";
import { EventStream } from "../EventStream";
import { FilesPanel } from "./FilesPanel";
import { WorkspaceChat } from "./WorkspaceChat";
import { pauseInfo } from "./pauseInfo";
import { IntegrationsPanel } from "./IntegrationsPanel";
import { SafetyPanel } from "./SafetyPanel";

type Tab = "chat" | "agents" | "files" | "knowhow" | "triggers" | "integrations" | "safety" | "events";

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

export function WorkspaceSidePanel({ workspace, events, onSelectAgent, onSelectRun, onChanged }: {
  workspace: WorkspaceSnapshot;
  events: RuntimeEvent[];
  onSelectAgent: (id: string) => void;
  onSelectRun: (runId: string) => void;
  onChanged: () => void;
}) {
  const [tab, setTab] = useState<Tab>("chat");
  // Reload the file list whenever an agent saves a file (and once on open).
  const fileWrites = events.filter((e) => e.type === "ArtifactCreated").length;
  const workspaceId = workspace.workspace_id;
  const files: WorkspaceFile[] = useLiveList(workspaceId, fileWrites, () => getWorkspaceFiles(workspaceId)) ?? [];


  const pendingApprovals = workspace.approvals?.filter((a) => a.status === "Pending").length ?? 0;
  const tabs: [Tab, string, number | null][] = [
    ["chat", "Chat", pendingApprovals || null],
    ["agents", "Agents", workspace.agents.length || null],
    ["files", "Files", files.length || null],
    ["knowhow", "Skills & knowledge", null],
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
        {tab === "chat" && <WorkspaceChat workspace={workspace} onSent={onChanged} onSelectRun={onSelectRun} />}

        {tab === "agents" && workspace.agents.length === 0 && (
          <div className="p-3 text-xs text-zinc-500">No run is in progress. The agents of runs in progress show here; finished runs keep theirs on the run&apos;s page.</div>
        )}
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

        {tab === "knowhow" && (
          <div className="space-y-3 p-3 text-xs">
            <p className="text-zinc-500">
              Give this workspace its own skills and knowledge. Only its agents use them, on top of what your whole organization
              shares; no other workspace or task sees them.
            </p>
            {[
              ["/skills", "Skills", "How this workspace's agents should do particular work (a SKILL.md each)."],
              ["/knowledge", "Knowledge", "Facts and documents (PDF, Word, Excel, slides…) its agents search."],
            ].map(([href, label, text]) => (
              <Link key={href} href={`${href}?workspace=${encodeURIComponent(workspace.workspace_id)}`}
                className="block rounded-lg border border-zinc-200 p-3 hover:border-brand-300 hover:bg-zinc-50 dark:border-zinc-800 dark:hover:border-brand-800 dark:hover:bg-zinc-900">
                <span className="font-semibold text-zinc-900 dark:text-zinc-100">{label} for this workspace →</span>
                <span className="mt-0.5 block text-zinc-500">{text}</span>
              </Link>
            ))}
          </div>
        )}

        {tab === "triggers" && <Triggers workspace={workspace} onChanged={onChanged} />}

        {tab === "integrations" && <IntegrationsPanel workspaceId={workspace.workspace_id} onChanged={onChanged} />}

        {tab === "safety" && <SafetyPanel workspace={workspace} onChanged={onChanged} />}

        {tab === "events" && <EventStream events={events} />}
      </div>
    </div>
  );
}

const WATCH_OPS = ["<", "<=", ">", ">=", "==", "!=", "contains", "not_contains", "exists", "not_exists"];

/** Read-only tools of the workspace's connections: the only kind a watch may call. */
function readOnlyTools(workspace: WorkspaceSnapshot) {
  return (workspace.connections ?? []).flatMap((c) => c.tools
    .filter((t) => t.enabled && t.side_effects === "ReadOnly")
    .map((t) => (t.name.includes("__") ? t.name : `${c.name}__${t.name}`)));
}

function Triggers({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const [kind, setKind] = useState<"schedule" | "webhook" | "watch">("schedule");
  const [name, setName] = useState("");
  const [instruction, setInstruction] = useState("");
  const [minutes, setMinutes] = useState(60);
  const [cron, setCron] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<(TriggerView & { dry_run?: { items_found: number; matching_now: number } }) | null>(null);
  // Watch
  const tools = readOnlyTools(workspace);
  const [sourceTool, setSourceTool] = useState("");
  const [sourceArgs, setSourceArgs] = useState("");
  const [itemsPath, setItemsPath] = useState("$");
  const [condition, setCondition] = useState({ field: "", op: "<", value: "" });
  const [keyField, setKeyField] = useState("");
  const [displayFields, setDisplayFields] = useState("");
  const [mode, setMode] = useState<"notify" | "run">("notify");
  const [message, setMessage] = useState("");
  const [urgency, setUrgency] = useState<"info" | "warning" | "urgent">("warning");

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      let args: Record<string, unknown> | undefined;
      if (kind === "watch" && sourceArgs.trim()) {
        try {
          args = JSON.parse(sourceArgs) as Record<string, unknown>;
        } catch {
          setError("The tool's arguments must be JSON, e.g. {\"path\": \"/todos\"}.");
          return;
        }
      }
      const watch = kind === "watch" ? {
        source_tool: sourceTool || tools[0],
        source_arguments: args,
        items_path: itemsPath.trim() || undefined,
        conditions: condition.field.trim() ? [{ field: condition.field.trim(), op: condition.op, value: condition.value }] : [],
        key_field: keyField.trim() || undefined,
        display_fields: displayFields.split(",").map((f) => f.trim()).filter(Boolean),
        mode,
        message: message.trim() || undefined,
        urgency,
      } : {};
      const t = await addWorkspaceTrigger(workspace.workspace_id, {
        kind,
        name: name || (kind === "webhook" ? "Webhook" : kind === "watch" ? "Watch" : "Schedule"),
        instruction,
        ...(kind !== "webhook" ? (cron.trim() ? { cron: cron.trim() } : { every_minutes: minutes }) : {}),
        ...watch,
      });
      setCreated(t);
      setName("");
      setInstruction("");
      onChanged();
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  const field = "w-full rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900";

  return (
    <div className="space-y-3 p-3 text-xs">
      <p className="text-zinc-500">Schedules and webhooks run the pipeline: the instruction (and a webhook&apos;s payload) is the run&apos;s input. Watches check a connected service without the model, and alert you or run the pipeline when something matches.</p>
      {workspace.triggers.length === 0 && <div className="text-zinc-500">No triggers yet.</div>}
      {workspace.triggers.map((t) => (
        <div key={t.trigger_id} className="rounded border border-zinc-200 p-2 dark:border-zinc-800">
          <div className="flex items-center justify-between gap-2">
            <span className="font-semibold">{t.kind === "Webhook" ? "🔗" : t.kind === "Watch" ? "👁" : "⏰"} {t.name}</span>
            <button onClick={() => deleteWorkspaceTrigger(workspace.workspace_id, t.trigger_id).then(onChanged)} className="text-[10px] text-rose-600 hover:underline">delete</button>
          </div>
          <div className="text-zinc-500">{describeSchedule(t)}{t.kind !== "Watch" && " → runs the pipeline"}</div>
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
            {t.next_due_at && t.kind !== "Webhook" && ` · next ${new Date(t.next_due_at).toLocaleString()}`}
            {t.dropped_count > 0 && ` · ${t.dropped_count} dropped`}
          </div>
        </div>
      ))}

      <form onSubmit={add} className="space-y-2 rounded border border-dashed border-zinc-300 p-2 dark:border-zinc-700">
        <div className="flex gap-1">
          {(["schedule", "webhook", "watch"] as const).map((k) => (
            <button type="button" key={k} onClick={() => setKind(k)} className={`rounded px-2 py-0.5 capitalize ${kind === k ? "bg-zinc-800 text-white dark:bg-zinc-100 dark:text-zinc-900" : "text-zinc-500"}`}>{k}</button>
          ))}
        </div>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" className={field} />
        {kind === "watch" && (
          tools.length === 0 ? (
            <p className="rounded bg-amber-50 p-2 text-amber-800 dark:bg-amber-950 dark:text-amber-200">Connect a service on the Integrations tab first: a watch calls one of its read-only tools.</p>
          ) : (
            <div className="space-y-2">
              <select value={sourceTool || tools[0]} onChange={(e) => setSourceTool(e.target.value)} className={field} title="The read-only tool the watch calls">
                {tools.map((t) => <option key={t} value={t}>{t}</option>)}
              </select>
              <input value={sourceArgs} onChange={(e) => setSourceArgs(e.target.value)} placeholder='Arguments (JSON), e.g. {"path": "/todos?userId=1"}' className={`${field} font-mono`} />
              <input value={itemsPath} onChange={(e) => setItemsPath(e.target.value)} placeholder="Items path, e.g. $.body.items[*]" className={`${field} font-mono`} />
              <div className="grid grid-cols-[1fr_auto_1fr] gap-1">
                <input value={condition.field} onChange={(e) => setCondition({ ...condition, field: e.target.value })} placeholder="field, e.g. qty" className={`${field} font-mono`} />
                <select value={condition.op} onChange={(e) => setCondition({ ...condition, op: e.target.value })} className="rounded border border-zinc-300 bg-white px-1 dark:border-zinc-700 dark:bg-zinc-900">
                  {WATCH_OPS.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
                <input value={condition.value} onChange={(e) => setCondition({ ...condition, value: e.target.value })} placeholder="value, e.g. 10" disabled={condition.op.includes("exists")} className={`${field} font-mono`} />
              </div>
              <div className="grid grid-cols-2 gap-1">
                <input value={keyField} onChange={(e) => setKeyField(e.target.value)} placeholder="Key field, e.g. sku" className={`${field} font-mono`} />
                <input value={displayFields} onChange={(e) => setDisplayFields(e.target.value)} placeholder="Show fields: sku, qty" className={`${field} font-mono`} />
              </div>
              <div className="flex gap-1">
                {(["notify", "run"] as const).map((m) => (
                  <button type="button" key={m} onClick={() => setMode(m)} className={`rounded px-2 py-0.5 ${mode === m ? "bg-zinc-800 text-white dark:bg-zinc-100 dark:text-zinc-900" : "text-zinc-500"}`}>
                    {m === "notify" ? "Alert me" : "Run the pipeline"}
                  </button>
                ))}
                <select value={urgency} onChange={(e) => setUrgency(e.target.value as typeof urgency)} className="ml-auto rounded border border-zinc-300 bg-white px-1 dark:border-zinc-700 dark:bg-zinc-900">
                  <option value="info">info</option>
                  <option value="warning">warning</option>
                  <option value="urgent">urgent</option>
                </select>
              </div>
              {mode === "notify" && <input value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Message, e.g. Low stock: {items}" className={field} />}
            </div>
          )
        )}
        {(kind !== "watch" || mode === "run") && (
          <MentionTextarea value={instruction} onValueChange={setInstruction} rows={2} mentionables={stageMentionables(workspace.pipeline?.stages ?? [])}
            placeholder={kind === "watch" ? "What the run should do with the matches" : "The run's input each time, e.g. Summarise yesterday's support tickets"} className={`${field} block`} />
        )}
        {kind !== "webhook" && (
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
        {error && <div className="text-rose-600">{error}</div>}
        <button type="submit" disabled={kind === "watch" && tools.length === 0} className="rounded bg-brand-500 px-3 py-1 font-medium text-white hover:bg-brand-600 disabled:opacity-50">Add {kind}</button>
        {created?.webhook_path && (
          <div className="rounded bg-amber-50 p-2 text-amber-900 dark:bg-amber-950 dark:text-amber-100">
            Point your service at this URL (POST). Keep it secret; it&apos;s also in the chat:
            <code className="mt-1 block break-all text-[10px]">{API_BASE}{created.webhook_path}</code>
          </div>
        )}
        {created?.dry_run && (
          <div className="rounded bg-emerald-50 p-2 text-emerald-900 dark:bg-emerald-950 dark:text-emerald-100">
            Checked once: {created.dry_run.items_found} item(s) found, {created.dry_run.matching_now} matching now.
          </div>
        )}
      </form>
    </div>
  );
}
