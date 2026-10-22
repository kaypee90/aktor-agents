"use client";

import Link from "next/link";
import { useState } from "react";
import { apiErrorMessage, runSolutionEvals, type EvalReport, type SolutionView } from "@/lib/api";
import { Button, Card, CardHeader, EmptyState, ErrorBanner } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** The Evals tab: run the solution's expected behaviours for real and see what failed. */
export function EvalsTab({ solution, canRun }: { solution: SolutionView; canRun: boolean }) {
  const [report, setReport] = useState<EvalReport | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setRunning(true);
    setError(null);
    try {
      setReport(await runSolutionEvals(solution.install_id));
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setRunning(false);
    }
  }

  if (solution.evals.length === 0) {
    return <EmptyState title="No evals" description="Evals are expected behaviours (a run's status, its summary, the records it writes) checked by running the workspace for real. Add them in the builder under Evals." />;
  }

  return (
    <Card>
      <CardHeader
        title="Evals"
        description="Each one starts a real run with its input and checks the outcome. They take as long as the runs do and spend like any run."
        actions={canRun && <Button variant="primary" icon={<Icons.Play className="h-3.5 w-3.5" />} disabled={running} onClick={run}>{running ? "Running…" : "Run all"}</Button>}
      />
      <ErrorBanner error={error} />
      <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {solution.evals.map((e) => {
          const r = report?.results.find((x) => x.name === e.name);
          return (
            <li key={e.name} className="px-5 py-3">
              <div className="flex items-center gap-3">
                {r ? (r.passed ? <Icons.Check className="h-4 w-4 text-emerald-500" /> : <Icons.X className="h-4 w-4 text-rose-500" />) : <span className="h-4 w-4 rounded-full border border-zinc-300 dark:border-zinc-700" />}
                <span className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{e.name}</span>
                <span className="font-mono text-[11px] text-zinc-500">{e.workspace}</span>
                {r?.duration_ms != null && <span className="ml-auto text-xs text-zinc-500">{(r.duration_ms / 1000).toFixed(1)}s</span>}
                {r?.run_id && <Link href={`/?task=${encodeURIComponent(r.run_id)}`} className="text-xs text-brand-600 hover:underline dark:text-brand-400">run</Link>}
              </div>
              {r && !r.passed && <ul className="ml-7 mt-1 list-disc space-y-0.5 pl-4 text-xs text-rose-600 dark:text-rose-400">{r.failures.map((f) => <li key={f}>{f}</li>)}</ul>}
            </li>
          );
        })}
      </ul>
      {report && (
        <div className="border-t border-zinc-200 px-5 py-3 text-sm dark:border-zinc-800">
          {report.failed === 0 ? <span className="text-emerald-600 dark:text-emerald-400">All {report.passed} passed.</span> : <span className="text-rose-600 dark:text-rose-400">{report.failed} failed, {report.passed} passed.</span>}
        </div>
      )}
    </Card>
  );
}
