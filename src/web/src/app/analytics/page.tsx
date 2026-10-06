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
import {
  apiErrorMessage,
  getAnalytics,
  getLlmSettings,
  listWorkspaces,
  modelChoices,
  type Analytics,
  type AnalyticsFilter,
  type AnalyticsModelRow,
  type AnalyticsTaskRow,
  type AnalyticsToolRow,
  type AnalyticsUserRow,
  type ModelChoice,
  type WorkspaceAnalytics,
} from "@/lib/api";
import { Button, Card, CardHeader, EmptyState, ErrorBanner, PageHeader, StatusBadge, Tabs, ago, compact, cx, inlineInputClass, inputClass, money } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const RANGES = [
  { id: "24h", label: "24 hours" },
  { id: "7d", label: "7 days" },
  { id: "30d", label: "30 days" },
  { id: "90d", label: "90 days" },
] as const;
const SOURCES = ["api", "mcp", "a2a", "acp", "replay"];
const PALETTE = ["#ff5a43", "#6366f1", "#14b8a6", "#f59e0b", "#ec4899", "#0ea5e9", "#84cc16", "#a855f7"];

const usd = (n: number) => (n === 0 ? "$0" : n < 0.01 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);
function duration(s: number | null | undefined) {
  if (s === null || s === undefined) return "—";
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m ${Math.round(s % 60)}s`;
  return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
}
/** The last day an exclusive end time covers, as a date input value. */
const dayBefore = (iso: string | null | undefined) => (iso ? new Date(new Date(iso).getTime() - 1).toISOString().slice(0, 10) : "");
const ms = (n: number | null | undefined) => (n === null || n === undefined ? "—" : n < 1000 ? `${Math.round(n)} ms` : duration(n / 1000));

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

/** Colours for chart strokes and the hover band, for the current theme. */
function useChartTheme() {
  const dark = useIsDark();
  return { dark, grid: dark ? "#27272a" : "#e4e4e7", axis: dark ? "#71717a" : "#a1a1aa", cursor: dark ? "#ffffff0d" : "#0000000a", brush: dark ? "#09090b" : "#fafafa" };
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

// ---------- Charts shared by both views ----------

type SeriesPoint = { t: string; label: string; [k: string]: number | string | null };

/** The trend: one bar per hour or day for the chosen metric, with runs or calls as a line. */
function TrendCard({ series, bucket, metrics, countKey, countLabel, onDrill }: {
  series: SeriesPoint[];
  bucket: "hour" | "day";
  metrics: { id: string; label: string; format: (v: number | null) => string }[];
  countKey: string;
  countLabel: string;
  onDrill: (t: string) => void;
}) {
  const theme = useChartTheme();
  const [metric, setMetric] = useState(metrics[0].id);
  const m = metrics.find((x) => x.id === metric) ?? metrics[0];
  return (
    <Card>
      <CardHeader title="Over time" description={`Per ${bucket}. Click a bar to zoom into it; drag the handles below to focus.`}
        actions={<Tabs value={metric} onChange={setMetric} tabs={metrics.map((x) => ({ id: x.id, label: x.label }))} className="border-0" />} />
      <div className="h-72 px-2 pb-2 pt-4">
        <ResponsiveContainer width="100%" height="100%">
          <ComposedChart data={series} margin={{ left: 8, right: 16 }}>
            <CartesianGrid stroke={theme.grid} vertical={false} />
            <XAxis dataKey="label" stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} minTickGap={24} />
            <YAxis stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => m.format(v)} />
            <Tooltip cursor={{ fill: theme.cursor }}
              content={<ChartTooltip format={(k, v) => (k === countKey ? String(v) : (metrics.find((x) => x.id === k)?.format(v) ?? String(v)))} />} />
            <Bar isAnimationActive={false} dataKey={m.id} name={m.label} fill="#ff5a43" radius={[4, 4, 0, 0]} maxBarSize={36}
              cursor="pointer" onClick={(d) => onDrill((d as unknown as { payload: { t: string } }).payload.t)} />
            {metric !== countKey && (
              <Line isAnimationActive={false} dataKey={countKey} name={countLabel} yAxisId="count" stroke="#6366f1" strokeWidth={2} dot={false} type="monotone" />
            )}
            {metric !== countKey && <YAxis yAxisId="count" orientation="right" hide />}
            {series.length > 12 && <Brush dataKey="label" height={22} stroke={theme.axis} fill={theme.brush} travellerWidth={8} />}
          </ComposedChart>
        </ResponsiveContainer>
      </div>
    </Card>
  );
}

/** Horizontal bars, one colour each, with an optional share label. */
type Row = Record<string, unknown>;

function RankedBars({ rows, nameKey, valueKey, format, nameWidth = 150, total, onPick, color }: {
  rows: Row[]; nameKey: string; valueKey: string; format: (v: number) => string; nameWidth?: number;
  total?: number; onPick?: (row: Row) => void; color?: string;
}) {
  const theme = useChartTheme();
  return (
    <div className="px-2 pb-3 pt-3" style={{ height: Math.max(140, rows.length * 34 + 30) }}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={rows} layout="vertical" margin={{ left: 8, right: total ? 48 : 24 }}>
          <CartesianGrid stroke={theme.grid} horizontal={false} />
          <XAxis type="number" stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(v: number) => format(v)} />
          <YAxis type="category" dataKey={nameKey} stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} width={nameWidth}
            tickFormatter={(r: string) => (r.length > 24 ? r.slice(0, 23) + "…" : r)} />
          <Tooltip cursor={{ fill: theme.cursor }} content={<ChartTooltip format={(_, v) => format(v)} />} />
          <Bar isAnimationActive={false} dataKey={valueKey} name={valueKey.replace(/_/g, " ")} radius={[0, 4, 4, 0]} maxBarSize={22}
            cursor={onPick ? "pointer" : undefined} onClick={onPick ? (d) => onPick((d as unknown as { payload: Row }).payload) : undefined}
            label={total ? { position: "right", fontSize: 10, fill: theme.axis, formatter: (v: unknown) => `${Math.round((Number(v) / total) * 100)}%` } : undefined}>
            {rows.map((r, i) => <Cell key={String(r[nameKey])} fill={color ?? PALETTE[i % PALETTE.length]} />)}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

type ModelMetric = "cost_usd" | "tokens" | "avg_cost_per_call_usd" | "avg_duration_ms";

/** Which model is cheaper or faster: spend, tokens, cost per call and response time per model. */
function ModelsCard({ rows, onPick }: { rows: AnalyticsModelRow[]; onPick: (profileId: string) => void }) {
  const [metric, setMetric] = useState<ModelMetric>("cost_usd");
  const sorted = [...rows].sort((a, b) => b[metric] - a[metric]).slice(0, 10);
  const format = (v: number) => (metric === "tokens" ? compact(v) : metric === "avg_duration_ms" ? ms(v) : usd(v));
  return (
    <Card>
      <CardHeader title="By model" description="Which model your agents used, what it cost and how fast it answered. Click a model to filter by it."
        actions={<select className={cx(inlineInputClass, "py-1 text-xs")} value={metric} onChange={(e) => setMetric(e.target.value as ModelMetric)}>
          <option value="cost_usd">Spend</option>
          <option value="tokens">Tokens</option>
          <option value="avg_cost_per_call_usd">Cost per call</option>
          <option value="avg_duration_ms">Response time</option>
        </select>} />
      {rows.length === 0 ? (
        <div className="p-5 text-sm text-zinc-500">No model calls recorded in this range yet. Calls are recorded from this release on.</div>
      ) : (
        <>
          <RankedBars rows={sorted as unknown as Row[]} nameKey="label" valueKey={metric} format={format} nameWidth={190} onPick={(r) => onPick(String(r.profile_id))} />
          <table className="w-full border-t border-zinc-100 text-xs dark:border-zinc-800">
            <thead className="text-left text-[10px] uppercase tracking-wide text-zinc-500">
              <tr>
                <th className="px-5 py-2 font-medium">Model</th>
                <th className="px-3 py-2 text-right font-medium">Calls</th>
                <th className="px-3 py-2 text-right font-medium">Tokens</th>
                <th className="px-3 py-2 text-right font-medium">Spend</th>
                <th className="px-3 py-2 text-right font-medium">Per call</th>
                <th className="px-5 py-2 text-right font-medium">Avg / p95 time</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800/70">
              {sorted.map((r) => (
                <tr key={`${r.profile_id}:${r.model}`} className="cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-900/50" onClick={() => onPick(r.profile_id)}>
                  <td className="px-5 py-2">
                    <div className="font-medium text-zinc-900 dark:text-zinc-100">{r.profile_name || r.model}</div>
                    <div className="font-mono text-[10px] text-zinc-500">{r.provider} · {r.model}</div>
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{r.calls}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{compact(r.tokens)}</td>
                  <td className="px-3 py-2 text-right tabular-nums text-zinc-900 dark:text-zinc-100">{usd(r.cost_usd)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{usd(r.avg_cost_per_call_usd)}</td>
                  <td className="px-5 py-2 text-right tabular-nums">{ms(r.avg_duration_ms)} / {ms(r.p95_duration_ms)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </Card>
  );
}

type ToolMetric = "total_duration_ms" | "avg_duration_ms" | "calls" | "failures";

function ToolsCard({ rows }: { rows: AnalyticsToolRow[] }) {
  const [metric, setMetric] = useState<ToolMetric>("total_duration_ms");
  const sorted = [...rows].sort((a, b) => (b[metric] ?? 0) - (a[metric] ?? 0)).slice(0, 10);
  return (
    <Card>
      <CardHeader title="Tools" description="Which tools agents wait on, and which fail."
        actions={<select className={cx(inlineInputClass, "py-1 text-xs")} value={metric} onChange={(e) => setMetric(e.target.value as ToolMetric)}>
          <option value="total_duration_ms">Total time</option>
          <option value="avg_duration_ms">Average time</option>
          <option value="calls">Calls</option>
          <option value="failures">Failures</option>
        </select>} />
      {sorted.length === 0
        ? <div className="p-5 text-sm text-zinc-500">No tool calls in this range.</div>
        : <RankedBars rows={sorted as unknown as Row[]} nameKey="tool" valueKey={metric} nameWidth={130} color={metric === "failures" ? "#f43f5e" : "#14b8a6"}
            format={(v) => (metric.endsWith("_ms") ? ms(v) : String(v))} />}
    </Card>
  );
}

/** Usage by whoever started the runs: members and API keys, with their share of spend. Click one to filter by them. */
function UsersCard({ rows, total, onPick }: { rows: AnalyticsUserRow[]; total: number; onPick: (user: string) => void }) {
  return (
    <Card>
      <CardHeader title="By user" description="Who started the runs (a member or an API key), what they spent and how often runs failed. Click one to filter by them." />
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="border-b border-zinc-200 text-left text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr>
              <th className="px-5 py-2.5 font-medium">User</th>
              <th className="px-3 py-2.5 text-right font-medium">Runs</th>
              <th className="px-3 py-2.5 text-right font-medium">Spend</th>
              <th className="hidden px-3 py-2.5 font-medium sm:table-cell">Share</th>
              <th className="hidden px-3 py-2.5 text-right font-medium md:table-cell">Tokens</th>
              <th className="hidden px-3 py-2.5 text-right font-medium md:table-cell">Avg / run</th>
              <th className="hidden px-3 py-2.5 text-right font-medium lg:table-cell">Failed</th>
              <th className="hidden px-5 py-2.5 font-medium lg:table-cell">Last run</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800/70">
            {rows.map((u) => {
              const share = total > 0 ? u.cost_usd / total : 0;
              return (
                <tr key={u.user} onClick={() => onPick(u.user)} className="cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-900/50">
                  <td className="max-w-xs px-5 py-2.5">
                    <div className="flex items-center gap-2">
                      {u.kind === "api_key" ? <Icons.Key className="h-3.5 w-3.5 shrink-0 text-zinc-400" /> : <Icons.User className="h-3.5 w-3.5 shrink-0 text-zinc-400" />}
                      <span className={cx("truncate font-medium", u.kind === "unknown" ? "text-zinc-500" : "text-zinc-900 dark:text-zinc-100")} title={u.name}>{u.name}</span>
                    </div>
                  </td>
                  <td className="px-3 py-2.5 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{u.runs}</td>
                  <td className="px-3 py-2.5 text-right tabular-nums text-zinc-900 dark:text-zinc-100">{money(u.cost_usd)}</td>
                  <td className="hidden px-3 py-2.5 sm:table-cell">
                    <div className="flex items-center gap-2">
                      <div className="h-1.5 w-24 overflow-hidden rounded-full bg-zinc-100 dark:bg-zinc-800">
                        <div className="h-full rounded-full bg-indigo-500" style={{ width: `${Math.round(share * 100)}%` }} />
                      </div>
                      <span className="text-xs tabular-nums text-zinc-500">{Math.round(share * 100)}%</span>
                    </div>
                  </td>
                  <td className="hidden px-3 py-2.5 text-right tabular-nums text-zinc-600 md:table-cell dark:text-zinc-400">{compact(u.tokens)}</td>
                  <td className="hidden px-3 py-2.5 text-right tabular-nums text-zinc-600 md:table-cell dark:text-zinc-400">{money(u.avg_cost_usd)}</td>
                  <td className={cx("hidden px-3 py-2.5 text-right tabular-nums lg:table-cell", u.failed ? "text-rose-600 dark:text-rose-400" : "text-zinc-400")}>{u.failed}</td>
                  <td className="hidden px-5 py-2.5 text-xs text-zinc-500 lg:table-cell">{ago(u.last_run_at)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function RolesCard({ rows }: { rows: { role: string; tokens: number; cost_usd: number }[] }) {
  const [metric, setMetric] = useState<"tokens" | "cost_usd">("tokens");
  const sorted = [...rows].sort((a, b) => b[metric] - a[metric]).slice(0, 10);
  const total = rows.reduce((n, r) => n + r[metric], 0);
  return (
    <Card>
      <CardHeader title="What consumes the most" description="By agent role, across everything in range."
        actions={<Tabs value={metric} onChange={setMetric} className="border-0" tabs={[{ id: "tokens", label: "Tokens" }, { id: "cost_usd", label: "Spend" }]} />} />
      {sorted.length === 0
        ? <div className="p-5 text-sm text-zinc-500">Nothing in this range.</div>
        : <RankedBars rows={sorted as unknown as Row[]} nameKey="role" valueKey={metric} total={total} format={(v) => (metric === "tokens" ? compact(v) : usd(v))} />}
    </Card>
  );
}

// ---------- The page ----------

export default function AnalyticsPage() {
  return <Suspense><AnalyticsView /></Suspense>;
}

/** Where tokens and money go and what takes long, for tasks or workspaces, with filters kept in the URL. */
function AnalyticsView() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const scope = params.get("scope") === "workspaces" ? "workspaces" : "tasks";
  const range = params.get("range") ?? (params.get("from") ? "custom" : "7d");
  const filter: AnalyticsFilter = useMemo(() => {
    const preset = RANGES.some((r) => r.id === range);
    return {
      scope,
      range: preset ? range : null,
      from: preset ? null : params.get("from"),
      to: preset ? null : params.get("to"),
      source: scope === "tasks" ? params.get("source") : null,
      status: scope === "tasks" ? (params.get("status") as AnalyticsFilter["status"]) : null,
      q: scope === "tasks" ? params.get("q") : null,
      user: scope === "tasks" ? params.get("user") : null,
      workspace: scope === "workspaces" ? params.get("workspace") : null,
      model: params.get("model"),
    };
  }, [params, range, scope]);

  const [data, setData] = useState<{ key: string; value: Analytics | WorkspaceAnalytics } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState(params.get("q") ?? "");
  const [models, setModels] = useState<ModelChoice[]>([]);
  const [workspaces, setWorkspaces] = useState<{ workspace_id: string; name: string }[]>([]);
  const key = params.toString();

  useEffect(() => {
    getLlmSettings().then((v) => setModels(modelChoices(v))).catch(() => { /* the model filter is optional */ });
    listWorkspaces().then(setWorkspaces).catch(() => { /* the workspace filter is optional */ });
  }, []);

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

  // Data for the other view (left over while switching) is not shown.
  const current = data && data.value.scope === scope ? data.value : null;
  const loading = data?.key !== key;

  /** Clicking a bucket narrows the whole page to it. */
  function drillInto(t: string) {
    if (!current) return;
    const start = new Date(t);
    const end = new Date(start.getTime() + (current.range.bucket === "hour" ? 3600_000 : 86_400_000));
    update({ range: null, from: start.toISOString(), to: end.toISOString() });
  }

  const filtered = ["source", "status", "q", "model", "workspace", "user"].some((k) => params.get(k)) || range === "custom";

  return (
    <div>
      <PageHeader
        title="Analytics"
        description="Where your tokens and money go, which models and tools are worth it, and what takes long. Click a bar, a slice or a model to drill in."
        actions={<Link href="/runs"><Button icon={<Icons.Runs className="h-3.5 w-3.5" />}>Run history</Button></Link>}
      />

      <div className="mx-auto max-w-7xl space-y-5 px-6 py-6">
        <Tabs value={scope} onChange={(s) => update({ scope: s === "tasks" ? null : s, source: null, status: null, q: null, user: null, workspace: null })}
          tabs={[{ id: "tasks", label: "Tasks" }, { id: "workspaces", label: "Workspaces" }]} />

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
            value={(filter.from ?? current?.range.from)?.slice(0, 10) ?? ""}
            onChange={(e) => update({ range: null, from: e.target.value ? new Date(e.target.value).toISOString() : null, to: filter.to ?? current?.range.to ?? null })} />
          <span className="text-xs text-zinc-400">to</span>
          <input type="date" className={cx(inlineInputClass, "py-1.5 text-xs")} title="To"
            value={dayBefore(filter.to ?? current?.range.to)}
            onChange={(e) => update({ range: null, from: filter.from ?? current?.range.from ?? null, to: e.target.value ? new Date(new Date(e.target.value).getTime() + 86_400_000).toISOString() : null })} />
          {models.length > 1 && (
            <select className={cx(inlineInputClass, "py-1.5 text-xs")} value={params.get("model") ?? ""} onChange={(e) => update({ model: e.target.value || null })}>
              <option value="">Any model</option>
              {models.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
            </select>
          )}
          {scope === "tasks" ? (
            <>
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
              {params.get("user") && (
                <button onClick={() => update({ user: null })} title="Show everyone's runs"
                  className="inline-flex items-center gap-1.5 rounded-full border border-zinc-200 bg-white px-2.5 py-1 text-xs text-zinc-700 hover:border-zinc-300 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-300">
                  <Icons.User className="h-3 w-3" />
                  {current?.scope === "tasks" ? current.by_user.find((u) => u.user === params.get("user"))?.name ?? params.get("user") : params.get("user")}
                  <Icons.X className="h-3 w-3 text-zinc-400" />
                </button>
              )}
            </>
          ) : (
            <select className={cx(inlineInputClass, "py-1.5 text-xs")} value={params.get("workspace") ?? ""} onChange={(e) => update({ workspace: e.target.value || null })}>
              <option value="">Every workspace</option>
              {workspaces.map((w) => <option key={w.workspace_id} value={w.workspace_id}>{w.name}</option>)}
            </select>
          )}
          {filtered && (
            <Button size="sm" variant="ghost" onClick={() => { setQuery(""); router.replace(scope === "tasks" ? pathname : `${pathname}?scope=workspaces`); }}>Clear</Button>
          )}
        </div>

        <ErrorBanner error={error} onClose={() => setError(null)} />

        {!current ? (
          <div className="grid gap-4 md:grid-cols-3">{[0, 1, 2, 3, 4, 5].map((i) => <div key={i} className="h-28 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />)}</div>
        ) : current.scope === "tasks" ? (
          <TasksAnalytics a={current} loading={loading} onDrill={drillInto} update={update} />
        ) : (
          <WorkspacesAnalytics w={current} loading={loading} onDrill={drillInto} update={update} />
        )}
      </div>
    </div>
  );
}

function TasksAnalytics({ a, loading, onDrill, update }: {
  a: Analytics; loading: boolean; onDrill: (t: string) => void; update: (patch: Record<string, string | null>) => void;
}) {
  const [runsTab, setRunsTab] = useState<"cost" | "slow">("cost");
  const theme = useChartTheme();
  if (a.totals.runs === 0) {
    return <EmptyState icon={<Icons.Analytics className="h-5 w-5" />} title="No runs in this range" description="Widen the date range or clear the filters." />;
  }

  const bucketLabel = (t: string) => label(t, a.range.bucket);
  const series = a.series.map((p) => ({ ...p, label: bucketLabel(p.t) }));
  const runs: AnalyticsTaskRow[] = runsTab === "cost" ? a.top_by_cost : a.slowest;

  return (
    <div className={cx("space-y-5 transition-opacity", loading && "opacity-60")}>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Kpi label="Spend" value={usd(a.totals.cost_usd)} hint={<Delta now={a.totals.cost_usd} before={a.previous.cost_usd} inverse />} />
        <Kpi label="Tokens" value={compact(a.totals.tokens)} hint={<Delta now={a.totals.tokens} before={a.previous.tokens} inverse />} />
        <Kpi label="Runs" value={a.totals.runs} hint={`${a.totals.completed} done · ${a.totals.failed} failed${a.totals.running ? ` · ${a.totals.running} running` : ""}`} />
        <Kpi label="Avg spend / run" value={usd(a.totals.avg_cost_usd)} hint={<Delta now={a.totals.avg_cost_usd} before={a.previous.avg_cost_usd} inverse />} />
        <Kpi label="Avg tokens / run" value={compact(Math.round(a.totals.avg_tokens))} hint={`${a.totals.agents} agents in all`} />
        <Kpi label="Median duration" value={duration(a.totals.p50_duration_s)} hint={`p95 ${duration(a.totals.p95_duration_s)}`} />
      </div>

      <TrendCard series={series as unknown as SeriesPoint[]} bucket={a.range.bucket} onDrill={onDrill} countKey="runs" countLabel="Runs"
        metrics={[
          { id: "cost_usd", label: "Spend", format: (v) => usd(v ?? 0) },
          { id: "tokens", label: "Tokens", format: (v) => compact(v ?? 0) },
          { id: "runs", label: "Runs", format: (v) => String(v ?? 0) },
          { id: "avg_duration_s", label: "Avg duration", format: (v) => duration(v) },
        ]} />

      <div className="grid gap-5 lg:grid-cols-5">
        <div className="lg:col-span-3"><RolesCard rows={a.by_role} /></div>
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

      <UsersCard rows={a.by_user} total={a.totals.cost_usd} onPick={(user) => update({ user })} />

      <ModelsCard rows={a.by_model} onPick={(id) => update({ model: id })} />

      <div className="grid gap-5 lg:grid-cols-2">
        <ToolsCard rows={a.by_tool} />
        <Card>
          <CardHeader title="How long runs take" description={`Finished runs by duration. Average ${duration(a.totals.avg_duration_s)}.`} />
          <div className="h-64 px-2 pb-3 pt-4">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={a.duration_histogram} margin={{ left: 0, right: 16 }}>
                <CartesianGrid stroke={theme.grid} vertical={false} />
                <XAxis dataKey="label" stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} />
                <YAxis stroke={theme.axis} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} width={32} />
                <Tooltip cursor={{ fill: theme.cursor }} content={<ChartTooltip format={(_, v) => `${v} runs`} />} />
                <Bar isAnimationActive={false} dataKey="runs" name="Runs" fill="#6366f1" radius={[4, 4, 0, 0]} maxBarSize={48} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </Card>
      </div>

      <Card>
        <CardHeader title="Runs to look at" description="The most expensive and the slowest runs in range."
          actions={<Tabs value={runsTab} onChange={setRunsTab} className="border-0" tabs={[{ id: "cost", label: "Most expensive" }, { id: "slow", label: "Slowest" }]} />} />
        <table className="w-full text-sm">
          <thead className="border-b border-zinc-200 text-left text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr>
              <th className="px-5 py-2.5 font-medium">Goal</th>
              <th className="px-3 py-2.5 font-medium">Status</th>
              <th className="hidden px-3 py-2.5 font-medium xl:table-cell">Started by</th>
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
                <td className="hidden max-w-48 truncate px-3 py-2.5 text-xs text-zinc-600 xl:table-cell dark:text-zinc-400" title={t.started_by_name}>{t.started_by_name}</td>
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
  );
}

function WorkspacesAnalytics({ w, loading, onDrill, update }: {
  w: WorkspaceAnalytics; loading: boolean; onDrill: (t: string) => void; update: (patch: Record<string, string | null>) => void;
}) {
  if (w.totals.workspaces === 0) {
    return <EmptyState icon={<Icons.Workspaces className="h-5 w-5" />} title="No workspaces yet"
      description="Create a workspace to see its spend, triggers and approvals here." action={<Link href="/workspaces"><Button variant="primary">Workspaces</Button></Link>} />;
  }

  const series = w.series.map((p) => ({ ...p, label: label(p.t, w.range.bucket) }));
  const t = w.totals;
  return (
    <div className={cx("space-y-5 transition-opacity", loading && "opacity-60")}>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Kpi label="Spend" value={usd(t.cost_usd)} hint={<Delta now={t.cost_usd} before={w.previous.cost_usd} inverse />} />
        <Kpi label="Tokens" value={compact(t.tokens)} hint={<Delta now={t.tokens} before={w.previous.tokens} inverse />} />
        <Kpi label="Avg spend / day" value={usd(t.avg_cost_per_day_usd)} hint={`${t.active_workspaces} of ${t.workspaces} workspaces active`} />
        <Kpi label="Model calls" value={compact(t.calls)} hint={`avg ${ms(t.avg_call_ms)} · p95 ${ms(t.p95_call_ms)}`} />
        <Kpi label="Triggers fired" value={t.triggers_fired} hint={`${t.tool_calls} tool calls · ${t.tool_failures} failed`} />
        <Kpi label="Approvals" value={t.approvals_requested}
          hint={`${t.approvals_approved} approved · ${t.approvals_rejected} rejected${t.approvals_expired ? ` · ${t.approvals_expired} expired` : ""}`} />
      </div>

      <TrendCard series={series as unknown as SeriesPoint[]} bucket={w.range.bucket} onDrill={onDrill} countKey="calls" countLabel="Model calls"
        metrics={[
          { id: "cost_usd", label: "Spend", format: (v) => usd(v ?? 0) },
          { id: "tokens", label: "Tokens", format: (v) => compact(v ?? 0) },
          { id: "calls", label: "Model calls", format: (v) => String(v ?? 0) },
          { id: "avg_duration_s", label: "Response time", format: (v) => duration(v) },
        ]} />

      <Card>
        <CardHeader title="By workspace" description="Spend, model calls, triggers and approvals for each workspace. Click one to focus on it." />
        <table className="w-full text-sm">
          <thead className="border-b border-zinc-200 text-left text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr>
              <th className="px-5 py-2.5 font-medium">Workspace</th>
              <th className="px-3 py-2.5 text-right font-medium">Spend</th>
              <th className="px-3 py-2.5 text-right font-medium">Tokens</th>
              <th className="hidden px-3 py-2.5 text-right font-medium md:table-cell">Model calls</th>
              <th className="px-3 py-2.5 text-right font-medium">Triggers</th>
              <th className="hidden px-3 py-2.5 text-right font-medium md:table-cell">Approvals</th>
              <th className="px-5 py-2.5" />
            </tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800/70">
            {w.by_workspace.map((ws) => (
              <tr key={ws.workspace_id} className="cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-900/50" onClick={() => update({ workspace: ws.workspace_id })}>
                <td className="px-5 py-2.5">
                  <div className="flex items-center gap-2"><span className="font-medium text-zinc-900 dark:text-zinc-100">{ws.name}</span><StatusBadge status={ws.status} /></div>
                </td>
                <td className="px-3 py-2.5 text-right tabular-nums text-zinc-900 dark:text-zinc-100">{usd(ws.cost_usd)}</td>
                <td className="px-3 py-2.5 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{compact(ws.tokens)}</td>
                <td className="hidden px-3 py-2.5 text-right tabular-nums text-zinc-600 md:table-cell dark:text-zinc-400">{ws.calls}</td>
                <td className="px-3 py-2.5 text-right tabular-nums text-zinc-600 dark:text-zinc-400">{ws.triggers_fired}</td>
                <td className="hidden px-3 py-2.5 text-right tabular-nums text-zinc-600 md:table-cell dark:text-zinc-400">{ws.approvals_requested}</td>
                <td className="px-5 py-2.5 text-right">
                  <Link href={`/workspaces?id=${ws.workspace_id}`} onClick={(e) => e.stopPropagation()} className="text-xs text-zinc-500 hover:text-brand-600 dark:hover:text-brand-400">Open</Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>

      <div className="grid gap-5 lg:grid-cols-2">
        <RolesCard rows={w.by_role} />
        <ToolsCard rows={w.by_tool} />
      </div>

      <ModelsCard rows={w.by_model} onPick={(id) => update({ model: id })} />
    </div>
  );
}

function label(t: string, bucket: "hour" | "day") {
  const d = new Date(t);
  return bucket === "hour"
    ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : d.toLocaleDateString([], { month: "short", day: "numeric" });
}
