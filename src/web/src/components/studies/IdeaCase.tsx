"use client";

import { useEffect, useMemo, useState } from "react";
import { Area, CartesianGrid, ComposedChart, Line, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import {
  apiErrorMessage,
  getProjectionDrivers,
  runStudyProjection,
  type Bands,
  type DriverValue,
  type IdeaBrief,
  type ProjectionDriverInfo,
  type ProjectionResult,
  type StudyAssumption,
  type StudyDetail,
  type StudyProjection,
  type StudyVerdict,
  type VerdictScore,
} from "@/lib/api";
import { Badge, Button, Card, CardHeader, EmptyState, ErrorBanner, Field, Modal, ago, cx, inlineInputClass, inputClass, type Tone } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { EvidenceChip, num, pct } from "./StudyEvidence";

/** The diverging pair for "lowers / raises NPV" and the cash band (validated for light and dark surfaces). */
const UP = "#0284c7";
const DOWN = "#d97706";

export const RECOMMENDATION: Record<StudyVerdict["recommendation"], { label: string; tone: Tone; hint: string }> = {
  go: { label: "Go", tone: "green", hint: "Worth pursuing as described." },
  go_with_changes: { label: "Go, with changes", tone: "blue", hint: "Worth pursuing if the changes below are made." },
  no_go: { label: "No go", tone: "red", hint: "Not worth pursuing as it stands." },
  needs_more_evidence: { label: "Needs more evidence", tone: "amber", hint: "The evidence can't decide it yet." },
};

const DIMENSIONS: { key: "usefulness" | "feasibility" | "profitability"; label: string; question: string }[] = [
  { key: "usefulness", label: "Usefulness", question: "Do the customers have the problem, and would they switch and pay?" },
  { key: "feasibility", label: "Feasibility", question: "Can it be built and run: technology, skills, time, cost, rules?" },
  { key: "profitability", label: "Profitability", question: "Does it make money within the horizon, in simulated futures?" },
];

/** An amount in the idea's currency, short for big numbers: 1234567 → "$1.2M". */
export function cash(v: number | null | undefined, currency = "USD", short = true) {
  if (typeof v !== "number" || !Number.isFinite(v)) return "—";
  try {
    return new Intl.NumberFormat(undefined, {
      style: "currency", currency, notation: short && Math.abs(v) >= 10_000 ? "compact" : "standard",
      minimumFractionDigits: 0, maximumFractionDigits: v !== 0 && Math.abs(v) < 100 ? 2 : 0,
    }).format(v);
  } catch {
    return `${num(v)} ${currency}`;
  }
}

const months = (v: number | null | undefined) => (typeof v === "number" ? `month ${v}` : "never");
const band = (b: Bands | undefined, f: (v: number) => string) => (b ? `${f(b.p10)} – ${f(b.p90)}` : "—");

export const blankIdea = (): IdeaBrief => ({ idea: "", customers: "", problem: "", revenue_model: "", costs: "", horizon_months: 36, currency: "USD" });

/** The fields of an idea brief, for creating or editing an idea study. */
export function IdeaFields({ value, onChange }: { value: IdeaBrief; onChange: (v: IdeaBrief) => void }) {
  const text = (key: "idea" | "customers" | "problem" | "revenue_model" | "costs", label: string, placeholder: string, hint?: string, rows = 2) => (
    <Field label={label} hint={hint}>
      <textarea className={inputClass} rows={rows} value={value[key] ?? ""} placeholder={placeholder} onChange={(e) => onChange({ ...value, [key]: e.target.value })} />
    </Field>
  );
  return (
    <div className="space-y-4">
      {text("idea", "The idea", "An AI assistant that triages tenants' maintenance requests for small landlords and books the right contractor.", "What it is and what it does.", 3)}
      {text("customers", "Who it's for", "Independent landlords with 5–50 units in the US.")}
      {text("problem", "The problem it solves", "Requests arrive by text at all hours; landlords sort and dispatch them by hand.", "And what people do about it today: the alternative they'd switch from.")}
      {text("revenue_model", "How it makes money", "Subscription: $39 a month per landlord, $99 for 50+ units.", "Prices, plans, who pays. Agents treat these numbers as claims to check.")}
      {text("costs", "Known costs and constraints", "Two engineers for six months; LLM costs about $1 per landlord a month; budget $150k.")}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Horizon (months)" hint="How far ahead the projections look.">
          <input className={inputClass} type="number" min={6} max={120} value={value.horizon_months} onChange={(e) => onChange({ ...value, horizon_months: Number(e.target.value) })} />
        </Field>
        <Field label="Currency"><input className={inputClass} maxLength={3} value={value.currency} onChange={(e) => onChange({ ...value, currency: e.target.value.toUpperCase() })} /></Field>
      </div>
    </div>
  );
}

/** The brief the study evaluates. */
export function IdeaBriefCard({ idea }: { idea: IdeaBrief }) {
  const rows: [string, string | null | undefined][] = [
    ["Who it's for", idea.customers], ["The problem", idea.problem], ["How it makes money", idea.revenue_model], ["Known costs", idea.costs],
  ];
  return (
    <Card className="p-5">
      <div className="mb-1 text-xs font-medium uppercase tracking-wide text-zinc-500">The idea</div>
      <p className="whitespace-pre-line text-sm text-zinc-800 dark:text-zinc-200">{idea.idea}</p>
      <dl className="mt-3 grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        {rows.filter(([, v]) => v).map(([label, v]) => (
          <div key={label}><dt className="text-xs text-zinc-500">{label}</dt><dd className="whitespace-pre-line text-zinc-700 dark:text-zinc-300">{v}</dd></div>
        ))}
        <div><dt className="text-xs text-zinc-500">Horizon</dt><dd className="text-zinc-700 dark:text-zinc-300">{idea.horizon_months} months · {idea.currency}</dd></div>
      </dl>
    </Card>
  );
}

