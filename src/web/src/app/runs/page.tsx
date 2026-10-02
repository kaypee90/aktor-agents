"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { listTasks, type TaskListItem } from "@/lib/api";
import { Badge, Button, Card, EmptyState, PageHeader, Stat, StatusBadge, ago, compact, inlineInputClass, inputClass, money } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const SOURCES = ["all", "api", "mcp", "a2a", "acp", "replay"] as const;
const STATES = ["all", "running", "completed", "failed"] as const;

function stateOf(t: TaskListItem) {
  if (!t.completed_at) return "running";
  return ["Failed", "TimedOut", "Rejected"].includes(t.status) ? "failed" : "completed";
}

/** Every task the organization has run: where it came from, what it cost, and how to dig in. */
export default function RunsPage() {
  const [tasks, setTasks] = useState<TaskListItem[] | null>(null);
  const [query, setQuery] = useState("");
  const [source, setSource] = useState<(typeof SOURCES)[number]>("all");
  const [state, setState] = useState<(typeof STATES)[number]>("all");

  useEffect(() => {
    const load = () => listTasks(300).then(setTasks).catch(() => setTasks([]));
    load();
    const interval = setInterval(load, 5000);
    return () => clearInterval(interval);
  }, []);

  const shown = useMemo(() => (tasks ?? []).filter((t) =>
    (source === "all" || t.source === source) &&
    (state === "all" || stateOf(t) === state) &&
    (!query || `${t.goal} ${t.correlation_id ?? ""} ${t.task_id}`.toLowerCase().includes(query.toLowerCase()))), [tasks, query, source, state]);

  const totals = useMemo(() => {
    const list = tasks ?? [];
    const done = list.filter((t) => t.completed_at);
    return {
      runs: list.length,
      completion: done.length === 0 ? null : done.filter((t) => stateOf(t) === "completed").length / done.length,
      cost: list.reduce((n, t) => n + t.cost_usd, 0),
      tokens: list.reduce((n, t) => n + t.tokens_used, 0),
    };
  }, [tasks]);

  return (
    <div>
      <PageHeader
        title="Run history"
        description="Every task your organization has run, from the dashboard, the API, MCP, A2A or ACP. Open one to see its agent tree, or its journal to step through, replay or fork it."
        actions={<Link href="/"><Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />}>New task</Button></Link>}
      />
      <div className="mx-auto max-w-6xl space-y-5 px-6 py-6">
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <Stat label="Runs" value={totals.runs} />
          <Stat label="Completed" value={totals.completion === null ? "—" : `${Math.round(totals.completion * 100)}%`} hint="of finished runs" />
          <Stat label="Tokens" value={compact(totals.tokens)} />
          <Stat label="Spend" value={money(totals.cost)} />
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-60 flex-1">
            <Icons.Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-zinc-400" />
            <input className={`${inputClass} pl-9`} placeholder="Search goal, correlation id or task id" value={query} onChange={(e) => setQuery(e.target.value)} />
          </div>
          <select className={inlineInputClass} value={state} onChange={(e) => setState(e.target.value as typeof state)}>
            {STATES.map((s) => <option key={s} value={s}>{s === "all" ? "Any status" : s[0].toUpperCase() + s.slice(1)}</option>)}
          </select>
          <select className={inlineInputClass} value={source} onChange={(e) => setSource(e.target.value as typeof source)}>
            {SOURCES.map((s) => <option key={s} value={s}>{s === "all" ? "Any source" : s.toUpperCase()}</option>)}
          </select>
        </div>

        {tasks === null ? (
          <div className="h-48 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />
        ) : shown.length === 0 ? (
          <EmptyState icon={<Icons.Runs className="h-5 w-5" />} title={tasks.length === 0 ? "No runs yet" : "No runs match"}
            description={tasks.length === 0 ? "Tasks you start appear here." : "Try another search or filter."} />
        ) : (
          <Card className="overflow-hidden">
            <table className="w-full text-sm">
              <thead className="border-b border-zinc-200 bg-zinc-50 text-left text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800 dark:bg-zinc-950">
                <tr>
                  <th className="px-4 py-2.5 font-medium">Goal</th>
                  <th className="px-3 py-2.5 font-medium">Status</th>
                  <th className="hidden px-3 py-2.5 font-medium md:table-cell">Source</th>
                  <th className="hidden px-3 py-2.5 text-right font-medium sm:table-cell">Agents</th>
                  <th className="px-3 py-2.5 text-right font-medium">Cost</th>
                  <th className="hidden px-3 py-2.5 font-medium lg:table-cell">Started</th>
                  <th className="px-3 py-2.5" />
                </tr>
              </thead>
              <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800/70">
                {shown.map((t) => (
                  <tr key={t.task_id} className="group hover:bg-zinc-50 dark:hover:bg-zinc-900/50">
                    <td className="max-w-md px-4 py-3">
                      <Link href={`/?task=${t.task_id}`} className="line-clamp-1 font-medium text-zinc-900 hover:text-brand-600 dark:text-zinc-100 dark:hover:text-brand-400">{t.goal}</Link>
                      <div className="mt-0.5 flex gap-2 font-mono text-[10px] text-zinc-400">
                        <span>{t.task_id.slice(0, 8)}</span>
                        {t.correlation_id && <span className="truncate">{t.correlation_id}</span>}
                        {t.replay_of_task_id && <Badge tone="blue">{t.replay_mode} replay</Badge>}
                      </div>
                    </td>
                    <td className="px-3 py-3"><StatusBadge status={t.completed_at ? t.status : "Running"} /></td>
                    <td className="hidden px-3 py-3 md:table-cell"><Badge>{t.source.toUpperCase()}</Badge></td>
                    <td className="hidden px-3 py-3 text-right tabular-nums text-zinc-600 sm:table-cell dark:text-zinc-400">{t.agents}</td>
                    <td className="px-3 py-3 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{money(t.cost_usd)}</td>
                    <td className="hidden px-3 py-3 text-xs text-zinc-500 lg:table-cell">{ago(t.created_at)}</td>
                    <td className="px-3 py-3 text-right">
                      <Link href={`/replay?task=${t.task_id}`} title="Journal, replay and diff"
                        className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-zinc-500 opacity-0 hover:bg-zinc-100 hover:text-zinc-800 group-hover:opacity-100 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
                        <Icons.Replay className="h-3.5 w-3.5" /> Journal
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        )}
      </div>
    </div>
  );
}
