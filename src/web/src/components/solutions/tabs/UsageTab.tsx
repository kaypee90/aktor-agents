"use client";

import { useEffect, useState } from "react";
import { apiErrorMessage, getSolutionUsage, solutionUsageCsvUrl, type SolutionUsage, type SolutionView } from "@/lib/api";
import { Card, CardHeader, ErrorBanner, Stat, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** The Usage tab: cost by end user, workspace or day, from the runtime's own metering. */
export function UsageTab({ solution }: { solution: SolutionView }) {
  const [by, setBy] = useState<SolutionUsage["by"]>("end_user");
  const [usage, setUsage] = useState<SolutionUsage | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    getSolutionUsage(solution.install_id, by).then((u) => { setUsage(u); setError(null); }).catch((e) => setError(apiErrorMessage(e)));
  }, [solution.install_id, by]);

  return (
    <div className="space-y-4">
      <ErrorBanner error={error} />
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Cost this month" value={usage ? `$${usage.cost_usd.toFixed(4)}` : "—"} />
        <Stat label="Tokens" value={usage ? usage.tokens.toLocaleString() : "—"} />
        <Stat label="Conversations" value={usage?.conversations ?? "—"} hint="end users' chats" />
        <Stat label="Runs" value={usage?.runs ?? "—"} hint="workspace pipelines" />
      </div>
      <Card>
        <CardHeader
          title="Usage"
          description="What the solution cost, from the runtime's own metering, so you can pass it on to your customers. Conversations count against the end user who had them; workspace runs against no one."
          actions={
            <>
              <select className={cx(inputClass, "h-8 w-40 py-1 text-xs")} value={by} onChange={(e) => setBy(e.target.value as SolutionUsage["by"])} aria-label="Group by">
                <option value="end_user">By end user</option>
                <option value="workspace">By workspace</option>
                <option value="day">By day</option>
              </select>
              <a href={solutionUsageCsvUrl(solution.install_id, by)} className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-zinc-200 px-2.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-800">
                <Icons.Download className="h-3.5 w-3.5" /> CSV
              </a>
            </>
          }
        />
        <table className="w-full text-left text-xs">
          <thead className="border-b border-zinc-200 text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr><th className="px-4 py-2 font-medium">{by.replace("_", " ")}</th><th className="px-4 py-2 font-medium">conversations</th><th className="px-4 py-2 font-medium">runs</th><th className="px-4 py-2 font-medium">tokens</th><th className="px-4 py-2 text-right font-medium">cost</th></tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {usage?.lines.map((l) => (
              <tr key={l.key}>
                <td className="px-4 py-2 font-mono text-zinc-800 dark:text-zinc-200">{l.key}</td>
                <td className="px-4 py-2 tabular-nums">{l.conversations}</td>
                <td className="px-4 py-2 tabular-nums">{l.runs}</td>
                <td className="px-4 py-2 tabular-nums">{l.tokens.toLocaleString()}</td>
                <td className="px-4 py-2 text-right tabular-nums">${l.cost_usd.toFixed(4)}</td>
              </tr>
            ))}
            {usage && usage.lines.length === 0 && <tr><td colSpan={5} className="px-4 py-6 text-center text-zinc-500">No usage in this period yet.</td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  );
}