/** A 0–10 meter for one dimension of the verdict. */
function ScoreMeter({ label, question, score, compact }: { label: string; question: string; score: VerdictScore | undefined; compact?: boolean }) {
  const value = score?.score ?? 0;
  return (
    <div className="min-w-0">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-sm font-medium text-zinc-800 dark:text-zinc-200" title={question}>{label}</span>
        <span className="text-sm tabular-nums text-zinc-700 dark:text-zinc-300"><span className="text-lg font-semibold text-zinc-900 dark:text-zinc-50">{num(value, 1)}</span> / 10</span>
      </div>
      <div className="mt-1.5 h-2 rounded-full bg-zinc-100 dark:bg-zinc-800" role="meter" aria-valuemin={0} aria-valuemax={10} aria-valuenow={value} aria-label={`${label} ${value} of 10`}>
        <div className="h-2 rounded-full bg-brand-500" style={{ width: `${Math.max(2, value * 10)}%` }} />
      </div>
      {!compact && score && (
        <>
          <p className="mt-2 text-sm text-zinc-600 dark:text-zinc-400">{score.summary}</p>
          <div className="mt-1.5 flex flex-wrap gap-1">{score.evidence.map((e) => <EvidenceChip key={e} id={e} />)}</div>
        </>
      )}
    </div>
  );
}

/** The verdict on an idea: the recommendation, the three scores and the headline numbers. */
export function VerdictCard({ verdict, compact, onOpen }: { verdict: StudyVerdict; compact?: boolean; onOpen?: () => void }) {
  const rec = RECOMMENDATION[verdict.recommendation] ?? RECOMMENDATION.needs_more_evidence;
  const p = verdict.projection;
  const currency = p?.currency ?? "USD";
  return (
    <Card className="p-5">
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-xs font-medium uppercase tracking-wide text-zinc-500">Verdict</div>
        <Badge tone={rec.tone} className="text-sm">{rec.label}</Badge>
        <span className="text-xs text-zinc-500">{rec.hint}</span>
        {typeof verdict.overall === "number" && <span className="ml-auto text-xs text-zinc-500">Overall <span className="font-semibold tabular-nums text-zinc-800 dark:text-zinc-200">{num(verdict.overall, 1)}</span> / 10</span>}
      </div>
      <div className={cx("mt-4 grid gap-5", compact ? "sm:grid-cols-3" : "lg:grid-cols-3")}>
        {DIMENSIONS.map((d) => <ScoreMeter key={d.key} label={d.label} question={d.question} score={verdict[d.key]} compact={compact} />)}
      </div>
      {p && (
        <div className="mt-5 grid gap-3 border-t border-zinc-100 pt-4 text-sm sm:grid-cols-2 lg:grid-cols-4 dark:border-zinc-800">
          <KeyNumber label="Pays back" value={pct(p.p_payback)} hint={`of simulated futures, within ${p.horizon_months} months`} />
          <KeyNumber label="Median payback" value={months(p.payback_month?.p50)} hint={p.payback_month ? `never in ${pct(p.payback_month.never_share)}` : undefined} />
          <KeyNumber label="NPV (median)" value={cash(p.npv?.p50, currency)} hint={`P10–P90 ${band(p.npv, (v) => cash(v, currency))}`} />
          <KeyNumber label="Funding needed" value={cash(p.funding_need?.p50, currency)} hint={`up to ${cash(p.funding_need?.p90, currency)} (P90)`} />
        </div>
      )}
      {verdict.assumption_check && !compact && (
        <p className="mt-4 text-xs text-zinc-500">
          Assumptions: {verdict.assumption_check.with_evidence} of {verdict.assumption_check.total} cite evidence ·{" "}
          {verdict.assumption_check.accepted} accepted, {verdict.assumption_check.challenged} challenged, {verdict.assumption_check.unreviewed} unreviewed
          {verdict.assumption_check.guesses.length > 0 && <> · guesses: <span className="font-mono">{verdict.assumption_check.guesses.join(", ")}</span></>}
        </p>
      )}
      {compact && onOpen && <button onClick={onOpen} className="mt-3 text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">Read the full verdict →</button>}
    </Card>
  );
}

