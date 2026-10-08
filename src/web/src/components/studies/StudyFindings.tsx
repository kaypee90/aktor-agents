"use client";

import type { StudyDetail, StudyModel, StudySimulation } from "@/lib/api";
import { Markdown } from "@/components/files/Markdown";
import { Badge, Card, CardHeader, EmptyState, ago, cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { EvidenceChip, ROLE_LABEL, num, pct } from "./StudyEvidence";

const METHOD_LABEL: Record<string, string> = { linear_regression: "Linear regression", logistic_regression: "Logistic regression", arima: "ARIMA" };
const METRIC_LABEL: Record<string, string> = {
  r2: "R²", adj_r2: "Adj. R²", rmse: "RMSE", mae: "MAE", mape: "MAPE %", aic: "AIC", bic: "BIC", auc: "AUC", accuracy: "Accuracy",
  pseudo_r2: "Pseudo R²", log_loss: "Log loss", brier: "Brier", positive_rate: "Positive rate", f_pvalue: "F p-value", llr_pvalue: "LLR p-value",
};

/** The study's report: the answer, findings with their evidence, and how every source was used. */
export function StudyReport({ study }: { study: StudyDetail }) {
  const report = study.report?.content;
  if (!report) {
    return <EmptyState icon={<Icons.Book className="h-5 w-5" />} title="No report yet"
      description="When a run finishes, its report appears here: findings with the evidence behind them, the models and their holdout scores, limitations and how every source was used." />;
  }

  const modelById = new Map(study.models.map((m) => [m.model_id, m]));
  return (
    <div className="space-y-5">
      <Card className="p-5">
        <div className="mb-2 flex flex-wrap items-center gap-2 text-xs text-zinc-500">
          <span>Report of run <span className="font-mono">{study.report!.run_id.slice(0, 13)}</span> · {ago(study.report!.created_at)}</span>
          <span>· checked by the runtime</span>
        </div>
        <Markdown text={report.summary} />
        {report.warnings.length > 0 && (
          <ul className="mt-3 space-y-1 text-xs text-amber-700 dark:text-amber-400">{report.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
        )}
      </Card>

      <Card>
        <CardHeader title="Findings" description="Each finding cites its evidence; click an id to see what was run and what came back. Without evidence, a finding is shown as interpretation." />
        <ol className="divide-y divide-zinc-100 dark:divide-zinc-800">
          {report.findings.map((f, i) => (
            <li key={i} className="space-y-2 px-5 py-4">
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge tone={f.status === "supported" ? "green" : "neutral"}>{f.status === "supported" ? "Supported" : "Interpretation"}</Badge>
                <Badge tone={f.confidence === "high" ? "green" : f.confidence === "medium" ? "blue" : "neutral"}>{f.confidence} confidence</Badge>
                {f.simulated && <Badge tone="amber">Simulated: directional</Badge>}
              </div>
              <p className="text-sm text-zinc-800 dark:text-zinc-200">{f.claim}</p>
              {(f.evidence.length > 0 || (f.models?.length ?? 0) > 0) && (
                <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-zinc-500">
                  {f.evidence.map((e) => <EvidenceChip key={e} id={e} />)}
                  {f.models?.map((m) => {
                    const model = modelById.get(m);
                    return <Badge key={m} tone="brand">{model ? `${METHOD_LABEL[model.method]} of ${model.target}` : m}</Badge>;
                  })}
                </div>
              )}
            </li>
          ))}
        </ol>
      </Card>

      <div className="grid gap-5 lg:grid-cols-2">
        <ListCard title="Limitations" items={report.limitations} empty="None listed." />
        <ListCard title="Open questions" items={report.open_questions ?? []} empty="None listed." />
      </div>
      {(report.recommendations?.length ?? 0) > 0 && <ListCard title="Recommendations" items={report.recommendations!} empty="" />}

      <Card>
        <CardHeader title="Data coverage" description="Every source, what it was used for, and how much evidence came from it." />
        <table className="w-full text-sm">
          <thead className="text-left text-[10px] uppercase tracking-wide text-zinc-500">
            <tr><th className="px-5 py-2 font-medium">Source</th><th className="px-3 py-2 font-medium">Role</th><th className="px-3 py-2 font-medium">Why</th><th className="px-5 py-2 text-right font-medium">Evidence</th></tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {report.data_coverage.map((c) => (
              <tr key={c.source}>
                <td className="px-5 py-2 font-mono text-xs">{c.source}</td>
                <td className="px-3 py-2"><Badge tone={c.role === "not_relevant" ? "neutral" : "blue"}>{ROLE_LABEL[c.role] ?? c.role}</Badge></td>
                <td className="px-3 py-2 text-xs text-zinc-500">{c.reason ?? ""}</td>
                <td className={cx("px-5 py-2 text-right tabular-nums", c.evidence_count === 0 && c.role !== "not_relevant" && "text-amber-600")}>{c.evidence_count}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
    </div>
  );
}

function ListCard({ title, items, empty }: { title: string; items: string[]; empty: string }) {
  return (
    <Card>
      <CardHeader title={title} />
      {items.length === 0 ? <p className="px-5 py-4 text-sm text-zinc-500">{empty}</p> : (
        <ul className="list-disc space-y-1.5 py-4 pl-10 pr-5 text-sm text-zinc-700 dark:text-zinc-300">{items.map((x) => <li key={x}>{x}</li>)}</ul>
      )}
    </Card>
  );
}

/** The model registry: candidates, reviews, fits and holdout scores, and the hypotheses they test. */
export function StudyModels({ study }: { study: StudyDetail }) {
  if (study.models.length === 0 && study.hypotheses.length === 0) {
    return <EmptyState icon={<Icons.Analytics className="h-5 w-5" />} title="No models yet"
      description="Models the agents fit (linear and logistic regression, ARIMA) appear here with their coefficients, diagnostics, reviews and holdout scores." />;
  }

  return (
    <div className="space-y-5">
      {study.hypotheses.length > 0 && (
        <Card>
          <CardHeader title="Hypotheses" description="Recorded before the models that test them were fitted." />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {study.hypotheses.map((h) => (
              <li key={h.hypothesis_id} className="px-5 py-3 text-sm">
                <span className="mr-2 font-mono text-[11px] text-zinc-400">{h.hypothesis_id}</span>{h.statement}
                {h.rationale && <span className="block text-xs text-zinc-500">{h.rationale}</span>}
              </li>
            ))}
          </ul>
        </Card>
      )}
      {study.models.map((m) => <ModelCard key={m.model_id} model={m} />)}
    </div>
  );
}

function ModelCard({ model: m }: { model: StudyModel }) {
  const fit = m.result;
  const odds = m.method === "logistic_regression";
  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-zinc-100 px-5 py-4 dark:border-zinc-800">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold text-zinc-900 dark:text-zinc-100">{METHOD_LABEL[m.method] ?? m.method} of <span className="font-mono">{m.target}</span></span>
            <Badge tone={m.status === "accepted" ? "green" : m.status === "rejected" ? "red" : "amber"}>{m.status}</Badge>
          </div>
          <div className="mt-0.5 text-xs text-zinc-500">
            on <span className="font-mono">{m.dataset}</span> v{m.dataset_version}
            {m.features.length > 0 && <> · features {m.features.join(", ")}</>} · n = {fit.n?.toLocaleString()} · by <span className="font-mono">{m.author}</span>
          </div>
        </div>
        <div className="flex items-center gap-1.5"><EvidenceChip id={m.evidence_id} />{m.holdout_evidence_id && <EvidenceChip id={m.holdout_evidence_id} />}</div>
      </div>

      <div className="grid gap-5 p-5 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[480px] text-xs">
              <thead className="text-left text-[10px] uppercase tracking-wide text-zinc-500">
                <tr>
                  <th className="py-1.5 pr-3 font-medium">Term</th>
                  <th className="py-1.5 pr-3 text-right font-medium">{odds ? "Odds ratio" : "Coefficient"}</th>
                  <th className="py-1.5 pr-3 text-right font-medium">95% interval</th>
                  <th className="py-1.5 text-right font-medium">p-value</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
                {fit.coefficients.map((c) => (
                  <tr key={c.term}>
                    <td className="py-1.5 pr-3 font-mono">{c.term}</td>
                    <td className="py-1.5 pr-3 text-right tabular-nums">{num(odds ? c.odds_ratio : c.coef)}</td>
                    <td className="py-1.5 pr-3 text-right tabular-nums text-zinc-500">
                      {c.ci_low != null && c.ci_high != null ? `${num(odds ? Math.exp(c.ci_low) : c.ci_low)} – ${num(odds ? Math.exp(c.ci_high) : c.ci_high)}` : "—"}
                    </td>
                    <td className={cx("py-1.5 text-right tabular-nums", (c.p_value ?? 1) < 0.05 ? "font-medium text-zinc-900 dark:text-zinc-100" : "text-zinc-500")}>{num(c.p_value)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {fit.forecast && fit.forecast.length > 0 && (
            <div className="mt-4 text-xs">
              <div className="mb-1 font-medium text-zinc-700 dark:text-zinc-300">Forecast (95% interval)</div>
              <div className="flex flex-wrap gap-1.5">
                {fit.forecast.map((f) => <span key={f.step} className="rounded-md bg-zinc-100 px-2 py-1 tabular-nums dark:bg-zinc-800">+{f.step}: {num(f.mean)} <span className="text-zinc-500">({num(f.ci_low)} – {num(f.ci_high)})</span></span>)}
              </div>
            </div>
          )}
        </div>
        <div className="space-y-3 text-xs">
          <Metrics title="Fit (training data)" metrics={fit.metrics} />
          {fit.cross_validation && <Metrics title={`Cross-validation (${fit.cross_validation.folds} folds)`} metrics={Object.fromEntries(Object.entries(fit.cross_validation).filter(([k]) => k !== "folds"))} />}
          {fit.backtest && <Metrics title={`Backtest (last ${fit.backtest.holdout_steps})`} metrics={{ rmse: fit.backtest.rmse, mape: fit.backtest.mape }} />}
          {m.holdout ? <Metrics title={`Sealed holdout (n = ${m.holdout.n})`} metrics={m.holdout.metrics} highlight />
            : <p className="text-zinc-500">{m.status === "accepted" ? "Not scored on the holdout yet." : "Scored on the holdout once accepted."}</p>}
        </div>
      </div>

      {((fit.warnings?.length ?? 0) > 0 || m.review_notes) && (
        <div className="space-y-2 border-t border-zinc-100 px-5 py-3 text-xs dark:border-zinc-800">
          {fit.warnings?.map((w) => <p key={w} className="text-amber-700 dark:text-amber-400">⚠ {w}</p>)}
          {m.review_notes && (
            <p className="text-zinc-600 dark:text-zinc-400">
              <span className="font-medium text-zinc-800 dark:text-zinc-200">Review by <span className="font-mono">{m.reviewer}</span>:</span> {m.review_notes}
            </p>
          )}
        </div>
      )}
    </Card>
  );
}

function Metrics({ title, metrics, highlight }: { title: string; metrics: Record<string, number | null>; highlight?: boolean }) {
  const entries = Object.entries(metrics).filter(([, v]) => v !== null && v !== undefined);
  return (
    <div className={cx("rounded-lg p-3", highlight ? "bg-emerald-50 dark:bg-emerald-950/30" : "bg-zinc-50 dark:bg-zinc-900")}>
      <div className="mb-1.5 font-medium text-zinc-700 dark:text-zinc-300">{title}</div>
      <dl className="grid grid-cols-2 gap-x-3 gap-y-1">
        {entries.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-zinc-500">{METRIC_LABEL[k] ?? k.replace(/_/g, " ")}</dt>
            <dd className="text-right tabular-nums text-zinc-900 dark:text-zinc-100">{num(v)}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

/** Simulated experiments: choice shares per condition, effects against the control, calibration. */
export function StudyExperiments({ study }: { study: StudyDetail }) {
  if (study.simulations.length === 0) {
    return <EmptyState icon={<Icons.Simulation className="h-5 w-5" />} title="No experiments yet"
      description="When agents run a simulation, a population built from your data reacts to the scenario under control and treatment conditions. Every decision becomes a dataset, and the results show here." />;
  }

  return (
    <div className="space-y-5">
      <p className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-2.5 text-xs text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
        Simulated people are not real people: they tend to be more alike and more agreeable. Treat these results as directional, and trust them more when the calibration gap is small.
      </p>
      {study.simulations.map((s) => <SimulationCard key={s.simulation_id} simulation={s} />)}
    </div>
  );
}

const PALETTE = ["#6366f1", "#14b8a6", "#f59e0b", "#ec4899", "#0ea5e9", "#84cc16", "#a855f7", "#ef4444"];

function SimulationCard({ simulation: s }: { simulation: StudySimulation }) {
  const summary = s.summary;
  const lastRound = Math.max(1, ...summary.shares.map((x) => x.round));
  const final = summary.shares.filter((x) => x.round === lastRound);
  const options = Array.from(new Set(final.flatMap((x) => Object.keys(x.shares))));
  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-zinc-100 px-5 py-4 dark:border-zinc-800">
        <div>
          <div className="font-semibold text-zinc-900 dark:text-zinc-100">{s.name}</div>
          <div className="mt-0.5 text-xs text-zinc-500">
            {s.participants} participants · {s.decisions} decisions · ${s.cost_usd.toFixed(2)} · dataset <span className="font-mono">{s.dataset}</span> · {ago(s.created_at)}
          </div>
        </div>
        <EvidenceChip id={s.evidence_id} />
      </div>
      <div className="space-y-4 p-5">
        <div className="space-y-2">
          {final.map((row) => (
            <div key={row.condition} className="flex items-center gap-3 text-xs">
              <span className="w-32 shrink-0 truncate font-medium text-zinc-700 dark:text-zinc-300" title={row.condition}>{row.condition}</span>
              <div className="flex h-5 flex-1 overflow-hidden rounded-md bg-zinc-100 dark:bg-zinc-800">
                {options.map((o, i) => (row.shares[o] ?? 0) > 0 && (
                  <div key={o} style={{ width: `${row.shares[o] * 100}%`, background: PALETTE[i % PALETTE.length] }}
                    className="flex items-center justify-center text-[10px] font-medium text-white" title={`${o}: ${pct(row.shares[o])}`}>
                    {row.shares[o] >= 0.12 ? pct(row.shares[o]) : ""}
                  </div>
                ))}
              </div>
              <span className="w-10 text-right tabular-nums text-zinc-500">n={row.n}</span>
            </div>
          ))}
          <div className="flex flex-wrap gap-3 pl-[8.75rem] text-[11px] text-zinc-500">
            {options.map((o, i) => <span key={o} className="inline-flex items-center gap-1"><span className="h-2 w-2 rounded-sm" style={{ background: PALETTE[i % PALETTE.length] }} />{o}</span>)}
          </div>
        </div>

        {summary.effects.length > 0 && (
          <div className="text-xs">
            <div className="mb-1 font-medium text-zinc-700 dark:text-zinc-300">Effect against {summary.effects[0].versus}{lastRound > 1 ? ` (round ${lastRound})` : ""}</div>
            {summary.effects.map((e) => (
              <div key={e.condition} className="text-zinc-600 dark:text-zinc-400">
                <span className="font-medium">{e.condition}:</span>{" "}
                {Object.entries(e.difference).map(([o, d]) => `${o} ${d >= 0 ? "+" : ""}${(d * 100).toFixed(0)} pts`).join(" · ")}
              </div>
            ))}
          </div>
        )}

        {summary.calibration && (
          <div className={cx("rounded-lg px-3 py-2 text-xs", summary.calibration.calibrated ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300" : "bg-amber-50 text-amber-800 dark:bg-amber-950/30 dark:text-amber-300")}>
            <span className="font-medium">Calibration:</span> under the control, {pct(summary.calibration.simulated_rate)} chose &ldquo;{summary.calibration.option}&rdquo;
            against {pct(summary.calibration.real_rate)} in the real data (gap {(summary.calibration.gap * 100).toFixed(0)} pts).
            {summary.calibration.evidence_id && <> <EvidenceChip id={summary.calibration.evidence_id} /></>}
          </div>
        )}
        {summary.warnings.map((w) => <p key={w} className="text-xs text-amber-700 dark:text-amber-400">⚠ {w}</p>)}
        {(summary.unanswered > 0 || summary.stopped_for_cost) && (
          <p className="text-xs text-zinc-500">{summary.unanswered > 0 && `${summary.unanswered} participant answer(s) were unusable. `}{summary.stopped_for_cost && "Stopped early at the cost limit."}</p>
        )}
      </div>
    </Card>
  );
}
