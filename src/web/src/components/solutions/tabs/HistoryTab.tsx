"use client";

import { Badge, Card, CardHeader } from "@/components/ui";
import type { SolutionView } from "@/lib/api";

/** The History tab: every install, upgrade and redeploy. */
export function HistoryTab({ solution }: { solution: SolutionView }) {
  return (
    <Card>
      <CardHeader title="History" description="Every install, upgrade and redeploy of this solution." />
      <ol className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {[...solution.history].reverse().map((h, i) => (
          <li key={i} className="flex items-center gap-3 px-5 py-3 text-sm">
            <Badge tone={h.action === "install" ? "green" : h.action.startsWith("uninstall") ? "red" : "blue"}>{h.action}</Badge>
            <span className="font-mono text-zinc-800 dark:text-zinc-200">{h.from ? `${h.from} → ${h.version}` : h.version}</span>
            <span className="ml-auto text-xs text-zinc-500">{h.by ? `${h.by} · ` : ""}{new Date(h.at).toLocaleString()}</span>
          </li>
        ))}
      </ol>
    </Card>
  );
}