function KeyNumber({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div>
      <div className="text-[11px] font-medium uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="text-lg font-semibold tabular-nums text-zinc-900 dark:text-zinc-50">{value}</div>
      {hint && <div className="text-xs text-zinc-500">{hint}</div>}
    </div>
  );
}

/** Risks, changes and next steps of a verdict (in the report). */
export function VerdictDetails({ verdict, assumptions }: { verdict: StudyVerdict; assumptions: StudyAssumption[] }) {
  const currency = verdict.projection?.currency;
  const byId = new Map(assumptions.map((a) => [a.assumption_id, a]));
  const keys = (verdict.key_assumptions ?? []).map((id) => byId.get(id)).filter((a): a is StudyAssumption => !!a);
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      {(verdict.risks?.length ?? 0) > 0 && (
        <Card>
          <CardHeader title="Risks" />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {verdict.risks!.map((r, i) => (
              <li key={i} className="px-5 py-3 text-sm">
                <div className="flex items-start gap-2">
                  {r.severity && <Badge tone={r.severity === "high" ? "red" : r.severity === "medium" ? "amber" : "neutral"}>{r.severity}</Badge>}
                  <span className="text-zinc-800 dark:text-zinc-200">{r.risk}</span>
                </div>
                {r.mitigation && <p className="mt-1 text-xs text-zinc-500">Mitigation: {r.mitigation}</p>}
              </li>
            ))}
          </ul>
        </Card>
      )}
      {keys.length > 0 && (
        <Card>
          <CardHeader title="What the verdict depends on" description="The assumptions it rests on most; test these first." />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {keys.map((a) => (
              <li key={a.assumption_id} className="flex flex-wrap items-center gap-2 px-5 py-2.5 text-sm">
                <span className="font-mono text-xs">{a.name}</span>
                <span className="tabular-nums text-zinc-600 dark:text-zinc-400">{rangeText(a, currency)}</span>
                <AssumptionStatus a={a} />
              </li>
            ))}
          </ul>
        </Card>
      )}
      {(verdict.changes?.length ?? 0) > 0 && <Bullets title="Changes to make" items={verdict.changes!} />}
      {(verdict.next_steps?.length ?? 0) > 0 && <Bullets title="Next steps" items={verdict.next_steps!} />}
    </div>
  );
}

function Bullets({ title, items }: { title: string; items: string[] }) {
  return (
    <Card className="p-5">
      <div className="mb-2 text-sm font-semibold text-zinc-900 dark:text-zinc-100">{title}</div>
      <ul className="list-disc space-y-1 pl-5 text-sm text-zinc-700 dark:text-zinc-300">{items.map((x) => <li key={x}>{x}</li>)}</ul>
    </Card>
  );
}

/** "20,000 – 40,000 – 60,000 customers"; money in the idea's currency. */
const rangeText = (a: { low: number; likely: number; high: number; unit?: string | null }, currency?: string) => {
  const unit = a.unit === "currency" ? currency ?? "" : a.unit === "share" ? "" : a.unit;
  const value = (v: number) => (a.unit === "share" ? pct(v, Math.round(v * 1000) % 10 ? 1 : 0) : num(v));
  return `${a.low === a.high ? value(a.likely) : `${value(a.low)} – ${value(a.likely)} – ${value(a.high)}`}${unit ? ` ${unit}` : ""}`;
};

function AssumptionStatus({ a }: { a: StudyAssumption }) {
  return (
    <span className="flex flex-wrap gap-1">
      {a.evidence.length === 0 && <Badge tone="amber">Guess: no evidence</Badge>}
      <Badge tone={a.status === "accepted" ? "green" : a.status === "challenged" ? "red" : "neutral"}>
        {a.status === "candidate" ? "Not reviewed" : a.status === "accepted" ? "Accepted" : "Challenged"}
      </Badge>
    </span>
  );
}

/**
 * The business case of an idea study: the assumption register and the financial projections,
 * with a what-if form that runs the same business model from the register or typed-in ranges.
 */
