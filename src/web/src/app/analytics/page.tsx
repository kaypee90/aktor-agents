"use client";

import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useMemo, useState } from "react";
import {
  Bar,
  BarChart,
  Brush,
  CartesianGrid,
  Cell,
  ComposedChart,
  Line,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { apiErrorMessage, getAnalytics, type Analytics, type AnalyticsFilter, type AnalyticsTaskRow } from "@/lib/api";
import { Button, Card, CardHeader, EmptyState, ErrorBanner, PageHeader, StatusBadge, Tabs, ago, compact, cx, inlineInputClass, inputClass, money } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const RANGES = [
  { id: "24h", label: "24 hours", hours: 24 },
  { id: "7d", label: "7 days", hours: 24 * 7 },
  { id: "30d", label: "30 days", hours: 24 * 30 },
  { id: "90d", label: "90 days", hours: 24 * 90 },
] as const;
const SOURCES = ["api", "mcp", "a2a", "acp", "replay"];
const PALETTE = ["#ff5a43", "#6366f1", "#14b8a6", "#f59e0b", "#ec4899", "#0ea5e9", "#84cc16", "#a855f7"];

type Metric = "cost_usd" | "tokens" | "runs" | "avg_duration_s";
const METRICS: { id: Metric; label: string }[] = [
  { id: "cost_usd", label: "Spend" },
  { id: "tokens", label: "Tokens" },
  { id: "runs", label: "Runs" },
  { id: "avg_duration_s", label: "Avg duration" },
];

const usd = (n: number) => (n === 0 ? "$0" : n < 0.01 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);
function duration(s: number | null | undefined) {
  if (s === null || s === undefined) return "—";
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m ${Math.round(s % 60)}s`;
  return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
}
/** The last day an exclusive end time covers, as a date input value. */
const dayBefore = (iso: string | null | undefined) => (iso ? new Date(new Date(iso).getTime() - 1).toISOString().slice(0, 10) : "");
const ms = (n: number | null) => (n === null ? "—" : n < 1000 ? `${Math.round(n)} ms` : duration(n / 1000));
const formatMetric = (m: Metric, v: number | null) =>
  m === "cost_usd" ? usd(v ?? 0) : m === "tokens" ? compact(v ?? 0) : m === "avg_duration_s" ? duration(v) : String(v ?? 0);

/** Follows the dashboard's light/dark class, so chart strokes match the theme. */
function useIsDark() {
  const [dark, setDark] = useState(false);
  useEffect(() => {
    const read = () => setDark(document.documentElement.classList.contains("dark"));
    read();
    const observer = new MutationObserver(read);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    return () => observer.disconnect();
  }, []);
  return dark;
}

function ChartTooltip({ active, payload, label, format }: {
  active?: boolean; payload?: { name?: string; value?: number; color?: string; dataKey?: string }[]; label?: string;
  format: (key: string, v: number) => string;
}) {
  if (!active || !payload?.length) return null;
  return (
    <div className="rounded-lg border border-zinc-200 bg-white px-3 py-2 text-xs shadow-lg dark:border-zinc-700 dark:bg-zinc-900">
      {label && <div className="mb-1 font-medium text-zinc-900 dark:text-zinc-100">{label}</div>}
      {payload.map((p) => (
        <div key={p.dataKey ?? p.name} className="flex items-center gap-2 text-zinc-600 dark:text-zinc-300">
          <span className="h-2 w-2 rounded-full" style={{ background: p.color }} />
          <span>{p.name}</span>
          <span className="ml-auto pl-3 font-medium tabular-nums text-zinc-900 dark:text-zinc-100">{format(p.dataKey ?? "", p.value ?? 0)}</span>
        </div>
      ))}
    </div>
  );
}

function Delta({ now, before, inverse }: { now: number | null; before: number | null; inverse?: boolean }) {
  if (now === null || before === null || before === 0) return null;
  const change = (now - before) / before;
  if (Math.abs(change) < 0.005) return <span className="text-zinc-400">no change</span>;
  const up = change > 0;
  const good = inverse ? !up : up;
  return (
    <span className={good ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}>
      {up ? "▲" : "▼"} {Math.abs(change * 100).toFixed(0)}% vs previous
    </span>
  );
}

function Kpi({ label, value, hint }: { label: string; value: React.ReactNode; hint?: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-zinc-200 bg-white px-4 py-3 dark:border-zinc-800 dark:bg-zinc-900/60">
      <div className="text-[11px] font-medium uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="mt-1 text-xl font-semibold tabular-nums text-zinc-900 dark:text-zinc-50">{value}</div>
      <div className="mt-0.5 min-h-4 text-[11px] text-zinc-500">{hint}</div>
    </div>
  );
}

export default function AnalyticsPage() {
  return <Suspense><AnalyticsView /></Suspense>;
}

/** Where tokens and money go, and what takes long: runs, agents and tools, with filters kept in the URL. */
function AnalyticsView() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const dark = useIsDark();

  const range = params.get("range") ?? (params.get("from") ? "custom" : "7d");
  const filter: AnalyticsFilter = useMemo(() => {
    const preset = RANGES.some((r) => r.id === range);
    return {
      range: preset ? range : null,
      from: preset ? null : params.get("from"),
      to: preset ? null : params.get("to"),
      source: params.get("source"),
      status: params.get("status") as AnalyticsFilter["status"],
      q: params.get("q"),
    };
  }, [params, range]);

  const [data, setData] = useState<{ key: string; value: Analytics } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<Metric>("cost_usd");
  const [roleMetric, setRoleMetric] = useState<"tokens" | "cost_usd">("tokens");
  const [toolMetric, setToolMetric] = useState<"total_duration_ms" | "avg_duration_ms" | "calls" | "failures">("total_duration_ms");
  const [runsTab, setRunsTab] = useState<"cost" | "slow">("cost");
  const [query, setQuery] = useState(params.get("q") ?? "");
  const key = params.toString();

  useEffect(() => {
    let cancelled = false;
    getAnalytics(filter)
      .then((value) => { if (!cancelled) { setData({ key, value }); setError(null); } })
      .catch((e) => { if (!cancelled) setError(apiErrorMessage(e)); });
    return () => { cancelled = true; };
  }, [filter, key]);

  function update(patch: Record<string, string | null>) {
    const next = new URLSearchParams(params.toString());
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  }

  // Search a moment after the last keystroke.
  useEffect(() => {
    if ((params.get("q") ?? "") === query) return;
    const timer = setTimeout(() => update({ q: query.trim() || null }), 400);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query]);

  const a = data?.value ?? null;
  const loading = data?.key !== key;
  const grid = dark ? "#27272a" : "#e4e4e7";
  const axis = dark ? "#71717a" : "#a1a1aa";
  const bucketLabel = (t: string) => {
    const d = new Date(t);
    return a?.range.bucket === "hour"
      ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
      : d.toLocaleDateString([], { month: "short", day: "numeric" });
  };

  /** Clicking a bucket narrows the whole page to it. */
  function drillInto(t: string) {
    if (!a) return;
    const start = new Date(t);
    const end = new Date(start.getTime() + (a.range.bucket === "hour" ? 3600_000 : 86_400_000));
    update({ range: null, from: start.toISOString(), to: end.toISOString() });
  }

  const series = (a?.series ?? []).map((p) => ({ ...p, label: bucketLabel(p.t) }));
  const roles = [...(a?.by_role ?? [])].sort((x, y) => y[roleMetric] - x[roleMetric]).slice(0, 10);
  const tools = [...(a?.by_tool ?? [])].sort((x, y) => (y[toolMetric] ?? 0) - (x[toolMetric] ?? 0)).slice(0, 10);
  const runs: AnalyticsTaskRow[] = runsTab === "cost" ? a?.top_by_cost ?? [] : a?.slowest ?? [];
  const tokensTotal = a?.totals.tokens ?? 0;

  return (
    <div>
      <PageHeader
        title="Analytics"
        description="Where your tokens and money go, what takes long, and how runs trend. Click a bar or a point to drill in."
        actions={<Link href="/runs"><Button icon={<Icons.Runs className="h-3.5 w-3.5" />}>Run history</Button></Link>}
      />

      <div className="mx-auto max-w-7xl space-y-5 px-6 py-6">
        {/* Filters */}
        <div className="flex flex-wrap items-center gap-2">
          <div className="inline-flex rounded-lg border border-zinc-200 bg-white p-0.5 text-xs dark:border-zinc-800 dark:bg-zinc-950">
            {RANGES.map((r) => (
              <button key={r.id} onClick={() => update({ range: r.id, from: null, to: null })}
                className={cx("rounded-md px-2.5 py-1.5 font-medium transition",
                  range === r.id ? "bg-zinc-900 text-white dark:bg-zinc-100 dark:text-zinc-900" : "text-zinc-600 hover:text-zinc-900 dark:text-zinc-400 dark:hover:text-zinc-100")}>
                {r.label}
              </button>
            ))}
          </div>
          <input type="date" className={cx(inlineInputClass, "py-1.5 text-xs")} title="From"
            value={(filter.from ?? a?.range.from)?.slice(0, 10) ?? ""}
            onChange={(e) => update({ range: null, from: e.target.value ? new Date(e.target.value).toISOString() : null, to: filter.to ?? a?.range.to ?? null })} />
          <span className="text-xs text-zinc-400">to</span>
          <input type="date" className={cx(inlineInputClass, "py-1.5 text-xs")} title="To"
            value={dayBefore(filter.to ?? a?.range.to)}
            onChange={(e) => update({ range: null, from: filter.from ?? a?.range.from ?? null, to: e.target.value ? new Date(new Date(e.target.value).getTime() + 86_400_000).toISOString() : null })} />
          <select className={cx(inlineInputClass, "py-1.5 text-xs")} value={params.get("source") ?? ""} onChange={(e) => update({ source: e.target.value || null })}>
            <option value="">Any source</option>
            {SOURCES.map((s) => <option key={s} value={s}>{s.toUpperCase()}</option>)}
          </select>
          <select className={cx(inlineInputClass, "py-1.5 text-xs")} value={params.get("status") ?? ""} onChange={(e) => update({ status: e.target.value || null })}>
            <option value="">Any status</option>
            <option value="completed">Completed</option>
            <option value="failed">Failed</option>
            <option value="running">Running</option>
          </select>
          <div className="relative min-w-48 flex-1">
            <Icons.Search className="pointer-events-none absolute left-3 top-2.5 h-3.5 w-3.5 text-zinc-400" />
            <input className={cx(inputClass, "py-1.5 pl-8 text-xs")} placeholder="Filter by goal" value={query} onChange={(e) => setQuery(e.target.value)} />
          </div>
          {(params.get("source") || params.get("status") || params.get("q") || range === "custom") && (
            <Button size="sm" variant="ghost" onClick={() => { setQuery(""); router.replace(pathname); }}>Clear</Button>
          )}
        </div>

        <ErrorBanner error={error} onClose={() => setError(null)} />

        {!a ? (
          <div className="grid gap-4 md:grid-cols-3">{[0, 1, 2, 3, 4, 5].map((i) => <div key={i} className="h-28 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />)}</div>
        ) : a.totals.runs === 0 ? (
          <EmptyState icon={<Icons.Analytics className="h-5 w-5" />} title="No runs in this range"
            description="Widen the date range or clear the filters." />
        ) : (
          <div className={cx("space-y-5 transition-opacity", loading && "opacity-60")}>
            {/* KPIs */}
            <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
              <Kpi label="Spend" value={usd(a.totals.cost_usd)} hint={<Delta now={a.totals.cost_usd} before={a.previous.cost_usd} inverse />} />
              <Kpi label="Tokens" value={compact(a.totals.tokens)} hint={<Delta now={a.totals.tokens} before={a.previous.tokens} inverse />} />
              <Kpi label="Runs" value={a.totals.runs}
                hint={`${a.totals.completed} done · ${a.totals.failed} failed${a.totals.running ? ` · ${a.totals.running} running` : ""}`} />
              <Kpi label="Avg spend / run" value={usd(a.totals.avg_cost_usd)} hint={<Delta now={a.totals.avg_cost_usd} before={a.previous.avg_cost_usd} inverse />} />
              <Kpi label="Avg tokens / run" value={compact(Math.round(a.totals.avg_tokens))} hint={`${a.totals.agents} agents in all`} />
              <Kpi label="Median duration" value={duration(a.totals.p50_duration_s)} hint={`p95 ${duration(a.totals.p95_duration_s)}`} />
            </div>

            {/* Trend */}
            <Card>
              <CardHeader title="Over time" description={`Per ${a.range.bucket}. Click a bar to zoom into it; drag the handles below to focus.`}
                actions={<Tabs value={metric} onChange={setMetric} tabs={METRICS.map((m) => ({ id: m.id, label: m.label }))} className="border-0" />} />
              <div className="h-72 px-2 pb-2 pt-4">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={series} margin={{ left: 8, right: 16 }}>
                    <CartesianGrid stroke={grid} vertical={false} />
                    <XAxis dataKey="label" stroke={axis} fontSize={11} tickLine={false} axisLine={false} minTickGap={24} />
                    <YAxis stroke={axis} fontSize={11} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => formatMetric(metric, v)} />
                    <Tooltip cursor={{ fill: dark ? "#ffffff0d" : "#0000000a" }}
                      content={<ChartTooltip format={(k, v) => formatMetric(k as Metric, v)} />} />
                    <Bar isAnimationActive={false} dataKey={metric} name={METRICS.find((m) => m.id === metric)!.label} fill="#ff5a43" radius={[4, 4, 0, 0]} maxBarSize={36}
                      cursor="pointer" onClick={(d) => drillInto((d as unknown as { payload: { t: string } }).payload.t)} />
                    {metric !== "runs" && (
                      <Line isAnimationActive={false} dataKey="runs" name="Runs" yAxisId="runs" stroke="#6366f1" strokeWidth={2} dot={false} type="monotone" />
                    )}
                    {metric !== "runs" && <YAxis yAxisId="runs" orientation="right" hide />}
                    {series.length > 12 && <Brush dataKey="label" height={22} stroke={axis} fill={dark ? "#09090b" : "#fafafa"} travellerWidth={8} />}
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            </Card>

            <div className="grid gap-5 lg:grid-cols-5">
              {/* What consumes tokens */}
              <Card className="lg:col-span-3">
                <CardHeader title="What consumes the most" description="By agent role, across every run in range."
                  actions={<Tabs value={roleMetric} onChange={setRoleMetric} className="border-0"
                    tabs={[{ id: "tokens", label: "Tokens" }, { id: "cost_usd", label: "Spend" }]} />} />
                <div className="px-2 pb-3 pt-3" style={{ height: Math.max(160, roles.length * 34 + 20) }}>
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={roles} layout="vertical" margin={{ left: 8, right: 56 }}>
                      <CartesianGrid stroke={grid} horizontal={false} />
                      <XAxis type="number" stroke={axis} fontSize={11} tickLine={false} axisLine={false}
                        tickFormatter={(v: number) => (roleMetric === "tokens" ? compact(v) : usd(v))} />
                      <YAxis type="category" dataKey="role" stroke={axis} fontSize={11} tickLine={false} axisLine={false} width={150}
                        tickFormatter={(r: string) => (r.length > 22 ? r.slice(0, 21) + "…" : r)} />
                      <Tooltip cursor={{ fill: dark ? "#ffffff0d" : "#0000000a" }}
                        content={<ChartTooltip format={(k, v) => (k === "cost_usd" ? usd(v) : compact(v))} />} />
                      <Bar isAnimationActive={false} dataKey={roleMetric} name={roleMetric === "tokens" ? "Tokens" : "Spend"} radius={[0, 4, 4, 0]} maxBarSize={22}
                        label={{ position: "right", fontSize: 10, fill: axis, formatter: (v: unknown) => roleMetric === "tokens" && tokensTotal ? `${Math.round((Number(v) / tokensTotal) * 100)}%` : "" }}>
                        {roles.map((r, i) => <Cell key={r.role} fill={PALETTE[i % PALETTE.length]} />)}
                      </Bar>
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              </Card>

              {/* Where runs come from */}
              <Card className="lg:col-span-2">
                <CardHeader title="Spend by source" description="Click a slice to filter by it." />
                <div className="flex h-64 items-center">
                  <ResponsiveContainer width="55%" height="100%">
                    <PieChart>
                      <Pie isAnimationActive={false} data={a.by_source} dataKey="cost_usd" nameKey="source" innerRadius="55%" outerRadius="85%" paddingAngle={2} stroke="none"
                        cursor="pointer" onClick={(d) => update({ source: (d as unknown as { source: string }).source })}>
                        {a.by_source.map((s, i) => <Cell key={s.source} fill={PALETTE[i % PALETTE.length]} />)}
                      </Pie>
                      <Tooltip content={<ChartTooltip format={(_, v) => usd(v)} />} />
                    </PieChart>
                  </ResponsiveContainer>
                  <ul className="flex-1 space-y-1.5 pr-5 text-xs">
                    {a.by_source.map((s, i) => (
                      <li key={s.source}>
                        <button className="flex w-full items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-zinc-50 dark:hover:bg-zinc-800/60" onClick={() => update({ source: s.source })}>
                          <span className="h-2.5 w-2.5 rounded-sm" style={{ background: PALETTE[i % PALETTE.length] }} />
                          <span className="font-medium uppercase text-zinc-700 dark:text-zinc-300">{s.source}</span>
                          <span className="ml-auto tabular-nums text-zinc-500">{usd(s.cost_usd)} · {s.runs}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                </div>
              </Card>
            </div>

            <div className="grid gap-5 lg:grid-cols-2">
              {/* Tools: what takes long */}
              <Card>
                <CardHeader title="Tools" description="Which tools agents wait on, and which fail."
                  actions={<select className={cx(inlineInputClass, "py-1 text-xs")} value={toolMetric} onChange={(e) => setToolMetric(e.target.value as typeof toolMetric)}>
                    <option value="total_duration_ms">Total time</option>
                    <option value="avg_duration_ms">Average time</option>
                    <option value="calls">Calls</option>
                    <option value="failures">Failures</option>
                  </select>} />
                {tools.length === 0 ? (
                  <div className="p-5 text-sm text-zinc-500">No tool calls in this range.</div>
                ) : (
                  <div className="px-2 pb-3 pt-3" style={{ height: Math.max(160, tools.length * 32 + 20) }}>
                    <ResponsiveContainer width="100%" height="100%">
                      <BarChart data={tools} layout="vertical" margin={{ left: 8, right: 24 }}>
                        <CartesianGrid stroke={grid} horizontal={false} />
                        <XAxis type="number" stroke={axis} fontSize={11} tickLine={false} axisLine={false}
                          tickFormatter={(v: number) => (toolMetric.endsWith("_ms") ? ms(v) : String(v))} />
                        <YAxis type="category" dataKey="tool" stroke={axis} fontSize={11} tickLine={false} axisLine={false} width={130} />
                        <Tooltip cursor={{ fill: dark ? "#ffffff0d" : "#0000000a" }}
                          content={<ChartTooltip format={(k, v) => (k.endsWith("_ms") ? ms(v) : String(v))} />} />
                        <Bar isAnimationActive={false} dataKey={toolMetric} name={toolMetric === "calls" ? "Calls" : toolMetric === "failures" ? "Failures" : toolMetric === "avg_duration_ms" ? "Average" : "Total time"}
                          fill={toolMetric === "failures" ? "#f43f5e" : "#14b8a6"} radius={[0, 4, 4, 0]} maxBarSize={20} />
                      </BarChart>
                    </ResponsiveContainer>
                  </div>
                )}
              </Card>

              {/* How long runs take */}
              <Card>
                <CardHeader title="How long runs take" description={`Finished runs by duration. Average ${duration(a.totals.avg_duration_s)}.`} />
                <div className="h-64 px-2 pb-3 pt-4">
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={a.duration_histogram} margin={{ left: 0, right: 16 }}>
                      <CartesianGrid stroke={grid} vertical={false} />
                      <XAxis dataKey="label" stroke={axis} fontSize={11} tickLine={false} axisLine={false} />
                      <YAxis stroke={axis} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} width={32} />
                      <Tooltip cursor={{ fill: dark ? "#ffffff0d" : "#0000000a" }} content={<ChartTooltip format={(_, v) => `${v} runs`} />} />
                      <Bar isAnimationActive={false} dataKey="runs" name="Runs" fill="#6366f1" radius={[4, 4, 0, 0]} maxBarSize={48} />
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              </Card>
            </div>

            {/* The runs behind the numbers */}
            <Card>
              <CardHeader title="Runs to look at" description="The most expensive and the slowest runs in range."
                actions={<Tabs value={runsTab} onChange={setRunsTab} className="border-0"
                  tabs={[{ id: "cost", label: "Most expensive" }, { id: "slow", label: "Slowest" }]} />} />
              <table className="w-full text-sm">
                <thead className="border-b border-zinc-200 text-left text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
                  <tr>
                    <th className="px-5 py-2.5 font-medium">Goal</th>
                    <th className="px-3 py-2.5 font-medium">Status</th>
                    <th className="hidden px-3 py-2.5 text-right font-medium md:table-cell">Agents</th>
                    <th className="px-3 py-2.5 text-right font-medium">Tokens</th>
                    <th className="px-3 py-2.5 text-right font-medium">Spend</th>
                    <th className="px-3 py-2.5 text-right font-medium">Duration</th>
                    <th className="hidden px-5 py-2.5 font-medium lg:table-cell">Started</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800/70">
                  {runs.map((t) => (
                    <tr key={t.task_id} className="hover:bg-zinc-50 dark:hover:bg-zinc-900/50">
                      <td className="max-w-md px-5 py-2.5">
                        <Link href={`/?task=${t.task_id}`} className="line-clamp-1 font-medium text-zinc-900 hover:text-brand-600 dark:text-zinc-100 dark:hover:text-brand-400">{t.goal}</Link>
                      </td>
                      <td className="px-3 py-2.5"><StatusBadge status={t.status} /></td>
                      <td className="hidden px-3 py-2.5 text-right tabular-nums text-zinc-600 md:table-cell dark:text-zinc-400">{t.agents}</td>
                      <td className="px-3 py-2.5 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{compact(t.tokens)}</td>
                      <td className="px-3 py-2.5 text-right tabular-nums text-zinc-900 dark:text-zinc-100">{money(t.cost_usd)}</td>
                      <td className="px-3 py-2.5 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{duration(t.duration_s)}</td>
                      <td className="hidden px-5 py-2.5 text-xs text-zinc-500 lg:table-cell">{ago(t.created_at)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>
          </div>
        )}
      </div>
    </div>
  );
}