export function BusinessCase({ study, canEdit, onChanged }: { study: StudyDetail; canEdit: boolean; onChanged: () => void }) {
  const [whatIf, setWhatIf] = useState<StudyProjection | "new" | null>(null);
  const [showOld, setShowOld] = useState(false);
  const current = study.assumptions.filter((a) => a.current);
  const old = study.assumptions.filter((a) => !a.current);
  const categories = Array.from(new Set(current.map((a) => a.category)));
  const projections = [...study.projections].reverse();

  return (
    <div className="space-y-5">
      <Card>
        <CardHeader title="Assumptions" description="Every number the case rests on, as a range with the evidence behind it. Another agent reviews each one; projections take their inputs from here." />
        {current.length === 0 ? (
          <p className="px-5 py-4 text-sm text-zinc-500">None yet: a run records them as its agents research the idea.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-wide text-zinc-500">
                <tr><th className="px-5 py-2 font-medium">Assumption</th><th className="px-3 py-2 font-medium">Low – likely – high</th><th className="px-3 py-2 font-medium">Evidence</th><th className="px-5 py-2 font-medium">Review</th></tr>
              </thead>
              {categories.map((c) => (
                <tbody key={c} className="divide-y divide-zinc-100 border-t border-zinc-100 dark:divide-zinc-800 dark:border-zinc-800">
                  <tr><td colSpan={4} className="bg-zinc-50 px-5 py-1 text-[11px] font-medium uppercase tracking-wide text-zinc-500 dark:bg-zinc-900">{c}</td></tr>
                  {current.filter((a) => a.category === c).map((a) => <AssumptionRow key={a.assumption_id} a={a} currency={study.idea?.currency} />)}
                </tbody>
              ))}
            </table>
          </div>
        )}
        {old.length > 0 && (
          <div className="border-t border-zinc-100 px-5 py-2 dark:border-zinc-800">
            <button className="text-xs text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200" onClick={() => setShowOld(!showOld)}>
              {showOld ? "Hide" : "Show"} {old.length} replaced assumption{old.length === 1 ? "" : "s"}
            </button>
            {showOld && (
              <table className="mt-2 w-full text-sm opacity-70"><tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">{old.map((a) => <AssumptionRow key={a.assumption_id} a={a} currency={study.idea?.currency} />)}</tbody></table>
            )}
          </div>
        )}
      </Card>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Financial projections</h2>
          <p className="text-xs text-zinc-500">The business simulated month by month, thousands of times, each driver drawn from its range. Computed by the runtime, not estimated by a model.</p>
        </div>
        {canEdit && <Button icon={<Icons.Play className="h-3.5 w-3.5" />} onClick={() => setWhatIf("new")}>What if…</Button>}
      </div>
      {projections.length === 0 ? (
        <EmptyState icon={<Icons.Simulation className="h-5 w-5" />} title="No projections yet"
          description="A run projects the finances from its assumptions. You can also run your own what-if: change the price, the churn or the market size and see what happens." />
      ) : (
        projections.map((p) => <ProjectionCard key={p.projection_id} projection={p} onWhatIf={canEdit ? () => setWhatIf(p) : undefined} />)
      )}

      {whatIf !== null && (
        <WhatIfForm study={study} from={whatIf === "new" ? null : whatIf} onClose={() => setWhatIf(null)} onDone={() => { setWhatIf(null); onChanged(); }} />
      )}
    </div>
  );
}

function AssumptionRow({ a, currency }: { a: StudyAssumption; currency?: string }) {
  return (
    <tr className="align-top">
      <td className="px-5 py-2.5">
        <div className="font-mono text-xs text-zinc-800 dark:text-zinc-200">{a.name}</div>
        {a.description && <div className="text-xs text-zinc-500">{a.description}</div>}
        {a.rationale && <div className="mt-0.5 text-xs italic text-zinc-500">{a.rationale}</div>}
      </td>
      <td className="whitespace-nowrap px-3 py-2.5 tabular-nums text-zinc-700 dark:text-zinc-300">{rangeText(a, currency)}</td>
      <td className="whitespace-nowrap px-3 py-2.5"><div className="flex flex-wrap gap-1">{a.evidence.length ? a.evidence.map((e) => <EvidenceChip key={e} id={e} />) : <span className="text-xs text-zinc-400">—</span>}</div></td>
      <td className="px-5 py-2.5">
        <AssumptionStatus a={a} />
        {a.review_notes && <div className="mt-1 max-w-xs text-xs text-zinc-500" title={a.review_notes}>{a.review_notes}</div>}
      </td>
    </tr>
  );
}

function ProjectionCard({ projection: p, onWhatIf }: { projection: StudyProjection; onWhatIf?: () => void }) {
  const r = p.result;
  const s = r.summary;
  const c = r.currency;
  const [tab, setTab] = useState<"cash" | "revenue" | "profit">("cash");
  return (
    <Card>
      <CardHeader
        title={<span className="flex flex-wrap items-center gap-2">{p.name}<Badge>{r.revenue_model.replace("_", " ")}</Badge>{p.manual && <Badge tone="blue">what-if by a person</Badge>}<EvidenceChip id={p.evidence_id} /></span>}
        description={`${r.iterations.toLocaleString()} simulated futures over ${r.horizon_months} months · ${p.manual ? "run by hand" : p.agent_id} · ${ago(p.created_at)}`}
        actions={onWhatIf && <Button size="sm" onClick={onWhatIf}>What if…</Button>}
      />
      <div className="grid gap-3 px-5 pt-4 sm:grid-cols-2 lg:grid-cols-4">
        <KeyNumber label="Pays back" value={pct(s.p_payback)} hint={`median ${months(s.payback_month.p50)} · never ${pct(s.payback_month.never_share)}`} />
        <KeyNumber label="NPV (median)" value={cash(s.npv.p50, c)} hint={`positive in ${pct(s.p_npv_positive)} · P10–P90 ${band(s.npv, (v) => cash(v, c))}`} />
        <KeyNumber label="Funding needed" value={cash(s.funding_need.p50, c)} hint={`P90 ${cash(s.funding_need.p90, c)}`} />
        <KeyNumber label="Customers at the end" value={num(s.customers_at_end.p50, 0)} hint={`P10–P90 ${band(s.customers_at_end, (v) => num(v, 0))}`} />
      </div>

      <div className="px-5 pt-5">
        <div className="mb-2 flex flex-wrap items-center gap-1 text-xs" role="tablist">
          {(["cash", "revenue", "profit"] as const).map((t) => (
            <button key={t} role="tab" aria-selected={tab === t} onClick={() => setTab(t)}
              className={cx("rounded-md px-2 py-1", tab === t ? "bg-zinc-100 font-medium text-zinc-900 dark:bg-zinc-800 dark:text-zinc-100" : "text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200")}>
              {t === "cash" ? "Cumulative cash" : t === "revenue" ? "Monthly revenue" : "Monthly profit"}
            </button>
          ))}
          <span className="ml-auto text-zinc-500">Line: median · band: P10–P90 of simulated futures</span>
        </div>
        <FanChart series={r.monthly[tab]} currency={c} label={tab === "cash" ? "Cumulative cash" : tab === "revenue" ? "Revenue" : "Profit"} />
      </div>

      <div className="grid gap-5 px-5 py-5 lg:grid-cols-2">
        <div>
          <SectionTitle>By year (median, P10–P90)</SectionTitle>
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-wide text-zinc-500"><tr><th className="py-1 font-medium">Year</th><th className="py-1 text-right font-medium">Revenue</th><th className="py-1 text-right font-medium">Profit</th></tr></thead>
            <tbody className="divide-y divide-zinc-100 tabular-nums dark:divide-zinc-800">
              {r.years.map((y) => (
                <tr key={y.year}>
                  <td className="py-1.5">{y.year}</td>
                  <td className="py-1.5 text-right">{cash(y.revenue.p50, c)}<div className="text-[11px] text-zinc-500">{band(y.revenue, (v) => cash(v, c))}</div></td>
                  <td className="py-1.5 text-right">{cash(y.profit.p50, c)}<div className="text-[11px] text-zinc-500">{band(y.profit, (v) => cash(v, c))}</div></td>
                </tr>
              ))}
            </tbody>
          </table>
          <SectionTitle className="mt-4">Unit economics (likely values)</SectionTitle>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
            <UnitRow label="Gross margin" value={pct(r.unit_economics.gross_margin)} />
            <UnitRow label="Customer lifetime" value={`${num(r.unit_economics.customer_lifetime_months, 1)} months`} />
            <UnitRow label="Lifetime value (LTV)" value={cash(r.unit_economics.ltv, c)} />
            <UnitRow label="Cost to acquire (CAC)" value={cash(r.unit_economics.cac, c)} />
            <UnitRow label="LTV / CAC" value={r.unit_economics.ltv_to_cac === null ? "—" : `${num(r.unit_economics.ltv_to_cac, 2)}×`} />
            <UnitRow label="CAC payback" value={r.unit_economics.cac_payback_months === null ? "—" : `${num(r.unit_economics.cac_payback_months, 1)} months`} />
            <UnitRow label="Break-even customers" value={num(r.unit_economics.break_even_customers, 0)} />
          </dl>
        </div>
        <div>
          <SectionTitle>What moves the result most</SectionTitle>
          <Tornado sensitivity={r.sensitivity} currency={c} />
        </div>
      </div>

      {r.warnings.length > 0 && (
        <ul className="space-y-1 border-t border-zinc-100 px-5 py-3 text-xs text-amber-700 dark:border-zinc-800 dark:text-amber-400">{r.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
      )}
      <Inputs result={r} />
    </Card>
  );
}

function SectionTitle({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={cx("mb-2 text-xs font-medium uppercase tracking-wide text-zinc-500", className)}>{children}</div>;
}

function UnitRow({ label, value }: { label: string; value: string }) {
  return <><dt className="text-zinc-500">{label}</dt><dd className="text-right tabular-nums text-zinc-800 dark:text-zinc-200">{value}</dd></>;
}

/** The median of a monthly series with the P10–P90 band around it, and a zero line. */
function FanChart({ series, currency, label }: { series: { p10: number[]; p50: number[]; p90: number[] }; currency: string; label: string }) {
  const data = useMemo(() => series.p50.map((v, i) => ({ month: i + 1, p50: v, band: [series.p10[i], series.p90[i]] as [number, number] })), [series]);
  return (
    <div className="h-56 w-full" aria-label={`${label} by month: median with P10 to P90 band`}>
      <ResponsiveContainer>
        <ComposedChart data={data} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
          <CartesianGrid vertical={false} stroke="currentColor" className="text-zinc-200 dark:text-zinc-800" />
          <XAxis dataKey="month" tickLine={false} axisLine={false} tick={{ fontSize: 11, fill: "currentColor" }} className="text-zinc-500" interval="preserveStartEnd" minTickGap={24} />
          <YAxis width={64} tickLine={false} axisLine={false} tick={{ fontSize: 11, fill: "currentColor" }} className="text-zinc-500" tickFormatter={(v: number) => cash(v, currency)} />
          <ReferenceLine y={0} stroke="currentColor" className="text-zinc-400" />
          <Area dataKey="band" stroke="none" fill={UP} fillOpacity={0.2} isAnimationActive={false} />
          <Line dataKey="p50" stroke={UP} strokeWidth={2} dot={false} isAnimationActive={false} />
          <Tooltip
            cursor={{ stroke: "currentColor", strokeOpacity: 0.3 }}
            content={({ active, payload }) => {
              if (!active || !payload?.length) return null;
              const row = payload[0].payload as { month: number; p50: number; band: [number, number] };
              return (
                <div className="rounded-lg border border-zinc-200 bg-white px-3 py-2 text-xs shadow-sm dark:border-zinc-700 dark:bg-zinc-900">
                  <div className="font-medium text-zinc-900 dark:text-zinc-100">Month {row.month}</div>
                  <div className="text-zinc-700 dark:text-zinc-300">{label} (median): {cash(row.p50, currency, false)}</div>
                  <div className="text-zinc-500">P10–P90: {cash(row.band[0], currency)} – {cash(row.band[1], currency)}</div>
                </div>
              );
            }}
          />
        </ComposedChart>
      </ResponsiveContainer>
    </div>
  );
}

/** NPV with each uncertain driver at its low and its high (every other at its likely value). */
function Tornado({ sensitivity, currency }: { sensitivity: ProjectionResult["sensitivity"]; currency: string }) {
  const rows = sensitivity.drivers.slice(0, 8);
  if (rows.length === 0) return <p className="text-sm text-zinc-500">No driver has a range, so nothing to compare.</p>;
  const base = sensitivity.baseline_npv;
  const extent = Math.max(1, ...rows.flatMap((r) => [Math.abs(r.npv_at_low - base), Math.abs(r.npv_at_high - base)]));
  const bar = (value: number) => {
    const delta = value - base;
    const width = (Math.abs(delta) / extent) * 50;
    return { left: delta < 0 ? 50 - width : 50, width, up: delta >= 0 };
  };
  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center gap-3 text-xs text-zinc-500">
        <span>NPV at likely values: <span className="font-medium tabular-nums text-zinc-800 dark:text-zinc-200">{cash(base, currency)}</span></span>
        <span className="flex items-center gap-1"><span className="inline-block h-2 w-3 rounded-sm" style={{ background: DOWN }} />lowers NPV</span>
        <span className="flex items-center gap-1"><span className="inline-block h-2 w-3 rounded-sm" style={{ background: UP }} />raises NPV</span>
      </div>
      <ul className="space-y-2">
        {rows.map((r) => (
          <li key={r.driver} className="grid grid-cols-[8rem_1fr] items-center gap-2 text-xs" title={`${r.driver}: at ${num(r.low)} → ${cash(r.npv_at_low, currency, false)}; at ${num(r.high)} → ${cash(r.npv_at_high, currency, false)}`}>
            <span className="truncate font-mono text-zinc-700 dark:text-zinc-300">{r.driver}</span>
            <div className="relative h-4">
              <div className="absolute inset-y-0 left-1/2 w-px bg-zinc-300 dark:bg-zinc-600" />
              {[{ v: r.npv_at_low, at: "low" }, { v: r.npv_at_high, at: "high" }].map(({ v, at }) => {
                const b = bar(v);
                return <div key={at} className="absolute inset-y-0.5 rounded-sm" style={{ left: `${b.left}%`, width: `${Math.max(b.width, 0.5)}%`, background: b.up ? UP : DOWN }} />;
              })}
            </div>
          </li>
        ))}
      </ul>
      <p className="mt-2 text-[11px] text-zinc-500">Each bar: the driver at its low and its high, every other driver at its likely value. Hover a row for the numbers.</p>
    </div>
  );
}

const SOURCE_LABEL: Record<string, string> = { assumption: "assumption", given: "typed in", default: "default" };

/** The unit of each driver, for showing a projection's inputs. */
const BusinessUnits: Record<string, string> = {
  market_size: "customers", adoption_rate: "share", initial_customers: "customers", price: "currency", purchases_per_month: "purchases",
  monthly_churn: "share", marketing_spend: "currency", cac: "currency", organic_rate: "share", variable_cost: "currency",
  fixed_costs: "currency", upfront_investment: "currency", launch_month: "months", discount_rate: "share",
};

function Inputs({ result }: { result: ProjectionResult }) {
  const [open, setOpen] = useState(false);
  const inputs = Object.entries(result.inputs ?? {});
  return (
    <div className="border-t border-zinc-100 px-5 py-3 dark:border-zinc-800">
      <button className="text-xs text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200" onClick={() => setOpen(!open)}>
        {open ? "Hide" : "Show"} the {inputs.length} drivers and where each came from
        {result.unsupported_drivers.length > 0 && <span className="text-amber-700 dark:text-amber-400"> · {result.unsupported_drivers.length} without evidence</span>}
      </button>
      {open && (
        <table className="mt-2 w-full text-xs">
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {inputs.map(([name, d]) => (
              <tr key={name}>
                <td className="py-1.5 pr-3 font-mono">{name}</td>
                <td className="py-1.5 pr-3 tabular-nums">{rangeText({ ...d, unit: BusinessUnits[name] }, result.currency)}</td>
                <td className="py-1.5 pr-3">
                  <Badge tone={d.source === "assumption" ? (result.unsupported_drivers.includes(name) ? "amber" : "green") : d.source === "given" ? "amber" : "neutral"}>
                    {SOURCE_LABEL[d.source] ?? d.source}{d.assumption ? `: ${d.assumption}` : ""}
                  </Badge>
                </td>
                <td className="py-1.5"><div className="flex flex-wrap gap-1">{(d.evidence ?? []).map((e) => <EvidenceChip key={e} id={e} />)}</div></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

type DriverDraft = { mode: "default" | "assumption" | "range"; assumption: string; low: string; likely: string; high: string };

/** A what-if: the business model again with drivers from the register or typed-in ranges. */
function WhatIfForm({ study, from, onClose, onDone }: { study: StudyDetail; from: StudyProjection | null; onClose: () => void; onDone: () => void }) {
  const [catalog, setCatalog] = useState<ProjectionDriverInfo[] | null>(null);
  const [models, setModels] = useState<string[]>(["subscription", "one_time", "usage"]);
  const [maxHorizon, setMaxHorizon] = useState(120);
  const [name, setName] = useState(from ? `${from.name}-what-if` : "what-if");
  const [revenueModel, setRevenueModel] = useState(from?.result.revenue_model ?? "subscription");
  const [horizon, setHorizon] = useState(String(from?.result.horizon_months ?? study.idea?.horizon_months ?? 36));
  const [drafts, setDrafts] = useState<Record<string, DriverDraft>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const current = study.assumptions.filter((a) => a.current);

  useEffect(() => {
    getProjectionDrivers().then((c) => {
      setCatalog(c.drivers);
      setModels(c.revenue_models);
      setMaxHorizon(c.max_horizon_months);
      const next: Record<string, DriverDraft> = {};
      for (const d of c.drivers) {
        const input = from?.result.inputs?.[d.name];
        const named = current.find((a) => a.name === d.name);
        const blank = { assumption: named?.assumption_id ?? "", low: "", likely: "", high: "" };
        if (input?.source === "assumption" && input.assumption_id) {
          // A replaced assumption points at its current version, so a what-if uses the latest numbers.
          const latest = current.find((a) => a.name === input.assumption) ?? current.find((a) => a.assumption_id === input.assumption_id);
          next[d.name] = latest ? { ...blank, mode: "assumption", assumption: latest.assumption_id } : { ...blank, mode: "range", low: String(input.low), likely: String(input.likely), high: String(input.high) };
        } else if (input?.source === "given") {
          next[d.name] = { ...blank, mode: "range", low: String(input.low), likely: String(input.likely), high: String(input.high) };
        } else if (!from && named) {
          next[d.name] = { ...blank, mode: "assumption" };
        } else {
          next[d.name] = { ...blank, mode: d.default === null ? "range" : "default" };
        }
      }
      setDrafts(next);
    }).catch((e) => setError(apiErrorMessage(e)));
    // Built once from the projection the what-if starts from.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function set(driver: string, patch: Partial<DriverDraft>) {
    setDrafts((d) => ({ ...d, [driver]: { ...d[driver], ...patch } }));
  }

  async function run() {
    const drivers: Record<string, DriverValue> = {};
    for (const [key, d] of Object.entries(drafts)) {
      if (d.mode === "assumption" && d.assumption) drivers[key] = d.assumption;
      if (d.mode === "range" && d.likely.trim() !== "") {
        const likely = Number(d.likely);
        drivers[key] = { likely, low: d.low.trim() === "" ? likely : Number(d.low), high: d.high.trim() === "" ? likely : Number(d.high) };
      }
    }

    setBusy(true);
    setError(null);
    try {
      await runStudyProjection(study.study_id, { name: name.trim() || "what-if", revenue_model: revenueModel, horizon_months: Number(horizon), drivers });
      onDone();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} wide title={from ? `What if… (from ${from.name})` : "What if…"}
      description="Run the business model with your own numbers. Each driver comes from an assumption in the register, a range you type (low – likely – high), or the model's default. It's saved as a projection marked as yours."
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={busy || !catalog} onClick={run}>{busy ? "Running…" : "Run projection"}</Button></>}>
      <ErrorBanner error={error} onClose={() => setError(null)} />
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Name"><input className={inputClass} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Revenue model" hint="Subscription: pays monthly until they leave. One time: buys once. Usage: buys several times a month.">
          <select className={inputClass} value={revenueModel} onChange={(e) => setRevenueModel(e.target.value)}>
            {models.map((m) => <option key={m} value={m}>{m.replace("_", " ")}</option>)}
          </select>
        </Field>
        <Field label="Horizon (months)"><input className={inputClass} type="number" min={6} max={maxHorizon} value={horizon} onChange={(e) => setHorizon(e.target.value)} /></Field>
      </div>
      {!catalog ? <div className="mt-4 h-40 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" /> : (
        <div className="mt-4 max-h-[50vh] space-y-3 overflow-y-auto pr-1">
          {catalog.map((d) => {
            const draft = drafts[d.name];
            if (!draft) return null;
            const options = current.filter((a) => a.name === d.name || a.unit === d.unit);
            return (
              <div key={d.name} className="rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <span className="font-mono text-xs text-zinc-800 dark:text-zinc-200">{d.name}</span>
                  <span className="text-xs text-zinc-500">{d.description}{d.unit === "share" ? " (0–1)" : ""}</span>
                </div>
                <div className="mt-2 flex flex-wrap items-center gap-2">
                  <select className={cx(inlineInputClass, "w-44 py-1 text-xs")} value={draft.mode} onChange={(e) => set(d.name, { mode: e.target.value as DriverDraft["mode"] })}>
                    {d.default !== null && <option value="default">Default ({num(d.default)})</option>}
                    {options.length > 0 && <option value="assumption">From an assumption</option>}
                    <option value="range">Type a range</option>
                  </select>
                  {draft.mode === "assumption" && (
                    <select className={cx(inlineInputClass, "min-w-0 flex-1 py-1 text-xs")} value={draft.assumption} onChange={(e) => set(d.name, { assumption: e.target.value })}>
                      <option value="">Choose…</option>
                      {options.map((a) => <option key={a.assumption_id} value={a.assumption_id}>{a.name}: {rangeText(a, study.idea?.currency)}{a.evidence.length ? "" : " (guess)"}</option>)}
                    </select>
                  )}
                  {draft.mode === "range" && (["low", "likely", "high"] as const).map((k) => (
                    <input key={k} className={cx(inlineInputClass, "w-28 py-1 text-xs")} type="number" step="any" placeholder={k} aria-label={`${d.name} ${k}`}
                      value={draft[k]} onChange={(e) => set(d.name, { [k]: e.target.value })} />
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </Modal>
  );
}
